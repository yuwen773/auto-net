using System.Globalization;
using System.Net.Sockets;

namespace AutoNet.Core;

/// <summary>
/// Builds a shareable log summary from a strict field allowlist. Free-text fields (<c>Detail</c>,
/// <c>LastError</c>, <c>Uu</c>, <c>SystemPath</c>) are never copied: an entry written by an older build
/// can carry anything in those, so "the field exists in the current model" is not evidence it is safe.
/// Every string that reaches the output is either a constant defined here or a value that matches a
/// controlled vocabulary; anything else collapses to a fixed placeholder.
/// </summary>
public static class LogSummary
{
    /// <summary>Event actions the service emits. Unknown actions become 其他事件 rather than passing through.</summary>
    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {
        "重连校园 Wi-Fi", "连接校园 Wi-Fi", "恢复联网", "观测到联网恢复", "认证结果", "认证已接受",
        "检测异常", "状态写入异常", "日志写入异常", "设置已更改", "服务启动", "服务停止", "安全状态已恢复",
        "服务观测开始", "服务观测停止", "发现网络异常", "外网恢复验证通过", "继续追踪历史故障", "故障观测暂停",
        "恢复决策", "恢复动作开始", "恢复动作结束", "认证步骤", "Wi-Fi 请求结果", "安全状态写入开始",
        "安全状态写入完成", "操作异常", "操作恢复正常", "检测轮次中断", "日志证据缺口",
        "认证提交获准", "认证提交被阻止", "提交结果复检", "提交结果消解", "提交结果待人工",
        "断线前重新核验", "诊断事件", "开始恢复动作", "辅助检测失败",
        "取消主动断线", "主动断线请求失败", "重新认证"
    };

    static LogSummary() => AllowedActions.UnionWith(NetworkDisplayMap.KnownStates);

    private static readonly HashSet<string> AllowedWifi = new(StringComparer.Ordinal)
    { "已连接", "已关联，等待 IPv4", "正在连接", "网卡未就绪", "未连接", "其他配置", "待检测" };

    private static readonly HashSet<string> AllowedPortal = new(StringComparer.Ordinal)
    { "已认证", "需认证", "未知，不自动认证", "未查询（外网检查已通过）", "待检测", "未知" };

    private static readonly HashSet<string> AllowedDirect = new(StringComparer.Ordinal)
    { "外网可达", "外网不可达或未知", "待检测" };

    private static readonly HashSet<string> AllowedStages = new(StringComparer.Ordinal)
    {
        nameof(PortalStage.Unknown), nameof(PortalStage.Status), nameof(PortalStage.GatewayRedirect),
        nameof(PortalStage.HomePage), nameof(PortalStage.ClientIpv4), nameof(PortalStage.Configuration),
        nameof(PortalStage.AuthenticationMode), nameof(PortalStage.Submission), nameof(PortalStage.Complete)
    };

    private static readonly HashSet<string> AllowedFailures = new(StringComparer.Ordinal)
    {
        nameof(PortalFailure.None), nameof(PortalFailure.InvalidTarget), nameof(PortalFailure.AlreadyAuthenticated),
        nameof(PortalFailure.StatusUnknown), nameof(PortalFailure.HttpStatus), nameof(PortalFailure.MissingClientIpv4),
        nameof(PortalFailure.MalformedConfiguration), nameof(PortalFailure.MissingFlag), nameof(PortalFailure.InvalidFlagType),
        nameof(PortalFailure.UnsupportedFlag), nameof(PortalFailure.DuplicateFlag), nameof(PortalFailure.InvalidResponse),
        nameof(PortalFailure.Timeout), nameof(PortalFailure.Transport), nameof(PortalFailure.Tls),
        nameof(PortalFailure.CredentialsRejected), nameof(PortalFailure.InteractionRequired), nameof(PortalFailure.LoginRejected),
        nameof(PortalFailure.MissingGatewayParameters), nameof(PortalFailure.BindingRejected), nameof(PortalFailure.SafetyPersistence)
    };

    private static readonly HashSet<string> AllowedEvents = new(StringComparer.Ordinal)
    {
        "SessionStarted", "SessionStopped", "IncidentStarted", "IncidentRecovered", "IncidentResumed", "IncidentSuspended",
        "RecoveryDecision", "RecoveryActionStarted", "RecoveryActionCompleted", "PortalStep", "WifiRequestResult",
        "SafetyStateWriteStarted", "SafetyStateWritten", "OperationFailed", "OperationRecovered", "CycleInterrupted",
        "LoggingGap", "StatusChanged", "SubmissionAuthorized", "SubmissionNotAuthorized", "DisconnectRecheck",
        "SubmissionRechecking", "SubmissionResolved", "SubmissionUnresolved"
    };
    private static readonly HashSet<string> AllowedReasons = new(StringComparer.Ordinal)
    {
        "PreviousObservationUnavailable", "ObservationInterrupted", "TargetChanged", "SettingsMissing", "OtherWifiSelected",
        "RecoverySourceUnconfirmed", "ServiceStopping", "Cancellation", "Online", "RecoveryDisabled", "CredentialsMissing",
        "AuthenticationPaused", "ActiveDisconnectDisabled", "NoApplicableAction", "DisconnectCompletionUnconfirmed",
        "DisconnectBudgetExhausted", "InsufficientFailureSamples", "ObservationWindow", "ReconnectWifiCooldown", "AuthenticateCooldown",
        "DisconnectWifiCooldown", "ReconnectWifiRequired", "AuthenticateRequired", "DisconnectWifiRequired", "ObservationFailed",
        "AuthenticationStateChanged", "AuthenticationAttemptLimit", "SettingsChanged", "WifiChanged", "SubmissionReservationFailed",
        "FinalSettingsOrWifiChanged", "InterfaceUnavailable", "WifiDisconnected", "Associating", "WaitingForAddress", "OtherProfile",
        "FinalWifiChanged", "RecheckRejected", "ChecksPassed", "FreshInternetRecovered", "WifiNotReady"
    };
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        "MainCycle", "SettingsRead", "SettingsValidation", "DisconnectBudgetRead", "AuthenticationStateRead", "CredentialsRead",
        "WifiRead", "InternetProbe", "PortalInspection", "StatusWrite", "Authentication", "AuthenticationStateWrite", "WifiRequest",
        "DisconnectBudgetWrite", "Auxiliary", "AuxiliaryCancel", "Power", "ServiceLoop", "PreviousLogRead", "ReconnectWifi", "DisconnectWifi", "Authenticate",
        "DisconnectRecheckInternet", "DisconnectRecheckPortal", "SubmissionRecheck"
    };

    /// <summary>One line per entry, oldest first. Every value is allowlisted or a constant.</summary>
    public static string Format(IEnumerable<ActivityLog> entries)
    {
        var lines = new List<string> { "AutoNet 日志脱敏摘要（仅含受控字段，不含异常原文、账号或网络标识）" };
        foreach (var entry in entries)
        {
            var parts = new List<string>
            {
                entry.Time.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Vocabulary(entry.Action, AllowedActions, "其他事件"),
                "Wi-Fi " + Vocabulary(entry.Wifi, AllowedWifi, "未知"),
                "认证 " + Vocabulary(entry.Portal, AllowedPortal, "未知"),
                "直连 " + Vocabulary(entry.Direct, AllowedDirect, "未知")
            };
            if (entry.Seconds is { } seconds) parts.Add("观测耗时 " + seconds.ToString("0.##", CultureInfo.InvariantCulture) + " 秒");
            if (entry.Attempts is { } attempts) parts.Add("自动动作 " + attempts + " 次");
            if (entry.DisconnectAttempts is { } disconnects) parts.Add("主动断线 " + disconnects + " 次");
            if (entry.AuthenticationStage is { } stage && AllowedStages.Contains(stage)) parts.Add("阶段 " + stage);
            if (entry.AuthenticationFailure is { } failure && failure != nameof(PortalFailure.None) && AllowedFailures.Contains(failure))
                parts.Add("分类 " + failure);
            if (entry.NotificationCode is { } notify && NotificationCodes.IsKnown(notify)) parts.Add("通知 " + notify);
            AppendDiagnostics(parts, entry);
            lines.Add(string.Join(" · ", parts));
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Count of records dropped because their free-text fields were not copied. Surfaced to the user.</summary>
    public static string Notice(int total, int included) =>
        $"已复制 {included} 条记录的脱敏摘要（共 {total} 条）；异常原文、账号与网络标识未包含。";

    private static string Vocabulary(string? value, HashSet<string> allowed, string fallback)
        => value is not null && allowed.Contains(value) ? value : fallback;

    private static void AppendDiagnostics(List<string> parts, ActivityLog entry)
    {
        if (entry.EventCode is not { } code || !AllowedEvents.Contains(code)) return;
        parts.Add("事件 " + code);
        if (Guid.TryParse(entry.RunId, out var run)) parts.Add("运行 " + run.ToString("N"));
        if (Guid.TryParse(entry.IncidentId, out var incident)) parts.Add("故障 " + incident.ToString("N"));
        foreach (var candidate in entry.BuildId?.Split('/').Skip(1).Take(2) ?? [])
            if (Guid.TryParse(candidate, out var module)) parts.Add("程序集 " + module.ToString("N"));
        if (entry.Sequence is { } sequence) parts.Add("序号 " + sequence);
        if (entry.Cycle is { } cycle) parts.Add("轮次 " + cycle);
        if (entry.Diagnostics is not { } data) return;
        if (data.Reason is { } reason && AllowedReasons.Contains(reason)) parts.Add("决策 " + reason);
        if (data.Outcome is "None" or "ReconnectWifi" or "DisconnectWifi" or "Authenticate" or "Started" or "HeadersReceived" or "BodyRead" or "BodyReadFailed" or
            "Failed" or "Recovered" or "Authorized" or "Blocked" or "Issued" or "Skipped" or "Accepted" or "Rejected" or "Unsupported" or
            "RejectedCredentials" or "RejectedBinding" or "NeedsInteraction" or "Unconfirmed") parts.Add("结果 " + data.Outcome);
        if (data.Stage is { } stage && stage.Split('/').All(s => AllowedOperations.Contains(s) || AllowedStages.Contains(s))) parts.Add("步骤 " + stage);
        if (Guid.TryParse(data.ActionId, out var action)) parts.Add("动作 " + action.ToString("N"));
        if (data.NativeError is { } native) parts.Add("Windows " + native);
        if (data.HttpStatus is { } httpStatus) parts.Add("HTTP " + httpStatus);
        if (data.NextAttempt is { } next) parts.Add("下次尝试 " + next.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        if (data.FailedRounds is { } rounds) parts.Add("连续失败 " + rounds);
        if (data.LostEvents is { } lost) parts.Add("未写入事件 " + lost);
        if (data.HasIpv4 is { } ip) parts.Add("本机IPv4就绪 " + ip);
        if (Enum.TryParse<WifiState>(data.WifiState, out var wifiState) && Enum.IsDefined(wifiState)) parts.Add("链路状态 " + wifiState);
        if (Enum.TryParse<NetworkCondition>(data.Condition, out var condition) && Enum.IsDefined(condition)) parts.Add("观测条件 " + condition);
        if (data.AuthenticationAttempts is { } authenticationAttempts) parts.Add($"认证尝试 {authenticationAttempts}/{data.AuthenticationAttemptMax}");
        if (data.DisconnectAttempts is { } disconnectAttempts) parts.Add($"断线尝试 {disconnectAttempts}/{data.DisconnectAttemptMax}");
        if (data.RequiredFailureRounds is { } requiredRounds) parts.Add("失败采样门槛 " + requiredRounds);
        if (data.ObservationSeconds is { } observed) parts.Add($"观察窗口 {observed.ToString("0.##", CultureInfo.InvariantCulture)}/{data.RequiredObservationSeconds} 秒");
        if (data.Enabled is { } enabled) parts.Add("启用恢复 " + enabled);
        if (data.HasCredentials is { } credentials) parts.Add("已保存凭据 " + credentials);
        if (data.AllowActiveDisconnect is { } disconnect) parts.Add("允许主动断线 " + disconnect);
        if (data.AuthenticationState is { } state && Enum.TryParse<AuthenticationState>(state, out var parsedState) && Enum.IsDefined(parsedState))
            parts.Add("认证授权 " + parsedState);
        if (Enum.TryParse<AuthenticationRecordKind>(data.AuthenticationRecordSource, out var record) && Enum.IsDefined(record)) parts.Add("记录来源 " + record);
        if (data.Error is { } error) AppendError(parts, error);
        if (data.Parameters is { } parameters) AppendParameters(parts, parameters);
        foreach (var probe in data.Probes ?? [])
        {
            if (probe.Target is not ("BaiduRobots" or "MicrosoftRobots")) continue;
            var failure = probe.Failure is "None" or "HttpStatus" or "ContentMismatch" or "Timeout" or "ResponseRead" ? probe.Failure :
                Enum.TryParse<HttpRequestError>(probe.Failure, out var http) && Enum.IsDefined(http) ? http.ToString() : "Unknown";
            parts.Add($"探测 {probe.Target}/{failure}/HTTP {probe.HttpStatus}");
            if (probe.Error is { } probeError) AppendError(parts, probeError);
        }
        if (data.Authentication is { } auth)
        {
            if (Enum.IsDefined(auth.Stage) && Enum.IsDefined(auth.Failure)) parts.Add($"认证 {auth.Stage}/{auth.Failure}/HTTP {auth.HttpStatus}/已发起凭据请求 {auth.CredentialRequestStarted}");
            if (auth.Field is "en_md5" or "password_cut" or "enable_r3") parts.Add("配置字段 " + auth.Field);
            if (auth.Error is { } authError) AppendError(parts, authError);
            if (auth.Evidence is { } evidence) AppendParameters(parts, evidence);
        }
    }

    private static void AppendParameters(List<string> parts, PortalEvidence evidence)
    {
        static string Shape(string? value) => value is "Missing" or "Valid" or "Invalid" or "Present" or "NotChecked" ? value : "Unknown";
        parts.Add($"参数 状态地址={Shape(evidence.StatusAddress)},网关地址={Shape(evidence.GatewayAddress)},首页地址={Shape(evidence.HomeAddress)},AC={Shape(evidence.AcAddress)},MAC={Shape(evidence.MacToken)}");
        if (evidence.AddressSource is "GatewayRedirect" or "StatusResponse" or "HomePage" or "None") parts.Add("地址来源 " + evidence.AddressSource);
        if (evidence.ResponseContentType is "Html" or "Json" or "Javascript" or "Text" or "Other" or "Missing") parts.Add("响应类型 " + evidence.ResponseContentType);
        if (evidence.ResponseCharacters is { } length) parts.Add("响应字符数 " + length);
    }

    private static void AppendError(List<string> parts, DiagnosticError error)
    {
        if (error.Type is "HttpRequestException" or "SocketException" or "NetworkInformationException" or "Win32Exception" or "IOException" or
            "UnauthorizedAccessException" or "SecurityException" or "InvalidDataException" or "JsonException" or "OperationCanceledException" or
            "TaskCanceledException" or "ArgumentException" or "ArgumentOutOfRangeException" or "InvalidOperationException" or "NotSupportedException") parts.Add("异常 " + error.Type);
        if (Enum.TryParse<HttpRequestError>(error.HttpError, out var http) && Enum.IsDefined(http)) parts.Add("HTTP错误 " + http);
        if (Enum.TryParse<SocketError>(error.SocketError, out var socket) && Enum.IsDefined(socket)) parts.Add("Socket错误 " + socket);
        parts.Add("HRESULT " + error.HResult.ToString(CultureInfo.InvariantCulture));
        if (error.NativeError is { } native) parts.Add("原生错误 " + native);
    }
}
