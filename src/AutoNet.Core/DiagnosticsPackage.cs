using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoNet.Core;

/// <summary>
/// Sanitised settings view for the diagnostics package. The allowlist is deliberate: proxy and UU
/// probe addresses describe the user's own tunnelling setup, are not needed to diagnose the campus
/// network, and are therefore dropped rather than "kept because Settings has them".
/// <see cref="Settings.PortalOrigin"/> passed the red-line checklist: <see cref="Settings.Validate"/>
/// pins it to the fixed https://login.csust.edu.cn:443 root, so it carries no credential and names
/// the diagnostic target a network admin needs.
/// </summary>
public sealed record SettingsSnapshot(bool Enabled, bool AllowActiveDisconnect, string InterfaceId, string WifiProfile, string PortalOrigin, bool KeepAwakeOnAc);

/// <summary>Read-only observations gathered at export time. A null member means "not collected" and is explained in the package.</summary>
public sealed record LiveDiagnostics(NetworkDiagnosticReport? Report, PortalAttempt? Portal, string? CollectionError = null);

/// <summary>
/// Injection seams for <see cref="DiagnosticsPackage.ExportAsync"/>. Every default is the production
/// boundary (Storage, WLAN, HTTP); tests substitute offline doubles so the contract is verifiable
/// without touching the real machine.
/// </summary>
public sealed record DiagnosticsPackageOptions
{
    /// <summary>Zip destination. Null or a directory gets the default file name; a file path is used as-is.</summary>
    public string? OutputPath { get; init; }
    /// <summary>Daily JSONL log directory; defaults to <see cref="Storage.Logs"/>.</summary>
    public string? LogDirectory { get; init; }
    /// <summary>Service status snapshot file; defaults to <see cref="Storage.StatusPath"/>.</summary>
    public string? StatusPath { get; init; }
    /// <summary>Stored settings source; defaults to <see cref="Storage.LoadSettings"/>.</summary>
    public Func<Settings?>? LoadSettings { get; init; }
    /// <summary>Saved Wi-Fi profiles used when no usable stored settings exist; defaults to <see cref="Wifi.Profiles"/>.</summary>
    public Func<IReadOnlyList<WifiProfile>>? Profiles { get; init; }
    /// <summary>Live read-only collection; defaults to the real Wi-Fi/probe/portal path.</summary>
    public Func<Settings, CancellationToken, Task<LiveDiagnostics>>? Collect { get; init; }
    /// <summary>Export moment; defaults to now. Injected so log windows and names stay deterministic offline.</summary>
    public DateTimeOffset? ExportedAt { get; init; }
    /// <summary>Build identifier recorded in README; defaults to this assembly's informational version.</summary>
    public string? BuildId { get; init; }
    /// <summary>Upper bound for the whole live collection; each probe keeps its own shorter internal deadline.</summary>
    public TimeSpan CollectionTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>
    /// How the export was started, recorded verbatim in the README's 导出方式 line. Defaults to the
    /// service CLI; the tray's elevated export names its own path so the package honestly says where
    /// the collection ran.
    /// </summary>
    public string? SourceLabel { get; init; }
}

public sealed record DiagnosticsPackageResult(string Path, IReadOnlyList<string> Entries, IReadOnlyList<string> Notes);

/// <summary>
/// Builds the diagnostics zip defined by ADR-0003: recent logs, the live read-only network report,
/// the portal read-only inspection, the current status snapshot, a sanitised settings view and a
/// README. One implementation in Core so the service CLI and a future tray menu can never carry two
/// different redaction logics. The package proves what was seen at collection time; it never proves
/// recovery performance, and it never contains credentials, cookies, tokens or a real MAC.
/// </summary>
public static class DiagnosticsPackage
{
    /// <summary>Daily log files newer than this many days are packaged; older evidence stays on disk.</summary>
    public const int LogWindowDays = 3;

    internal static readonly JsonSerializerOptions PackageJson = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<DiagnosticsPackageResult> ExportAsync(DiagnosticsPackageOptions options, CancellationToken ct = default)
    {
        var exportedAt = options.ExportedAt ?? DateTimeOffset.Now;
        var buildId = options.BuildId ?? typeof(DiagnosticsPackage).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var notes = new List<string>();

        var settings = LoadUsableSettings(options, notes);
        NetworkDiagnosticReport? report = null;
        PortalAttempt? portal = null;
        if (settings is null) notes.Add("未找到可用设置，未进行实时采集；包内只含既有日志与状态文件。");
        else
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(options.CollectionTimeout);
                var live = options.Collect is null
                    ? await CollectLiveAsync(settings, timeout.Token)
                    : await options.Collect(settings, timeout.Token);
                report = live.Report; portal = live.Portal;
                if (portal is null && live.CollectionError is not null)
                    notes.Add("门户只读诊断失败（" + live.CollectionError + "），包内不含 portal_diagnostic.json。");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { notes.Add("实时采集超时；包内只含既有日志与状态文件。"); }
            catch (Exception e) when (e is not OperationCanceledException) { notes.Add("实时采集失败（" + e.GetType().Name + "）；包内只含既有日志与状态文件。"); }
            if (report is null && !notes.Any(n => n.Contains("net_state.json", StringComparison.Ordinal)))
                notes.Add("实时网络状态未采集，包内不含 net_state.json。");
            if (portal is null && !notes.Any(n => n.Contains("portal_diagnostic.json", StringComparison.Ordinal)))
                notes.Add("门户只读诊断未采集，包内不含 portal_diagnostic.json。");
        }

        var statusJson = ReadStatus(options, notes);
        var logs = ReadLogs(options, exportedAt, notes);

        // All content is materialised before the zip is opened so a mid-package read failure is
        // reported in the README instead of leaving a truncated archive behind.
        var logContents = new List<(string EntryName, byte[] Content)>();
        foreach (var (entryName, path) in logs)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                file.CopyTo(buffer);
                logContents.Add((entryName, buffer.ToArray()));
            }
            catch (Exception e) when (IsStorageFailure(e)) { notes.Add("日志文件读取失败，未包含 " + entryName + "（" + e.GetType().Name + "）。"); }
        }

        var entries = new List<string> { "README.txt" };
        var snapshot = Snapshot(settings);
        if (snapshot is not null) entries.Add("settings-snapshot.json");
        if (report is not null) entries.Add("net_state.json");
        if (portal is not null) entries.Add("portal_diagnostic.json");
        if (statusJson is not null) entries.Add("status.json");
        entries.AddRange(logContents.Select(l => l.EntryName));

        var zipPath = ResolveOutputPath(options.OutputPath, DesktopDirectory(), exportedAt);
        var parent = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        var sourceLabel = options.SourceLabel ?? "AutoNet.Service --export-diagnostics";
        // Safe publish (UI-MIGRATION-SPEC 决策 13 / 故事 79): the archive is written at a private
        // temp name inside the destination directory and only then replaces the target. A failure
        // or an accepted cancellation before publish keeps the previous package byte-identical and
        // never leaves a half archive at the user-chosen path.
        var temporary = Path.Combine(
            string.IsNullOrEmpty(parent) ? "." : parent,
            Path.GetFileNameWithoutExtension(zipPath) + ".autonet-part-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // An exclusive new temp stream cannot collide (GUID name) and cannot truncate anything.
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                // README carries a BOM so Notepad never misreads the Chinese text; the JSON entries
                // follow Storage's BOM-less convention instead.
                WriteText(zip, "README.txt", Readme(exportedAt, buildId, sourceLabel, entries, notes), bom: true);
                if (snapshot is not null)
                    WriteText(zip, "settings-snapshot.json", JsonSerializer.Serialize(snapshot, PackageJson));
                if (report is not null)
                    WriteText(zip, "net_state.json", JsonSerializer.Serialize(report, PackageJson));
                if (portal is not null)
                    WriteText(zip, "portal_diagnostic.json", JsonSerializer.Serialize(portal, PackageJson));
                if (statusJson is not null) WriteText(zip, "status.json", statusJson);
                foreach (var (entryName, content) in logContents)
                {
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(content);
                }
            }

            // Once the archive is complete it is published even if cancellation arrived meanwhile:
            // pretending a finished export was cancelled would hide a good package (决策 13).
            File.Move(temporary, zipPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
        return new(zipPath, entries, notes);
    }

    /// <summary>Allowlist projection; a null settings yields no snapshot rather than a partial one.</summary>
    internal static SettingsSnapshot? Snapshot(Settings? settings) => settings is null ? null :
        new(settings.Enabled, settings.AllowActiveDisconnect, settings.InterfaceId, settings.WifiProfile, settings.PortalOrigin, settings.KeepAwakeOnAc);

    /// <summary>
    /// Daily logs inside the <see cref="LogWindowDays"/>-day window ending today, oldest first.
    /// The file name date is the evidence date: an idle machine contributes no stale logs, and a
    /// malformed name is skipped rather than guessed at.
    /// </summary>
    internal static IReadOnlyList<(string EntryName, string Path)> SelectLogs(string directory, DateTime today)
    {
        if (!Directory.Exists(directory)) return [];
        var cutoff = today.Date.AddDays(1 - LogWindowDays);
        var selected = new List<(string EntryName, string Path)>();
        foreach (var path in Directory.EnumerateFiles(directory, "autonet-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!name.StartsWith("autonet-", StringComparison.Ordinal)) continue;
            var date = name["autonet-".Length..];
            if (date.Length != 10 || !DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) continue;
            if (parsed < cutoff) continue;
            selected.Add(("logs/" + Path.GetFileName(path), path));
        }
        return selected.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Null or blank means the default name on the desktop; an existing directory (or a trailing separator) receives the default name inside it.</summary>
    internal static string ResolveOutputPath(string? requested, string desktop, DateTimeOffset now)
    {
        var fallback = Path.Combine(desktop, $"autonet-diagnostics-{now.ToLocalTime():yyyyMMdd-HHmmss}.zip");
        if (string.IsNullOrWhiteSpace(requested)) return fallback;
        var trimmed = requested.Trim();
        return Directory.Exists(trimmed) || trimmed.EndsWith('/') || trimmed.EndsWith('\\')
            ? Path.Combine(trimmed, Path.GetFileName(fallback)) : trimmed;
    }

    internal static string Readme(DateTimeOffset exportedAt, string buildId, string sourceLabel, IReadOnlyList<string> entries, IReadOnlyList<string> notes)
    {
        var text = new StringBuilder();
        text.AppendLine("AutoNet 诊断包");
        text.AppendLine("=============");
        text.AppendLine();
        text.AppendLine("导出时间：" + exportedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + "（本机时间）");
        text.AppendLine("程序构建：" + buildId);
        text.AppendLine("导出方式：" + sourceLabel + "（只读；未修改网络、未加载凭据、未清除认证阻断）");
        text.AppendLine();
        text.AppendLine("内容清单");
        text.AppendLine("--------");
        foreach (var entry in entries) text.AppendLine("- " + entry + "：" + Describe(entry));
        text.AppendLine();
        text.AppendLine("阅读指引");
        text.AppendLine("--------");
        text.AppendLine("- 日志时间为墙钟口径；MonotonicMilliseconds 为单调计时，两者不可直接互推。");
        text.AppendLine("- RunId / IncidentId / Sequence 用于跨记录关联；NetworkId 为网卡与配置名的哈希，不是网卡标识。");
        text.AppendLine("- “恢复来源无法确认”表示外网检查恢复但无法归因于程序动作，不等于程序修复成功。");
        text.AppendLine("- 出现“日志证据缺口”表示有事件未能写入，不能假设期间没有故障或动作。");
        text.AppendLine("- 本包证明“采集时看到了什么”，不证明恢复性能；恢复速度目标需按现场验收判断。");
        text.AppendLine();
        text.AppendLine("缺失项");
        text.AppendLine("------");
        if (notes.Count == 0) text.AppendLine("- 无；以上内容均已包含。");
        else foreach (var note in notes) text.AppendLine("- " + note);
        text.AppendLine();
        text.AppendLine("隐私说明");
        text.AppendLine("--------");
        text.AppendLine("本包按契约不包含：校园网账号、密码、Cookie、Token；网卡 MAC 仅以存在性表示。");
        return text.ToString();
    }

    private static string Describe(string entry) => entry switch
    {
        "README.txt" => "本说明（内容清单、导出时间、阅读指引）。",
        "settings-snapshot.json" => "设置脱敏快照：不含凭据，不含代理与 UU 检测地址；保留网卡与 Wi-Fi 配置名。",
        "net_state.json" => "导出时的网络状态只读报告：Wi-Fi 链路、本机 IPv4、DNS、外网探测、门户状态、路由样本。",
        "portal_diagnostic.json" => "门户只读诊断：阶段、失败分类、HTTP 状态、参数存在性；未发送凭据。",
        "status.json" => "后台服务最近一次发布的状态快照原文。",
        var logs when logs.StartsWith("logs/", StringComparison.Ordinal) => "结构化日志（JSONL，schemaVersion 2），按天一个文件。",
        _ => entry
    };

    private static Settings? LoadUsableSettings(DiagnosticsPackageOptions options, List<string> notes)
    {
        Settings? settings = null;
        try { settings = options.LoadSettings?.Invoke() ?? Storage.LoadSettings(); }
        catch (Exception e) when (IsStorageFailure(e) || e is JsonException) { notes.Add("已保存设置读取失败（" + e.GetType().Name + "）；未加载凭据。"); }
        if (settings is not null)
        {
            try { settings.Validate(); return settings; }
            catch (ArgumentException) { notes.Add("已保存设置未通过校验，未用于实时采集。"); }
        }
        // Without stored settings the service monitors nothing; the connected all-users profile is
        // the only honest live view, and the package says so instead of implying a configured target.
        try
        {
            var profile = (options.Profiles?.Invoke() ?? Wifi.Profiles()).FirstOrDefault(p => p.Connected && p.AllUsers);
            if (profile is null) return null;
            notes.Add("未找到可用的已保存设置；已按当前连接的所有用户 Wi-Fi 配置进行只读采集。");
            return new Settings { InterfaceId = profile.InterfaceId, WifiProfile = profile.Name };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            notes.Add("当前 Wi-Fi 配置枚举失败（" + e.GetType().Name + "）。");
            return null;
        }
    }

    private static string? ReadStatus(DiagnosticsPackageOptions options, List<string> notes)
    {
        try { return Storage.ReadShared(options.StatusPath ?? Storage.StatusPath); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            notes.Add("status.json 不存在；后台服务可能尚未发布状态。");
            return null;
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            notes.Add("status.json 读取失败（" + e.GetType().Name + "）。");
            return null;
        }
    }

    private static IReadOnlyList<(string EntryName, string Path)> ReadLogs(DiagnosticsPackageOptions options, DateTimeOffset exportedAt, List<string> notes)
    {
        try
        {
            var logs = SelectLogs(options.LogDirectory ?? Storage.Logs, exportedAt.ToLocalTime().Date);
            if (logs.Count == 0) notes.Add($"近 {LogWindowDays} 天没有日志文件。");
            return logs;
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            notes.Add("日志目录枚举失败（" + e.GetType().Name + "）。");
            return [];
        }
    }

    private static async Task<LiveDiagnostics> CollectLiveAsync(Settings settings, CancellationToken ct)
    {
        var link = Wifi.Read(settings);
        using var http = link.State == WifiState.Ready ? DirectNetwork.CreateClient(link) : new HttpClient();
        var route = await HealthChecks.ReadRouteAsync(link, ct);
        var report = await NetworkDiagnostics.RunAsync(settings, link, http, route, ct);
        try
        {
            return new(report, await new DrcomPortal(http, new Uri(settings.PortalOrigin)).InspectDetailedAsync(ct));
        }
        catch (OperationCanceledException) { throw; }
        // InspectDetailedAsync classifies its own transport failures; anything escaping it is
        // unexpected. Keep the network report and name the failure instead of dropping both.
        catch (Exception e) { return new(report, null, e.GetType().Name); }
    }

    private static void WriteText(ZipArchive zip, string entryName, string content, bool bom = false)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(bom));
        writer.Write(content);
    }

    private static string DesktopDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrEmpty(desktop) ? AppContext.BaseDirectory : desktop;
    }

    /// <summary>Best-effort temp cleanup after a failed publish; a leftover never masks the original error.</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsStorageFailure(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
