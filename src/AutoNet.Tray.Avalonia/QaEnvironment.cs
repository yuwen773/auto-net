using System.Text.Json;

namespace AutoNet.Tray.Avalonia;

internal enum QaMode
{
    /// <summary>未激活 QA 场景；仅保留“已在运行/提权请求”的报告能力。</summary>
    None,
    Lifecycle,
    Hold,
    ThemeRender,
    /// <summary>壳级 QA：导航截图与状态栏降级证据（scripts/qa-avalonia-shell.ps1 使用）。</summary>
    Shell,
    /// <summary>概览页 QA：状态矩阵投影与动作意图记录（scripts/qa-avalonia-overview.ps1 使用）。</summary>
    Overview,
    /// <summary>解除认证暂停 QA：确认/复核/修订核对/审计的逐场景序列证据（scripts/qa-avalonia-unblock.ps1 使用）。</summary>
    Unblock
}

/// <summary>
/// 离线 QA 开关（scripts/qa-avalonia-prototype.ps1 与 scripts/qa-avalonia-shell.ps1 使用）。
/// 只有显式设置 AUTONET_QA_DIR 时才激活，普通用户机器上永远不生效，与既有 AUTONET_CAPTURE_PATH 的约定一致。
/// </summary>
internal sealed record QaEnvironment(QaMode Mode, string Directory)
{
    public static QaEnvironment FromProcess()
    {
        var directory = Environment.GetEnvironmentVariable("AUTONET_QA_DIR");
        if (string.IsNullOrEmpty(directory)) return new QaEnvironment(QaMode.None, string.Empty);
        var mode = Environment.GetEnvironmentVariable("AUTONET_QA_MODE") switch
        {
            "lifecycle" => QaMode.Lifecycle,
            "hold" => QaMode.Hold,
            "theme" => QaMode.ThemeRender,
            "shell" => QaMode.Shell,
            "overview" => QaMode.Overview,
            "unblock" => QaMode.Unblock,
            _ => QaMode.None
        };
        return new QaEnvironment(mode, Path.GetFullPath(directory));
    }

    public bool Active => Directory.Length != 0;

    public string LifecycleReportPath => Path.Combine(Directory, "lifecycle.json");
    public string ElevationRecordPath => Path.Combine(Directory, "elevation.json");
    public string ElevatedInlineReportPath => Path.Combine(Directory, "elevated-inline.json");
    public string AlreadyRunningReportPath => Path.Combine(Directory, "already-running.json");
    public string ThemeLightPath => Path.Combine(Directory, "theme-light.png");
    public string ThemeDarkPath => Path.Combine(Directory, "theme-dark.png");
    public string ShellReportPath => Path.Combine(Directory, "shell.json");
    public string OverviewReportPath => Path.Combine(Directory, "overview.json");
    public string UnblockReportPath => Path.Combine(Directory, "unblock.json");

    public string ShellShotPath(string name) => Path.Combine(Directory, name + ".png");

    public string OverviewShotPath(string name) => Path.Combine(Directory, name + ".png");

    public string UnblockShotPath(string name) => Path.Combine(Directory, name + ".png");
}

/// <summary>QA 证据写入。仅包含虚构/本机路径信息，不含凭据。</summary>
internal static class QaReports
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>QA 证据统一时间戳（ISO 8601，UTC）。</summary>
    public static string Timestamp() => DateTimeOffset.UtcNow.ToString("o");

    public static void Write(string path, object payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, Options));
    }
}

internal sealed record LifecycleStep(string Step, bool WindowVisible, bool AppAlive, string At);

internal sealed record LifecycleReport(
    string Phase,
    string Mode,
    bool StartHidden,
    string ProcessPath,
    int ProcessId,
    bool Administrator,
    string MutexName,
    string ThemeVariant,
    List<LifecycleStep> Steps,
    string? Error = null);

internal sealed record ShellStep(string Step, string Page, string Name, string Strip, string? File, string At);

internal sealed record ShellReport(
    string Mode,
    bool Fixture,
    string ProcessPath,
    int ProcessId,
    bool Administrator,
    string MutexName,
    string ThemeVariant,
    List<ShellStep> Steps,
    string? Error = null);

/// <summary>概览页一步证据：从真实窗口对应的 <see cref="OverviewContent"/> 投影逐项记录（用户实际看到的内容）。</summary>
internal sealed record QaOverviewStep(
    string State,
    bool Trusted,
    string Title,
    bool AuthVisible,
    string AuthTitle,
    string RecoveryLabel,
    bool RecoveryEnabled,
    bool UnblockVisible,
    bool UnblockEnabled,
    string? File);

/// <summary>一次动作驱动：上下文、明确意图、是否发起、实际记录到的命令。</summary>
internal sealed record QaOverviewAction(string Context, string Intent, bool Launched, string? Command);

internal sealed record QaOverviewReport(
    string Mode,
    bool Fixture,
    string ProcessPath,
    int ProcessId,
    bool Administrator,
    string MutexName,
    string ThemeVariant,
    List<QaOverviewStep> Steps,
    List<QaOverviewAction> Actions,
    string? Error = null);

/// <summary>
/// 解除认证暂停一步证据：场景、实际结果、阶段序列，以及场景前后认证阻断标记与审计行的落盘状态。
/// 只记录虚构夹具目录内的文件名与布尔事实，不含凭据、Cookie 或原始门户响应。
/// </summary>
internal sealed record QaUnblockStep(
    string Scenario,
    string Outcome,
    string Heading,
    bool Released,
    bool RequiresNotice,
    List<string> Stages,
    string? ConfirmAnswer,
    bool BlockFileBefore,
    bool BlockFileAfter,
    bool AuditWritten,
    int AuditLines,
    bool SettingsFileUntouched,
    bool CredentialFileCreated,
    List<string> PortalRequestPaths,
    string Message,
    string? File);

internal sealed record QaUnblockReport(
    string Mode,
    string ProcessPath,
    int ProcessId,
    bool Administrator,
    string MutexName,
    string ThemeVariant,
    string FixtureRoot,
    List<QaUnblockStep> Steps,
    string? Error = null);
