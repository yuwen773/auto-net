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
    Unblock,
    /// <summary>网络诊断页 QA：五类诊断、信号分列与只读边界的逐场景证据（scripts/qa-avalonia-diagnostics.ps1 使用）。</summary>
    Diagnostics,
    /// <summary>日志页 QA：日期入口、受限范围、缺口、快速切换与脱敏复制的逐场景证据（scripts/qa-avalonia-logs.ps1 使用）。</summary>
    Logs,
    /// <summary>设置页 QA：普通界面只读投影、提权入口意图与编辑器保存规则的逐场景证据（scripts/qa-avalonia-settings.ps1 使用）。</summary>
    Settings,
    /// <summary>主题偏好 QA：默认跟随系统、手动明暗、系统变化优先级与跨重启保留的逐场景证据（scripts/qa-avalonia-theme.ps1 使用）。</summary>
    ThemePrefs,
    /// <summary>关键通知 QA：一次提醒、不重播、未知码降级与消费记录持久化的逐场景证据（scripts/qa-avalonia-notifications.ps1 使用）。</summary>
    Notifications
}

/// <summary>
/// 离线 QA 开关（scripts/qa-avalonia-prototype.ps1 与 scripts/qa-avalonia-shell.ps1 使用）。
/// 只有显式设置 AUTONET_QA_DIR 时才激活，普通用户机器上永远不生效，与既有 AUTONET_CAPTURE_PATH 的约定一致。
/// </summary>
internal sealed record QaEnvironment(QaMode Mode, string Directory)
{
    /// <summary>未激活 QA 的环境：偏好存储等路径选择用它回落到真实用户目录。</summary>
    public static QaEnvironment Inactive { get; } = new(QaMode.None, string.Empty);

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
            "diagnostics" => QaMode.Diagnostics,
            "logs" => QaMode.Logs,
            "settings" => QaMode.Settings,
            "theme-prefs" => QaMode.ThemePrefs,
            "notifications" => QaMode.Notifications,
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
    public string DiagnosticsReportPath => Path.Combine(Directory, "diagnostics.json");
    public string LogsReportPath => Path.Combine(Directory, "logs.json");
    public string SettingsReportPath => Path.Combine(Directory, "settings.json");
    public string ThemePrefsReportPath => Path.Combine(Directory, "theme-prefs.json");
    public string ThemePrefsResumeReportPath => Path.Combine(Directory, "theme-prefs-resume.json");
    public string NotificationsReportPath => Path.Combine(Directory, "notifications.json");
    public string NotificationsResumeReportPath => Path.Combine(Directory, "notifications-resume.json");

    public string ShellShotPath(string name) => Path.Combine(Directory, name + ".png");

    public string OverviewShotPath(string name) => Path.Combine(Directory, name + ".png");

    public string UnblockShotPath(string name) => Path.Combine(Directory, name + ".png");

    public string DiagnosticsShotPath(string name) => Path.Combine(Directory, name + ".png");

    public string LogsShotPath(string name) => Path.Combine(Directory, name + ".png");

    public string SettingsShotPath(string name) => Path.Combine(Directory, name + ".png");

    public string ThemePrefsShotPath(string name) => Path.Combine(Directory, name + ".png");
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
/// 网络诊断页一步证据：五类分列的类别名与值、认证信号标题与受控认证分类（用户实际看到的内容）。
/// 只记录受控词表内的文本与虚构夹具事实，不含凭据、Cookie、URL 或原始门户响应。
/// </summary>
internal sealed record QaDiagnosticsStep(
    string State,
    bool Trusted,
    List<string> Captions,
    List<string> Values,
    List<string> SignalCaptions,
    string AttemptSummary,
    string PersistentState,
    string? File);

internal sealed record QaDiagnosticsReport(
    string Mode,
    bool Fixture,
    string ProcessPath,
    int ProcessId,
    bool Administrator,
    string MutexName,
    string ThemeVariant,
    string ReadOnlyNotice,
    List<QaDiagnosticsStep> Steps,
    string? Error = null);

/// <summary>
/// 日志页一步证据：日期入口数、选中日期、行数、范围/缺口/失败说明、首行事件、详情首行与复制可用性。
/// 夹具模式额外记录脱敏复制文本（供脚本隐私扫描）；实况模式不记录详情或复制文本，只记录计数与说明，
/// 不把真实日志内容带出本机（隐私契约与诊断页一致）。
/// </summary>
internal sealed record QaLogsStep(
    string Scenario,
    int Dates,
    string? SelectedDate,
    int Rows,
    string ScopeNote,
    string GapNote,
    string FailureNote,
    string FirstRow,
    string DetailHeading,
    bool CopyAvailable,
    string? CopyText,
    string? File);

internal sealed record QaLogsReport(
    string Mode,
    bool Fixture,
    string ProcessPath,
    int ProcessId,
    bool Administrator,
    string MutexName,
    string ThemeVariant,
    string ScopeRule,
    List<QaLogsStep> Steps,
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

/// <summary>
/// 设置页一步证据（#8 普通界面侧）：状态可信度、只读事实行与修改入口标签——
/// 普通界面不读取受限配置、不显示凭据（故事 34、36 的呈现侧）。
/// </summary>
internal sealed record QaSettingsStep(
    string State,
    bool Trusted,
    string RecoveryState,
    string GuardedNetwork,
    string PortalLine,
    string ModifyLabel,
    string? File);

/// <summary>一次修改入口驱动：预期命令与实际记录的命令（QA 模式只记录，不弹 UAC）。</summary>
internal sealed record QaSettingsAction(string Context, string Expected, bool Launched, string? Command);

/// <summary>
/// 设置编辑器一步证据（#8 提权侧）：场景结果、settings.json / credentials.dpapi 的落盘事实、
/// 凭据密文不含明文、认证暂停标记逐字节保持（故事 39）。只记录布尔事实与受控消息，
/// 不把凭据内容或密码带出本机。
/// </summary>
internal sealed record QaSettingsEditorStep(
    string Scenario,
    string Outcome,
    string Message,
    bool SettingsWritten,
    bool CredentialsWritten,
    bool CredentialsCreated,
    bool CredentialPlaintextFree,
    bool BlockUntouched,
    string? File);

internal sealed record QaSettingsReport(
    string Mode,
    bool Fixture,
    string ProcessPath,
    int ProcessId,
    bool Administrator,
    string MutexName,
    string ThemeVariant,
    List<QaSettingsStep> Steps,
    List<QaSettingsAction> Actions,
    List<QaSettingsEditorStep> EditorSteps,
    string? Error = null);

/// <summary>
/// 主题偏好一步证据（#9）：当前偏好、应用级请求变体、主窗口实际变体与操作系统变体，
/// 以及偏好文件的文件名与全文（只含主题枚举，供脚本逐字断言无秘密）。
/// </summary>
internal sealed record QaThemePrefsStep(
    string Scenario,
    string Preference,
    string RequestedVariant,
    string ActualVariant,
    string OsVariant,
    string PrefsFile,
    string PrefsContent,
    string? File);

internal sealed record QaThemePrefsReport(
    string Mode,
    string Phase,
    string ProcessPath,
    int ProcessId,
    bool Administrator,
    string MutexName,
    string ThemeVariant,
    List<QaThemePrefsStep> Steps,
    string? Error = null);
