using System.Text.Json;

namespace AutoNet.Tray.Avalonia;

internal enum QaMode
{
    /// <summary>未激活 QA 场景；仅保留“已在运行/提权请求”的报告能力。</summary>
    None,
    Lifecycle,
    Hold,
    ThemeRender
}

/// <summary>
/// 离线 QA 开关（scripts/qa-avalonia-prototype.ps1 使用）。只有显式设置 AUTONET_QA_DIR 时才激活，
/// 普通用户机器上永远不生效，与既有 AUTONET_CAPTURE_PATH 的约定一致。
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
}

/// <summary>QA 证据写入。仅包含虚构/本机路径信息，不含凭据。</summary>
internal static class QaReports
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

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
    string? Error = null)
{
    public static string Timestamp() => DateTimeOffset.UtcNow.ToString("o");
}
