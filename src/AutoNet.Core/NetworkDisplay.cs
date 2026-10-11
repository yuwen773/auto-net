namespace AutoNet.Core;

/// <summary>
/// The mutually exclusive primary state shown in the main health area. Exactly one applies at a time;
/// orthogonal facts (quota exhausted, proxy fault, recovery paused) are reported through
/// <see cref="NetworkDisplay.Additional"/> instead of being folded in here.
/// </summary>
public enum NetworkPrimaryState
{
    Unrecognized,
    Online,
    Reconnecting,
    NeedsAuthentication,
    BindingRejected,
    AuthenticatedOffline,
    AuthenticationUnknown,
    DisconnectQuotaExhausted,
    InterfaceUnavailable,
    WaitingForAddress,
    Associating,
    OtherProfileConnected,
    SettingsMissing,
    ProbeFailed,
    RecoveryPaused
}

/// <summary>Facts that can co-exist with any primary state. Rendered as separate, independent indicators.</summary>
[Flags]
public enum NetworkAdditional
{
    None = 0,
    /// <summary>Network is reachable but automatic authentication submission is currently suspended.</summary>
    AuthenticationSuspended = 1,
    /// <summary>The direct-internet probe passed but the campus path may still be wrong; TUN/UU are inferences.</summary>
    EgressUnverified = 2,
    /// <summary>All proactive-disconnect attempts for this outage are used up.</summary>
    DisconnectQuotaExhausted = 4,
    /// <summary>A disconnect request was reserved but its completion was never durably confirmed.</summary>
    DisconnectUnconfirmed = 8,
    /// <summary>Auto-recovery is switched off in settings; detection still runs.</summary>
    RecoveryPaused = 16,
    /// <summary>Local proxy or system path probe failed.</summary>
    ProxyFault = 32
}

/// <summary>One row of the status matrix: everything the UI needs to render a state, decided without touching WinForms.</summary>
public sealed record NetworkDisplay(
    NetworkPrimaryState Primary,
    NetworkAdditional Additional,
    string Title,
    string Explanation,
    string NextStep,
    string TechnicalDetail)
{
    public bool IsHealthy => Primary == NetworkPrimaryState.Online;
}

/// <summary>
/// Pure projection from <see cref="StatusSnapshot"/> to the state the window renders. No I/O, no
/// WinForms types, no clock reads — the caller passes freshness in — so every branch is directly testable.
/// An unrecognised <see cref="StatusSnapshot.State"/> yields <see cref="NetworkPrimaryState.Unrecognized"/>
/// and must never fall through to a healthy-looking state.
/// </summary>
public static class NetworkDisplayMap
{
    /// <summary>Matches the tray's existing freshness window; a status older than this is reported as stale.</summary>
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(15);

    /// <summary>Service status strings this mapper understands. A new one must be added deliberately.</summary>
    public static IReadOnlyCollection<string> KnownStates { get; } =
    [
        "校园网在线", "正在重连校园 Wi-Fi", "需要处理认证", "已认证但外网不可达", "认证状态未知",
        "正在恢复", "网卡未就绪", "等待网络地址", "Wi-Fi 正在连接", "已切换其他 Wi-Fi",
        "等待设置", "检测异常", "已暂停恢复", "启动中"
    ];

    public static NetworkDisplay Map(StatusSnapshot? status, TimeSpan? age)
    {
        if (status is null) return new(NetworkPrimaryState.Unrecognized, NetworkAdditional.None,
            "后台服务未就绪", "还没有收到后台服务的状态，暂时无法判断网络情况。", "请确认 AutoNet 服务已安装并正在运行。", "未读取到 status.json。");
        if (age is null || age >= FreshnessWindow)
            return Build(NetworkPrimaryState.Unrecognized, status,
                "后台状态已过期", "后台服务已有一段时间没有更新，这里的信息可能已经过时。", "请确认 AutoNet 服务正在运行，并查看日志了解原因。",
                $"状态已 {DescribeAge(age)} 未更新。");

        var additional = Additional(status);
        var primary = Primary(status, additional);
        var (title, explanation, nextStep) = Copy(primary, status);
        return new(primary, additional, title, explanation, nextStep, Technical(status));
    }

    private static NetworkPrimaryState Primary(StatusSnapshot s, NetworkAdditional additional)
    {
        // Binding rejection is checked before the generic blocked state: it needs different user action.
        if (s.AuthenticationFailure == nameof(PortalFailure.BindingRejected)) return NetworkPrimaryState.BindingRejected;
        if (!s.Enabled && s.Direct != "外网可达") return NetworkPrimaryState.RecoveryPaused;
        return s.State switch
        {
            "校园网在线" => NetworkPrimaryState.Online,
            "正在重连校园 Wi-Fi" => NetworkPrimaryState.Reconnecting,
            "需要处理认证" => NetworkPrimaryState.NeedsAuthentication,
            "已认证但外网不可达" => additional.HasFlag(NetworkAdditional.DisconnectQuotaExhausted)
                ? NetworkPrimaryState.DisconnectQuotaExhausted
                : NetworkPrimaryState.AuthenticatedOffline,
            "认证状态未知" => additional.HasFlag(NetworkAdditional.DisconnectQuotaExhausted)
                ? NetworkPrimaryState.DisconnectQuotaExhausted
                : NetworkPrimaryState.AuthenticationUnknown,
            "正在恢复" => NetworkPrimaryState.Reconnecting,
            "网卡未就绪" => NetworkPrimaryState.InterfaceUnavailable,
            "等待网络地址" => NetworkPrimaryState.WaitingForAddress,
            "Wi-Fi 正在连接" => NetworkPrimaryState.Associating,
            "已切换其他 Wi-Fi" => NetworkPrimaryState.OtherProfileConnected,
            "等待设置" => NetworkPrimaryState.SettingsMissing,
            "检测异常" => NetworkPrimaryState.ProbeFailed,
            "已暂停恢复" => NetworkPrimaryState.RecoveryPaused,
            "启动中" => NetworkPrimaryState.AuthenticationUnknown,
            _ => NetworkPrimaryState.Unrecognized
        };
    }

    private static NetworkAdditional Additional(StatusSnapshot s)
    {
        var flags = NetworkAdditional.None;
        if (!s.Enabled) flags |= NetworkAdditional.RecoveryPaused;
        if (s.CredentialsBlocked) flags |= NetworkAdditional.AuthenticationSuspended;
        // The direct probe only proves the campus path answered. Physical egress is never verified here.
        if (s.Direct == "外网可达") flags |= NetworkAdditional.EgressUnverified;
        if (s.DisconnectBudgetMax > 0 && s.DisconnectAttempts >= s.DisconnectBudgetMax) flags |= NetworkAdditional.DisconnectQuotaExhausted;
        if (s.DisconnectRequestUnconfirmed) flags |= NetworkAdditional.DisconnectUnconfirmed;
        if (s.Tunnel.Contains("不可达")) flags |= NetworkAdditional.ProxyFault;
        return flags;
    }

    private static (string Title, string Explanation, string NextStep) Copy(NetworkPrimaryState state, StatusSnapshot s) => state switch
    {
        NetworkPrimaryState.Online =>
            ("网络在线", "校园网 HTTPS 内容检查已通过。", "无需操作，保持当前连接即可。"),
        NetworkPrimaryState.AuthenticationUnknown =>
            ("认证状态未知", "两个外网检查都没有通过，AutoNet 也无法确认当前是否已认证；这不等于认证失败。", "请稍候观察，若持续出现请查看日志。"),
        NetworkPrimaryState.AuthenticatedOffline =>
            ("已认证但外网不可达", "校园网显示已认证，但两个外网检查都没有通过。", "请稍候观察；持续失败时 AutoNet 会按额度尝试重连。"),
        NetworkPrimaryState.Reconnecting =>
            ("正在重连校园 Wi-Fi", "AutoNet 已请求重新连接校园 Wi-Fi，正在等待链路恢复。", "请等待，无需手动干预。"),
        // Whether credentials are actually blocked is a separate fact from "needs authentication". The
        // service only emits this state when blocked, but the wording must follow the flag rather than
        // the state string — otherwise a stale status file, or a future service build, makes the window
        // tell the user their credentials are suspended when AutoNet is about to submit them.
        NetworkPrimaryState.NeedsAuthentication when !s.CredentialsBlocked =>
            ("需要处理认证", "校园网会话已失效，需要重新登录。", "AutoNet 会按既定退避策略尝试认证；请查看日志了解进展。"),
        NetworkPrimaryState.NeedsAuthentication =>
            ("需要处理认证", "校园网需要登录，但自动提交凭据当前处于暂停状态。", "请先查看认证诊断，再决定是否解除暂停。"),
        NetworkPrimaryState.BindingRejected =>
            ("VLAN 绑定被拒绝", "校园网拒绝了本次登录的设备绑定关系；这不是密码错误。", "请确认已连接正确的校园 Wi-Fi 与网卡，然后查看认证诊断。"),
        NetworkPrimaryState.DisconnectQuotaExhausted =>
            ("主动断线额度已用尽", "本次故障的主动断线请求已用完，AutoNet 会继续只读检测与允许的认证。", "请查看认证诊断；额度会在连续在线后自动恢复。"),
        NetworkPrimaryState.InterfaceUnavailable =>
            ("网卡未就绪", "选定的 Wi-Fi 网卡尚未就绪。", "请确认网卡已启用并选择了正确的校园 Wi-Fi。"),
        NetworkPrimaryState.WaitingForAddress =>
            ("等待网络地址", "Wi-Fi 已关联，正在等待 IPv4 地址。", "请等待 DHCP 完成。"),
        NetworkPrimaryState.Associating =>
            ("Wi-Fi 正在连接", "正在与校园 Wi-Fi 关联并进行链路认证。", "请等待连接完成。"),
        NetworkPrimaryState.OtherProfileConnected =>
            ("已切换其他 Wi-Fi", "你连接了另一个 Wi-Fi，AutoNet 会保留你的选择。", "如需守护校园网，请切回指定的校园 Wi-Fi。"),
        NetworkPrimaryState.SettingsMissing =>
            ("等待设置", "尚未保存校园 Wi-Fi 与认证信息。", "请在设置中填写并保存。"),
        NetworkPrimaryState.ProbeFailed =>
            ("检测异常", "本轮检测出现异常，AutoNet 会自动重试。", "若持续出现，请查看日志了解原因。"),
        NetworkPrimaryState.RecoveryPaused =>
            ("自动恢复已暂停", "自动恢复已关闭，AutoNet 仍会检测网络但不会重连或认证。", "如需恢复自动处理，请在设置中重新开启。"),
        _ =>
            ("未识别的服务状态", "后台服务返回了当前版本无法识别的状态，界面不会把它当作正常。", "请查看日志，并向维护者报告此状态。")
    };

    private static NetworkDisplay Build(NetworkPrimaryState state, StatusSnapshot s, string title, string explanation, string nextStep, string technical)
        => new(state, Additional(s), title, explanation, nextStep, technical);

    /// <summary>Operator-facing classification only. Never carries exception text, URLs, or identifiers.</summary>
    private static string Technical(StatusSnapshot s)
    {
        var parts = new List<string>();
        if (s.AuthenticationStage != "Unknown") parts.Add($"认证阶段 {s.AuthenticationStage}");
        if (s.AuthenticationFailure != "None") parts.Add($"失败分类 {s.AuthenticationFailure}");
        if (s.AuthenticationHttpStatus is { } status) parts.Add($"HTTP {status}");
        if (s.AuthenticationPersistentState != "Unknown") parts.Add($"认证持久状态 {s.AuthenticationPersistentState}");
        if (parts.Count == 0) return "";
        return string.Join("；", parts) + (s.CredentialRequestStarted ? "；已发起凭据请求" : "；未发送凭据");
    }

    private static string DescribeAge(TimeSpan? age)
    {
        if (age is null) return "未知时长";
        return age.Value < TimeSpan.Zero ? "少于 0 秒" : $"{age.Value.TotalSeconds:F0} 秒";
    }
}
