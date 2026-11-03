namespace AutoNet.Core;

public enum ActionKind { None, ReconnectWifi, Authenticate, DisconnectWifi }
public enum WifiState { Unavailable, Disconnected, Associating, WaitingForAddress, Ready, OtherProfile }
public enum NetworkCondition { Online, InterfaceUnavailable, WifiDisconnected, Associating, WaitingForAddress, OtherProfile, PortalUnknown, NeedsAuthentication, AuthenticatedOffline }
public sealed record Observation(WifiState Wifi, bool DirectOnline, PortalState Portal)
{
    public Observation(bool wifiConnected, bool directOnline, bool portalNeedsLogin)
        : this(wifiConnected ? WifiState.Ready : WifiState.Disconnected, directOnline, portalNeedsLogin ? PortalState.NeedsLogin : PortalState.Unknown) { }

    public NetworkCondition Condition => DirectOnline ? NetworkCondition.Online : Wifi switch
    {
        WifiState.Unavailable => NetworkCondition.InterfaceUnavailable,
        WifiState.Disconnected => NetworkCondition.WifiDisconnected,
        WifiState.Associating => NetworkCondition.Associating,
        WifiState.WaitingForAddress => NetworkCondition.WaitingForAddress,
        WifiState.OtherProfile => NetworkCondition.OtherProfile,
        _ => Portal switch { PortalState.LoggedIn => NetworkCondition.AuthenticatedOffline, PortalState.NeedsLogin => NetworkCondition.NeedsAuthentication, _ => NetworkCondition.PortalUnknown }
    };
}

public sealed class RecoveryPolicy
{
    // Single source of truth for the stall-link fallback. The tray renders these values instead of
    // repeating them, so a policy change cannot silently drift from what the window claims.
    public const int DisconnectAttemptCap = 3;
    public const int StalledRoundThreshold = 3;
    public const int AuthenticationAttemptCap = 3;
    public const int DisconnectCooldownSeconds = 120;
    public const int OnlineResetSeconds = 60;
    public const int StalledObservationSeconds = 30;
    public const int FastRetrySeconds = 2;
    public const int FastRetryWindowSeconds = 15;
    public const int BackoffCeilingSeconds = 60;

    /// <summary>
    /// Browser-fallback open cadence (ADR-0008), shared by the service grant stamp and the tray's
    /// own local cooldown so the two sides cannot drift apart. The tray's first open is immediate;
    /// this span gates only later automatic opens (a re-granted episode still waits it out).
    /// </summary>
    public static readonly TimeSpan BrowserFallbackOpenCooldown = TimeSpan.FromMinutes(10);

    /// <summary>内嵌认证通道与浏览器兜底共用同一冷却节奏（工单 #24 验收：冷却同源）。</summary>
    public static readonly TimeSpan AuthChannelCooldown = BrowserFallbackOpenCooldown;

    /// <summary>
    /// 内嵌认证通道自动降级判定（工单 #24）：仅 Automatic 模式下，门户结构改版信号
    /// 与「需要人工交互」的认证失败达到连续尝试阈值同时成立才发起；与浏览器兜底
    /// 同源（阈值/失败形态相同），ManualOnly/Off 永不自动发起。
    /// </summary>
    public static bool ShouldRequestAuthChannel(string? mode, bool portalLayoutSuspected, PortalFailure blockFailure, int attempts)
        => mode == AuthChannelModes.Automatic
            && portalLayoutSuspected
            && blockFailure == PortalFailure.InteractionRequired
            && attempts >= AuthenticationAttemptCap;

    /// <summary>
    /// The only page the tray may open for the browser fallback (ADR-0008): the fixed portal root.
    /// Opens are validated against <see cref="Settings.IsSupportedPortal"/> before launch, so a
    /// future drift here fails closed instead of navigating anywhere else.
    /// </summary>
    public const string FallbackPortalPageUrl = "https://" + Settings.PortalHost + "/";

    private ActionKind lastAction;
    private DateTimeOffset? stalledSince;
    private DateTimeOffset? onlineSince;
    private int stalledRounds;
    private bool disconnectPending;
    private DateTimeOffset disconnectCompletedAt;
    private readonly Dictionary<ActionKind, (int Attempts, DateTimeOffset NextAttempt)> retries = [];
    public int DisconnectAttempts { get; private set; }
    public DateTimeOffset NextDisconnectAttempt { get; private set; } = DateTimeOffset.MinValue;
    public DisconnectBudget DisconnectBudget => new(DisconnectAttempts, NextDisconnectAttempt)
    {
        State = disconnectPending ? DisconnectRequestState.RequestPending : DisconnectRequestState.Completed,
        CompletedAt = disconnectPending ? default : disconnectCompletedAt
    };
    public DateTimeOffset? OutageSince { get; private set; }
    public DateTimeOffset NextAttempt { get; private set; } = DateTimeOffset.MinValue;
    /// <summary>
    /// 与 <see cref="NextAttempt"/> 对应的动作种类。通用退避时间也可能来自 Wi-Fi 重连，
    /// 界面不能把它当成认证重试；此字段让投影能区分两者。
    /// </summary>
    public ActionKind NextAttemptAction { get; private set; }
    public int Attempts { get; private set; }
    public bool CredentialsBlocked { get; set; }
    public string DecisionReason { get; private set; } = "NotEvaluated";
    public int FailedRounds => stalledRounds;
    public double ObservationSeconds(DateTimeOffset now) => stalledSince is { } since ? Math.Max(0, (now - since).TotalSeconds) : 0;

    private ActionKind NoAction(string reason) { DecisionReason = reason; return ActionKind.None; }

    public ActionKind Decide(Observation observation, DateTimeOffset now, bool enabled, bool hasCredentials, bool allowActiveDisconnect = true)
    {
        if (observation.DirectOnline)
        {
            var since = onlineSince ?? now;
            Reset();
            onlineSince = since;
            if (!disconnectPending && now - since >= TimeSpan.FromSeconds(OnlineResetSeconds))
            {
                DisconnectAttempts = 0;
                NextDisconnectAttempt = DateTimeOffset.MinValue;
                disconnectCompletedAt = default;
            }
            return NoAction("Online");
        }
        onlineSince = null;
        if (!enabled) { Reset(); return NoAction("RecoveryDisabled"); }
        OutageSince ??= now;
        var stalled = observation.Condition is NetworkCondition.PortalUnknown or NetworkCondition.AuthenticatedOffline
            && hasCredentials && !CredentialsBlocked && allowActiveDisconnect;
        if (stalled) { stalledSince ??= now; stalledRounds++; }
        else { stalledSince = null; stalledRounds = 0; }
        var action = observation.Wifi == WifiState.Disconnected ? ActionKind.ReconnectWifi :
            observation.Condition == NetworkCondition.NeedsAuthentication && hasCredentials && !CredentialsBlocked
                ? ActionKind.Authenticate : stalled ? ActionKind.DisconnectWifi : ActionKind.None;
        NextAttempt = DateTimeOffset.MinValue;
        NextAttemptAction = ActionKind.None;
        if (action == ActionKind.None) return NoAction(observation.Wifi != WifiState.Ready ? observation.Condition.ToString() :
            !hasCredentials ? "CredentialsMissing" : CredentialsBlocked ? "AuthenticationPaused" :
            !allowActiveDisconnect ? "ActiveDisconnectDisabled" : "NoApplicableAction");
        if (action == ActionKind.DisconnectWifi && (DisconnectAttempts >= DisconnectAttemptCap || disconnectPending))
            return NoAction(disconnectPending ? "DisconnectCompletionUnconfirmed" : "DisconnectBudgetExhausted");
        NextAttempt = retries.TryGetValue(action, out var retry) ? retry.NextAttempt : DateTimeOffset.MinValue;
        NextAttemptAction = action;
        if (action == ActionKind.DisconnectWifi)
        {
            NextAttempt = NextDisconnectAttempt > stalledSince!.Value.AddSeconds(StalledObservationSeconds)
                ? NextDisconnectAttempt : stalledSince.Value.AddSeconds(StalledObservationSeconds);
            if (stalledRounds < StalledRoundThreshold) return NoAction("InsufficientFailureSamples");
        }
        if (now < NextAttempt) return NoAction(action == ActionKind.DisconnectWifi && now < stalledSince!.Value.AddSeconds(StalledObservationSeconds)
            ? "ObservationWindow" : action + "Cooldown");
        lastAction = action;
        DecisionReason = action + "Required";
        return action;
    }

    public void Attempted(DateTimeOffset finishedAt)
    {
        Attempts++;
        var actionAttempts = retries.TryGetValue(lastAction, out var retry) ? retry.Attempts + 1 : 1;
        var fast = OutageSince is { } start && finishedAt - start < TimeSpan.FromSeconds(FastRetryWindowSeconds);
        // Reserve destructive actions before invoking Windows, including failed requests.
        var delay = lastAction == ActionKind.DisconnectWifi
            ? DisconnectCooldownSeconds
            : fast ? FastRetrySeconds : Math.Min(BackoffCeilingSeconds, 10 * (1 << Math.Min(Math.Max(actionAttempts - 3, 0), 3)));
        NextAttempt = finishedAt.AddSeconds(delay);
        NextAttemptAction = lastAction;
        retries[lastAction] = (actionAttempts, NextAttempt);
        if (lastAction == ActionKind.DisconnectWifi)
        {
            DisconnectAttempts++;
            NextDisconnectAttempt = NextAttempt;
            disconnectPending = true;
        }
    }

    public void RestoreDisconnectBudget(DisconnectBudget budget, DateTimeOffset now)
    {
        DisconnectAttempts = budget.Attempts;
        disconnectPending = budget.State == DisconnectRequestState.RequestPending;
        disconnectCompletedAt = budget.CompletedAt;
        // A monotonic clock cannot survive reboot. Re-arm the full cooldown on load
        // so restarting or advancing the wall clock cannot shorten it.
        NextDisconnectAttempt = budget.Attempts == 0 ? DateTimeOffset.MinValue : now.AddSeconds(DisconnectCooldownSeconds);
    }

    public void DisconnectCompleted(DisconnectBudget budget, DateTimeOffset now)
    {
        disconnectPending = false;
        disconnectCompletedAt = budget.CompletedAt;
        NextAttempt = NextDisconnectAttempt = now.AddSeconds(DisconnectCooldownSeconds);
        NextAttemptAction = ActionKind.DisconnectWifi;
    }

    public void CancelUnissuedDisconnectReservation() => disconnectPending = false;

    public void InterruptObservation()
    {
        stalledSince = onlineSince = null;
        stalledRounds = 0;
    }

    public void DeferAuthenticationUntil(DateTimeOffset until)
    {
        var retry = retries.GetValueOrDefault(ActionKind.Authenticate);
        if (until > retry.NextAttempt) retries[ActionKind.Authenticate] = (retry.Attempts, until);
    }

    public void Reset()
    {
        OutageSince = null;
        NextAttempt = DateTimeOffset.MinValue;
        NextAttemptAction = ActionKind.None;
        Attempts = 0;
        lastAction = ActionKind.None;
        stalledSince = null;
        stalledRounds = 0;
        onlineSince = null;
        retries.Clear();
        // Persistent disconnect budget and authentication pause survive transient resets.
    }
}

public sealed record StatusSnapshot
{
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string State { get; init; } = "启动中";
    public string Detail { get; init; } = "";
    public bool Enabled { get; init; }
    /// <summary>
    /// 非秘密配置投影是否来自后台已加载的设置。旧版服务缺省 false：界面不得把默认值
    /// 当成已保存的配置，也不得据此覆盖受限存储里的既有选项。
    /// </summary>
    public bool SettingsProjected { get; init; }
    /// <summary>
    /// 已保存的校园网账号，仅用于普通权限设置页预填（配置投影的一部分）；密码永不进入快照。
    /// 诊断包收录 status.json 前会剔除该字段，保持“不含账号”的既有契约。
    /// </summary>
    public string Username { get; init; } = "";
    public string InterfaceId { get; init; } = "";
    public string WifiProfile { get; init; } = "";
    /// <summary>
    /// 代理地址的安全展示值：不含隐藏部分时是完整地址，否则只含 scheme://host[:port]。
    /// 完整地址可能带令牌，绝不能写入对普通用户可读的 status.json 或诊断包。
    /// </summary>
    public string ProxyUrlDisplay { get; init; } = "";
    /// <summary>已保存代理地址含未展示的路径、查询或片段；保存端未编辑时必须保留原值。</summary>
    public bool ProxyUrlRedacted { get; init; }
    public string UuEndpointDisplay { get; init; } = "";
    /// <summary>已保存 UU 地址含未展示的路径；保存端未编辑时必须保留原值。</summary>
    public bool UuEndpointRedacted { get; init; }
    public bool AllowActiveDisconnect { get; init; } = true;
    public bool KeepAwakeOnAc { get; init; } = true;
    public string Wifi { get; init; } = "待检测";
    public string Direct { get; init; } = "待检测";
    public string Portal { get; init; } = "待检测";
    public string Tunnel { get; init; } = "未配置检测";
    public string Uu { get; init; } = "未核验";
    public int Attempts { get; init; }
    public int DisconnectAttempts { get; init; }
    /// <summary>Projected from <see cref="RecoveryPolicy.DisconnectAttemptCap"/> so the tray never restates the cap.</summary>
    public int DisconnectBudgetMax { get; init; } = RecoveryPolicy.DisconnectAttemptCap;
    /// <summary>Projected from <see cref="RecoveryPolicy.DisconnectCooldownSeconds"/>; drives the "next allowed retry" hint.</summary>
    public int DisconnectCooldownSeconds { get; init; } = RecoveryPolicy.DisconnectCooldownSeconds;
    /// <summary>Projected from <see cref="RecoveryPolicy.OnlineResetSeconds"/>; explains when the quota returns.</summary>
    public int DisconnectResetAfterSeconds { get; init; } = RecoveryPolicy.OnlineResetSeconds;
    /// <summary>True while a disconnect request is reserved but its completion was never durably confirmed.</summary>
    public bool DisconnectRequestUnconfirmed { get; init; }
    public DateTimeOffset? OutageSince { get; init; }
    public DateTimeOffset? NextAttempt { get; init; }
    /// <summary>
    /// <see cref="NextAttempt"/> 对应的动作种类（None/ReconnectWifi/Authenticate/DisconnectWifi）。
    /// 旧版状态缺省为 None：界面不得把未知来源的退避时间当成认证重试。
    /// </summary>
    public string NextAttemptAction { get; init; } = nameof(ActionKind.None);
    public double? LastRecoverySeconds { get; init; }
    public DateTimeOffset? LastRecoveryAt { get; init; }
    /// <summary>
    /// Last cycle duration measured on the monotonic clock from the start of the round up to
    /// (but excluding) the status write that carries it — a value cannot include the write that
    /// is writing it. The full duration including that write is recorded in the cycle's
    /// <c>SlowCycle</c> log entry. Older producers omit it; the tray treats null as "not reported".
    /// </summary>
    public double? CycleMilliseconds { get; init; }
    /// <summary>Aggregated slowest stage of the last cycle; a fixed diagnostic name, never page text.</summary>
    public string SlowestStage { get; init; } = "";
    /// <summary>Aggregated milliseconds for <see cref="SlowestStage"/>; null when no stage was measured.</summary>
    public double? SlowestStageMilliseconds { get; init; }
    public DateTimeOffset? ActionStartedAt { get; init; }
    public DateTimeOffset? ActionFinishedAt { get; init; }
    public DateTimeOffset? AuthenticationAcceptedAt { get; init; }
    public string AuthenticationStage { get; init; } = "Unknown";
    public string AuthenticationFailure { get; init; } = "None";
    /// <summary>消解写入故障的进程内观测；旧版状态缺省为 false，不代表已验证存储正常。</summary>
    public bool SubmissionResolutionStorageFault { get; init; }
    public string AuthenticationPersistentState { get; init; } = "Unknown";
    public int? AuthenticationHttpStatus { get; init; }
    public bool CredentialRequestStarted { get; init; }
    public string LastError { get; init; } = "";
    public bool CredentialsBlocked { get; init; }
    /// <summary>
    /// #35 提交额度级别的受控码（见 <see cref="SubmissionBudgetCodes"/>）。旧版服务不写该字段，
    /// 缺省为空字符串；托盘必须按“不可确认”降级，不得据此推断次数或放行提交。
    /// </summary>
    public string SubmissionBudgetCode { get; init; } = "";
    /// <summary>#35 受控重试决定码（<see cref="SubmissionRetryDecision"/> 的名称）；旧版缺省为空字符串。</summary>
    public string SubmissionRetryCode { get; init; } = "";
    /// <summary>#35 已用提交次数；null 表示快照未提供（旧版服务），不是零次。</summary>
    public int? SubmissionAttempts { get; init; }
    /// <summary>#35 提交额度上限；null 表示快照未提供。</summary>
    public int? SubmissionAttemptMax { get; init; }
    /// <summary>#35 额度是否耗尽；null 表示快照未提供。</summary>
    public bool? SubmissionBudgetExhausted { get; init; }
    /// <summary>#35 最早下次允许提交的墙钟时刻；null 表示当前没有可信的下次时刻。</summary>
    public DateTimeOffset? SubmissionNextPossibleAt { get; init; }
    /// <summary>
    /// #35 相邻两次可能发出凭据的提交至少间隔的秒数；旧版服务不写该字段时缺省为 0，
    /// 托盘不得把它当成“无需间隔”，只在确有正数时展示。
    /// </summary>
    public int SubmissionCooldownSeconds { get; init; }
    /// <summary>
    /// #35 链路状态的受控码（见 <see cref="WifiLinkCodes"/>）。旧版服务不写该字段时缺省为空，
    /// 托盘必须按“链路不可确认”降级，不得从 <see cref="Wifi"/> 的显示文字猜测是否就绪。
    /// </summary>
    public string WifiLinkCode { get; init; } = "";
    /// <summary>ADR-0005: the latest portal observation fell in the structural failure set; the portal's shape is suspected to have changed.</summary>
    public bool PortalLayoutSuspected { get; init; }
    /// <summary>
    /// ADR-0002: consecutive authentication failures reached the cap; the tray should open the portal page in the user session.
    /// </summary>
    public bool BrowserFallbackRequested { get; init; }
    /// <summary>
    /// Grant time plus the 10-minute cooldown, stamped once per episode. The tray owns first-open
    /// dedup and its own repeat cadence; the service never refreshes it while the flag stays up,
    /// and a service restart re-grants it.
    /// </summary>
    public DateTimeOffset? BrowserFallbackNotBefore { get; init; }
    /// <summary>
    /// ADR-0009: portal layout is suspected to have changed AND the interaction-required failure
    /// reached the cap — the tray should launch the embedded auth channel (UAC-elevated, user
    /// session). Only set when the configured mode is Automatic; ManualOnly/Off never set it.
    /// </summary>
    public bool AuthChannelRequested { get; init; }
    /// <summary>Grant time plus the shared 10-minute cooldown, stamped once per episode like the browser fallback.</summary>
    public DateTimeOffset? AuthChannelNotBefore { get; init; }
    /// <summary>内嵌认证通道模式广播（诊断页只读投影用）；服务从设置读出，未知设置缺省 ManualOnly。</summary>
    public string AuthChannelMode { get; init; } = AuthChannelModes.ManualOnly;
    /// <summary>Latest one-shot notification, or null when none was emitted; older builds omit the field.</summary>
    public StatusNotification? Notification { get; init; }
}

/// <summary>Controlled vocabulary for <see cref="StatusNotification.Code"/>; the tray owns the wording.</summary>
public static class NotificationCodes
{
    public const string InternetRecovered = "InternetRecovered";
    public const string AuthenticationPaused = "AuthenticationPaused";
    public const string SubmissionRechecking = "SubmissionRechecking";
    public const string SubmissionUnresolved = "SubmissionUnresolved";
    public const string BrowserFallbackRequested = "BrowserFallbackRequested";
    public const string PortalLayoutChanged = "PortalLayoutChanged";
    public const string AuthChannelStarted = "AuthChannelStarted";
    public const string AuthChannelResult = "AuthChannelResult";
    public const string AuthChannelNeedsHuman = "AuthChannelNeedsHuman";
    /// <summary>#35 提交额度用尽、自动提交已停止的一次性提示。</summary>
    public const string SubmissionBudgetExhausted = "SubmissionBudgetExhausted";
    /// <summary>#35 提交额度即将用尽、只剩最后一次自动提交机会的一次性提示。</summary>
    public const string SubmissionBudgetNearLimit = "SubmissionBudgetNearLimit";
    /// <summary>#35 提交额度已由门户确认成功或人工重新开始认证重置的一次性提示。</summary>
    public const string SubmissionBudgetReset = "SubmissionBudgetReset";

    public static bool IsKnown(string? code) => code is InternetRecovered or AuthenticationPaused
        or SubmissionRechecking or SubmissionUnresolved or BrowserFallbackRequested or PortalLayoutChanged
        or AuthChannelStarted or AuthChannelResult or AuthChannelNeedsHuman
        or SubmissionBudgetExhausted or SubmissionBudgetNearLimit or SubmissionBudgetReset;
}

/// <summary>
/// One-shot state transition broadcast to the tray through status.json. The service stamps
/// <see cref="At"/>; the tray renders wording from <see cref="Code"/> and balloons only when At
/// advances, so the payload carries no text and needs no persistent sequence counter.
/// </summary>
public sealed record StatusNotification(string Code, DateTimeOffset At);
