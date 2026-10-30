using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 认证页的五种可观察状态（工单 #37 / #27 故事 20–28）、恢复开关关闭的暂停态与不可确认的降级态，共七种。
/// 状态只由可信后台快照和浏览器宿主状态投影，不从页面内容反推，也不把“打开页面”当作提交意图。
/// </summary>
internal enum AuthPagePhase
{
    /// <summary>外网内容检查通过。</summary>
    Online,
    /// <summary>门户状态明确显示已认证，但外网暂时不可达。</summary>
    Authenticated,
    /// <summary>上次凭据提交的结果尚未确认。</summary>
    OutcomeUnknown,
    /// <summary>自动恢复开关关闭：后台仍检测网络，但不重连或自动提交凭据。</summary>
    Paused,
    /// <summary>自动认证将按额度/退避继续，或等待唯一授权。</summary>
    WaitingRetry,
    /// <summary>自动提交已暂停、额度耗尽、缺少设置/凭据或门户要求人工处理。</summary>
    NeedsManual,
    /// <summary>没有可信的后台状态。</summary>
    Unconfirmed
}

/// <summary>
/// 认证页内容投影（迁移工单 #37）：门户地址、七种认证阶段与浏览器宿主状态。
/// 不依赖 Avalonia 或具体浏览器类型，可直接用假宿主受控测试。
/// </summary>
internal sealed record AuthPageContent : PageContent
{
    internal AuthPageContent() : base(ShellPage.Authentication, ShellPages.Info(ShellPage.Authentication).Name) { }

    /// <summary>状态边界（完整性、时间有效性）是否允许依据其内容显示事实。</summary>
    public bool Trusted { get; init; }

    public AuthPagePhase Phase { get; init; } = AuthPagePhase.Unconfirmed;
    public OverviewTone Tone { get; init; } = OverviewTone.Neutral;
    public string StatusTitle { get; init; } = "";
    public string StatusExplanation { get; init; } = "";
    public string NextStep { get; init; } = "";

    /// <summary>固定门户根地址；页面只加载它，不接受其他来源。</summary>
    public string PortalAddress { get; init; } = AuthPageModel.PortalAddress;

    /// <summary>后台载体说明：协议认证只投影状态；内嵌通道也不因打开本页另起会话。</summary>
    public string BackgroundNote { get; init; } = "";

    public AuthPageHostStatus HostStatus { get; init; } = AuthPageHostStatus.NotAttached;
    public string HostTitle { get; init; } = "";
    public string HostDetail { get; init; } = "";
    public bool HostAvailable { get; init; }
    public bool HostRetryVisible { get; init; }

    /// <summary>渲染指纹：全部可见字段的稳定拼接；不变时视图跳过重建。</summary>
    public string ContentKey { get; init; } = "";
}

/// <summary>
/// 认证页投影规则（#37）。纯函数：不读文件、不访问网络、不产生提交；
/// 浏览器运行时缺失、网络边界未就绪与加载失败都只转成受控文案。
/// </summary>
internal static class AuthPageModel
{
    /// <summary>与既有设置页、协议认证一致的门户根地址；唯一来源在 Core，避免地址漂移。</summary>
    public const string PortalAddress = RecoveryPolicy.FallbackPortalPageUrl;

    /// <summary>把一次状态读取与宿主状态投影为页面内容；<paramref name="reading"/> 为 null 表示尚未取得状态。</summary>
    public static AuthPageContent From(StatusReading? reading, AuthPageHostSnapshot host)
    {
        var trusted = reading?.Trusted == true;
        var snapshot = trusted ? reading!.Snapshot : null;
        var phase = PhaseFor(reading);
        var cause = phase == AuthPagePhase.NeedsManual ? ManualCauseFor(snapshot) : ManualCause.None;
        var (title, explanation, nextStep) = Copy(phase, cause, snapshot);
        var content = new AuthPageContent
        {
            Trusted = trusted,
            Phase = phase,
            Tone = ToneFor(phase),
            StatusTitle = title,
            StatusExplanation = explanation,
            NextStep = nextStep,
            BackgroundNote = BackgroundNote(trusted, snapshot),
            HostStatus = host.Status,
            HostTitle = HostTitle(host.Status),
            HostDetail = host.Detail.Length > 0 ? host.Detail : HostDetail(host.Status),
            HostAvailable = host.Status == AuthPageHostStatus.Attached,
            HostRetryVisible = host.Status is AuthPageHostStatus.RuntimeMissing
                or AuthPageHostStatus.NetworkBoundaryUnavailable
                or AuthPageHostStatus.Failed
        };
        return content with { ContentKey = Fingerprint(content) };
    }

    /// <summary>
    /// 阶段判定顺序固定：联网 > 已认证 > 需要人工 > 已暂停 > 提交结果未知 > 等待重试 > 不可确认。
    /// 联网事实优先于认证暂停，避免把已恢复的链路说成仍有问题；明确的人工信号
    /// （结构变化、存储故障、终止性暂停、提交额度用尽）优先于“结果未知”。
    /// 恢复开关关闭时服务仍会观测到“门户需认证”，但不会仅因等待而自动提交，
    /// 因此必须排在“等待重试”之前，且不能把待消解提交说成会自动重试。
    /// </summary>
    public static AuthPagePhase PhaseFor(StatusReading? reading)
    {
        if (reading is not { Trusted: true }) return AuthPagePhase.Unconfirmed;
        var snapshot = reading.Snapshot!;
        if (snapshot.Direct == "外网可达" || reading.Display.Primary == NetworkPrimaryState.Online)
            return AuthPagePhase.Online;
        if (snapshot.Portal == "已认证") return AuthPagePhase.Authenticated;
        if (ManualCauseFor(snapshot) != ManualCause.None)
            return AuthPagePhase.NeedsManual;
        if (IsRecoveryPaused(snapshot)) return AuthPagePhase.Paused;
        if (snapshot.AuthenticationPersistentState == nameof(AuthenticationState.SubmissionPending))
            return AuthPagePhase.OutcomeUnknown;
        if (snapshot.Portal == "需认证" ||
            reading.Display.Primary == NetworkPrimaryState.NeedsAuthentication ||
            snapshot.AuthenticationPersistentState == nameof(AuthenticationState.RetryCoolingDown) ||
            snapshot.AuthChannelRequested ||
            NextAttemptIsAuthentication(snapshot, reading))
            return AuthPagePhase.WaitingRetry;
        return AuthPagePhase.Unconfirmed;
    }

    /// <summary>
    /// 恢复开关关闭的可证实形态：服务在关闭恢复且外网不可达时发布“已暂停恢复”。
    /// 只认这个状态，不把 Enabled 缺省为 false 的“等待设置”“检测异常”等快照误读为暂停；
    /// 外网可达时即使开关关闭也按联网展示，不在此处判定。
    /// </summary>
    private static bool IsRecoveryPaused(StatusSnapshot? snapshot) =>
        snapshot is { Enabled: false, State: "已暂停恢复" };

    /// <summary>
    /// 只有服务明确发布“下一次动作是认证”时，通用退避时间才能被说成自动认证重试；
    /// Wi-Fi 重连或主动断线的退避不能被当成认证重试（旧版状态缺字段时为 None）。
    /// </summary>
    private static bool NextAttemptIsAuthentication(StatusSnapshot snapshot, StatusReading reading) =>
        snapshot.NextAttemptAction == nameof(ActionKind.Authenticate) &&
        snapshot.NextAttempt is { } next && next > reading.ReadAt;

    /// <summary>校园 Wi-Fi 已就绪；服务只在此时真正执行低频只读复检。</summary>
    private static bool RecheckPossible(StatusSnapshot? snapshot) => snapshot is { Wifi: "已连接" };

    /// <summary>复检文案跟随网络条件：Wi-Fi 未就绪时服务会跳过复检，页面不能声称正在复检。</summary>
    private static string RecheckClause(StatusSnapshot? snapshot, string active, string suspended) =>
        RecheckPossible(snapshot) ? active : suspended;

    /// <summary>需要人工处理的具体原因；只从快照已发布的事实判定，不推断缺失字段。</summary>
    private enum ManualCause
    {
        None,
        QuotaExhausted,
        StorageFault,
        PortalLayout,
        BrowserFallback,
        TerminalBlock,
        MissingSettings,
        MissingCredentials
    }

    /// <summary>
    /// 人工信号的优先顺序：存储故障 > 门户结构 > 浏览器兜底 > 终止性暂停 > 缺少设置 > 缺少凭据 > 提交额度用尽。
    /// 提交额度用尽与缺少凭据目前只能从服务已发布的受控文案识别；出现结构化字段后应改为读取字段，
    /// 未命中时保持一般“结果未知”或“等待重试”，不猜测额度，也不猜测凭据是否存在。
    /// </summary>
    private static ManualCause ManualCauseFor(StatusSnapshot? snapshot)
    {
        if (snapshot is null) return ManualCause.None;
        if (snapshot.SubmissionResolutionStorageFault) return ManualCause.StorageFault;
        if (snapshot.PortalLayoutSuspected) return ManualCause.PortalLayout;
        if (snapshot.BrowserFallbackRequested) return ManualCause.BrowserFallback;
        var state = snapshot.AuthenticationPersistentState;
        if (state is nameof(AuthenticationState.BindingRejected)
            or nameof(AuthenticationState.CredentialsRejected)
            or nameof(AuthenticationState.InteractionRequired)
            or nameof(AuthenticationState.SafetyBlocked))
            return ManualCause.TerminalBlock;
        // 旧版或损坏记录可能只留下 CredentialsBlocked；除 SubmissionPending 外一律按人工暂停处理。
        if (snapshot.CredentialsBlocked && state != nameof(AuthenticationState.SubmissionPending))
            return ManualCause.TerminalBlock;
        // 缺少设置或凭据时服务不会发出认证请求；这类快照不能落到“等待重试”。
        if (snapshot.State == "等待设置") return ManualCause.MissingSettings;
        if (MissingCredentials(snapshot)) return ManualCause.MissingCredentials;
        if (state == nameof(AuthenticationState.SubmissionPending) && SubmissionQuotaExhausted(snapshot))
            return ManualCause.QuotaExhausted;
        return ManualCause.None;
    }

    /// <summary>
    /// 缺少凭据的可证实形态：服务在 Wi-Fi 就绪、外网不可达且没有凭据时发布的受控文案
    /// “未保存认证信息”。与提交额度一样，这是结构化字段出现前的受控桥接；未命中时
    /// 不猜测凭据是否存在，也不读取、记录或回显任何账号密码。
    /// </summary>
    private static bool MissingCredentials(StatusSnapshot snapshot) =>
        snapshot.Detail?.Contains("未保存认证信息", StringComparison.Ordinal) == true;

    /// <summary>
    /// 提交额度用尽的受控文案标记。必须同时匹配“提交”语境，避免把主动断线额度
    /// （“主动断线额度已用尽”）误判为提交额度。未命中时按一般结果未知展示。
    /// </summary>
    private static bool SubmissionQuotaExhausted(StatusSnapshot snapshot) =>
        snapshot.Detail?.Contains("提交额度已用尽", StringComparison.Ordinal) == true ||
        snapshot.Detail?.Contains("两次额外额度已用尽", StringComparison.Ordinal) == true;

    public static OverviewTone ToneFor(AuthPagePhase phase) => phase switch
    {
        AuthPagePhase.Online or AuthPagePhase.Authenticated => OverviewTone.Good,
        AuthPagePhase.OutcomeUnknown => OverviewTone.Attention,
        AuthPagePhase.WaitingRetry => OverviewTone.Warning,
        AuthPagePhase.NeedsManual => OverviewTone.Problem,
        // 恢复开关关闭是用户主动设置，不是故障；与概览页一致按中性呈现。
        AuthPagePhase.Paused => OverviewTone.Neutral,
        _ => OverviewTone.Neutral
    };

    private static (string Title, string Explanation, string NextStep) Copy(
        AuthPagePhase phase, ManualCause cause, StatusSnapshot? snapshot) => phase switch
    {
        AuthPagePhase.Online => OnlineCopy(snapshot),
        AuthPagePhase.Authenticated =>
            ("认证成功", "门户状态显示已认证，但外网检查尚未通过；AutoNet 会继续检测。", "请稍候观察；持续失败时查看网络诊断。"),
        AuthPagePhase.OutcomeUnknown =>
            ("提交结果未知",
                "上次凭据提交的结果尚未确认。" + RecheckClause(snapshot,
                    "后台继续只读复检；",
                    "校园 Wi-Fi 当前不可用，后台暂不发起只读复检，恢复连接后再继续；") +
                "只有在外网不可达且共享提交额度与最短间隔仍允许时，才可能进行有限自动重试，否则不会重发凭据。打开或刷新本页本身不会提交凭据。",
                "保持观察；若网络诊断显示额度用尽或需要人工处理，请按指引操作，不要重复提交。"),
        AuthPagePhase.Paused => PausedCopy(snapshot),
        AuthPagePhase.WaitingRetry =>
            ("等待重试", "自动认证将在额度与退避条件满足后继续；打开、关闭或刷新本页不会改变重试节奏。", "请等待自动重试，无需反复打开页面。"),
        AuthPagePhase.NeedsManual => ManualCopy(cause, snapshot),
        _ =>
            ("状态待确认", "当前没有可信的后台状态，本页不会据此提交任何凭据。", "请确认 AutoNet 后台服务正在运行，然后等待下一次检测。")
    };

    /// <summary>
    /// 外网已恢复时不再按“结果未知”展示；若快照同时保留待消解的提交记录，
    /// 只补充“当前不会重新提交”的事实，不把联网恢复说成认证已成功。
    /// </summary>
    private static (string Title, string Explanation, string NextStep) OnlineCopy(StatusSnapshot? snapshot) =>
        snapshot?.AuthenticationPersistentState == nameof(AuthenticationState.SubmissionPending)
            ? ("已联网", "外网内容检查已通过；上次提交结果仍由后台只读复检，当前不会重新提交。", "无需操作；如需了解认证状态，可查看网络诊断。")
            : ("已联网", "外网内容检查已通过；查看本页不会改变认证状态。", "无需操作，保持当前连接即可。");

    /// <summary>
    /// 恢复开关关闭：后台仍检测，但不会重连或自动提交；待消解的提交只由只读复检处理。
    /// 明确告知用户暂停是开关状态，并给出恢复入口（概览页），页面本身不提供提交或开关动作。
    /// </summary>
    private static (string Title, string Explanation, string NextStep) PausedCopy(StatusSnapshot? snapshot)
    {
        var pending = snapshot?.AuthenticationPersistentState == nameof(AuthenticationState.SubmissionPending);
        var explanation = "自动恢复已关闭，AutoNet 仍会检测网络，但不会重连校园网或自动提交凭据。"
            + (pending
                ? "上次凭据提交的结果仍未确认；" + RecheckClause(snapshot,
                    "后台只做只读复检，不会重新提交。",
                    "校园 Wi-Fi 当前不可用，后台暂不发起只读复检；恢复连接后只做只读复检，不会重新提交。")
                : "");
        return ("自动恢复已关闭", explanation, "如需恢复自动处理，请在概览页开启自动恢复；本页不会提交凭据。");
    }

    private static (string Title, string Explanation, string NextStep) ManualCopy(
        ManualCause cause, StatusSnapshot? snapshot)
    {
        var pending = snapshot?.AuthenticationPersistentState == nameof(AuthenticationState.SubmissionPending);
        // 恢复开关关闭时，任何“条件满足仍可能有限自动重试”的说法都不成立：服务在该开关下
        // 不会重新提交凭据。这里只陈述暂停事实，不承诺重新开启后会做什么。
        var paused = IsRecoveryPaused(snapshot);
        return cause switch
        {
            ManualCause.QuotaExhausted =>
                ("提交额度已用尽", "上次凭据提交的结果仍未确认，且自动提交额度已用尽；" + RecheckClause(snapshot,
                        "后台只做低频只读复检，不会自动重发凭据。",
                        "校园 Wi-Fi 当前不可用，后台暂不发起只读复检，恢复连接后只做低频只读复检，不会自动重发凭据。")
                    + (paused ? "自动恢复当前已关闭；即使开启自动恢复也不会自动重发凭据。" : ""),
                    "按网络诊断指引人工核实；不要重复提交。"),
            ManualCause.StorageFault =>
                ("认证状态需检查", pending
                    ? "认证消解记录写入失败，上次提交结果仍未确认；" + RecheckClause(snapshot,
                            "后台继续只读复检，",
                            "校园 Wi-Fi 当前不可用，后台暂不发起只读复检，恢复连接后再继续；")
                        + (paused ? "自动恢复当前已关闭，不会自动重试。" : "网络、额度与最短间隔等条件满足时仍可能有限自动重试。")
                    : "认证消解记录写入失败；"
                        + (paused ? "自动恢复当前已关闭，不会自动重试。" : "自动提交仍受认证状态、网络与额度约束。"),
                    "检查本地存储与诊断日志后再操作；不要删除状态文件或重复提交。"),
            ManualCause.PortalLayout =>
                ("门户结构疑似变化", (pending
                        ? "上次提交结果仍未确认，同时门户结构疑似变化；该信号本身不代表认证已暂停。"
                        : snapshot?.CredentialsBlocked == true
                            ? "门户结构疑似变化，认证已暂停。"
                            : "门户结构疑似变化；该信号本身不代表认证已暂停。")
                    + (paused ? "自动恢复当前也已关闭。" : ""),
                    "导出诊断包反馈给网管；不要重复提交。"),
            ManualCause.BrowserFallback =>
                ("需要人工处理", (pending
                        ? "连续自动认证失败已达上限；请人工确认门户页面。上次提交结果仍未确认，页面不会代为提交。"
                        : "连续自动认证失败已达上限；请人工确认门户页面。打开本页不会代为提交凭据。")
                    + (paused ? "自动恢复当前也已关闭。" : ""),
                    "按网络诊断指引人工确认；不要重复提交。"),
            ManualCause.MissingSettings =>
                ("尚未保存设置", "AutoNet 还没有保存校园 Wi-Fi 或认证信息，不会发出认证请求。",
                    "请在设置中填写并保存校园 Wi-Fi 与账号密码；保存后后台会按策略尝试。"),
            ManualCause.MissingCredentials =>
                ("需要保存认证信息", "后台没有可用的认证凭据，不会发出认证请求，也无法自动认证。",
                    "请在设置中填写并保存账号密码；保存后后台会按策略尝试。"),
            _ =>
                ("需要人工处理", "自动提交已暂停或门户要求人工处理；继续自动提交不会解决问题。"
                    + (paused ? "自动恢复当前也已关闭。" : ""),
                    "请按网络诊断的指引处理，必要时在门户页面人工确认。")
        };
    }

    private static string BackgroundNote(bool trusted, StatusSnapshot? snapshot)
    {
        if (!trusted || snapshot is null)
            return "后台状态不可确认；本页不会提交凭据，也不会启动新的认证会话。";
        if (IsRecoveryPaused(snapshot))
        {
            var facts = snapshot.AuthenticationPersistentState == nameof(AuthenticationState.SubmissionPending)
                ? RecheckClause(snapshot,
                    "上次提交结果仍由只读复检处理，不会重新提交凭据。",
                    "校园 Wi-Fi 当前不可用，后台暂不发起只读复检；恢复连接后只做只读复检，不会重新提交凭据。")
                : "不会重连或自动提交凭据。";
            if (snapshot.SubmissionResolutionStorageFault) facts += "认证消解记录存储写入失败。";
            return "自动恢复已关闭；后台仅检测网络。" + facts + "本页不会提交凭据。";
        }
        if (snapshot.State == "等待设置")
            return "后台尚未保存设置；本页不会提交凭据，也不会启动新的认证会话。";
        if (MissingCredentials(snapshot))
            return "后台没有可用的认证凭据，不会发出认证请求；本页只显示状态，不会提交凭据。";
        if (snapshot.AuthChannelMode == AuthChannelModes.Automatic && snapshot.AuthChannelRequested)
            return "后台已请求内嵌认证通道；本页只显示当前状态，不会另起会发送密码的会话。";
        if (snapshot.SubmissionResolutionStorageFault)
            return snapshot.AuthenticationPersistentState == nameof(AuthenticationState.SubmissionPending)
                ? "认证消解记录存储写入失败；" + RecheckClause(snapshot,
                    "后台继续只读复检，条件满足时仍可能有限自动重试，本页不会提交凭据。",
                    "校园 Wi-Fi 当前不可用，后台暂不发起只读复检；恢复连接后再继续，条件满足时仍可能有限自动重试，本页不会提交凭据。")
                : "认证消解记录存储写入失败；自动提交仍受认证状态、网络与额度约束，本页只显示状态。";
        if (snapshot.AuthenticationPersistentState == nameof(AuthenticationState.SubmissionPending))
        {
            if (snapshot.Direct == "外网可达")
                return "外网已恢复，后台暂停提交并继续只读复检；本页不会提交凭据。";
            if (SubmissionQuotaExhausted(snapshot))
                return RecheckClause(snapshot,
                    "后台自动提交额度已用尽，只做低频只读复检；本页不会提交凭据。",
                    "后台自动提交额度已用尽；校园 Wi-Fi 当前不可用，暂不发起只读复检，恢复连接后只做低频只读复检；本页不会提交凭据。");
            return RecheckClause(snapshot,
                "后台按共享提交额度继续只读复检；条件允许时才可能有限自动重试，本页不会提交凭据。",
                "校园 Wi-Fi 当前不可用，后台暂不发起只读复检；恢复连接后按共享提交额度继续只读复检，条件允许时才可能有限自动重试，本页不会提交凭据。");
        }
        if (snapshot.CredentialsBlocked)
            return "后台自动认证当前已暂停或等待人工处理；本页只显示状态，打开或刷新不会提交凭据。";
        return "后台按协议认证运行；本页只显示状态，不代表发生过网页操作，打开或刷新本页不会提交凭据。";
    }

    private static string HostTitle(AuthPageHostStatus status) => status switch
    {
        AuthPageHostStatus.Attached => "门户页面",
        AuthPageHostStatus.RuntimeMissing => "浏览器运行时不可用",
        AuthPageHostStatus.NetworkBoundaryUnavailable => "认证页网络边界未就绪",
        AuthPageHostStatus.Failed => "门户页面加载失败",
        _ => "门户页面尚未加载"
    };

    private static string HostDetail(AuthPageHostStatus status) => status switch
    {
        AuthPageHostStatus.Attached => "已加载固定门户根地址；本页不填表、不提交，后台认证继续独立运行。",
        AuthPageHostStatus.RuntimeMissing => "未检测到 Microsoft Edge WebView2 Runtime；认证页无法显示门户页面。安装运行时后可重试。",
        AuthPageHostStatus.NetworkBoundaryUnavailable => "受控网络边界尚未接入，认证页不会连接门户，也不会回退直连。",
        AuthPageHostStatus.Failed => "浏览器宿主未能加载门户页面；可手动重试，后台认证不受影响。",
        _ => "打开本页会加载固定门户地址；本页不会填表或提交凭据。"
    };

    private static string Fingerprint(AuthPageContent content) => string.Join("\u001f",
        content.Trusted, content.Phase, content.StatusTitle, content.StatusExplanation, content.NextStep,
        content.PortalAddress, content.BackgroundNote, content.HostStatus, content.HostTitle, content.HostDetail,
        content.HostAvailable, content.HostRetryVisible);
}
