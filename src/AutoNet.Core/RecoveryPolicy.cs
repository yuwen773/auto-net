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
    public const string FallbackPortalPageUrl = "https://login.csust.edu.cn/";

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
        if (action == ActionKind.None) return NoAction(observation.Wifi != WifiState.Ready ? observation.Condition.ToString() :
            !hasCredentials ? "CredentialsMissing" : CredentialsBlocked ? "AuthenticationPaused" :
            !allowActiveDisconnect ? "ActiveDisconnectDisabled" : "NoApplicableAction");
        if (action == ActionKind.DisconnectWifi && (DisconnectAttempts >= DisconnectAttemptCap || disconnectPending))
            return NoAction(disconnectPending ? "DisconnectCompletionUnconfirmed" : "DisconnectBudgetExhausted");
        NextAttempt = retries.TryGetValue(action, out var retry) ? retry.NextAttempt : DateTimeOffset.MinValue;
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
    public double? LastRecoverySeconds { get; init; }
    public DateTimeOffset? LastRecoveryAt { get; init; }
    public DateTimeOffset? ActionStartedAt { get; init; }
    public DateTimeOffset? ActionFinishedAt { get; init; }
    public DateTimeOffset? AuthenticationAcceptedAt { get; init; }
    public string AuthenticationStage { get; init; } = "Unknown";
    public string AuthenticationFailure { get; init; } = "None";
    public string AuthenticationPersistentState { get; init; } = "Unknown";
    public int? AuthenticationHttpStatus { get; init; }
    public bool CredentialRequestStarted { get; init; }
    public string LastError { get; init; } = "";
    public bool CredentialsBlocked { get; init; }
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

    public static bool IsKnown(string? code) => code is InternetRecovered or AuthenticationPaused
        or SubmissionRechecking or SubmissionUnresolved or BrowserFallbackRequested or PortalLayoutChanged
        or AuthChannelStarted or AuthChannelResult or AuthChannelNeedsHuman;
}

/// <summary>
/// One-shot state transition broadcast to the tray through status.json. The service stamps
/// <see cref="At"/>; the tray renders wording from <see cref="Code"/> and balloons only when At
/// advances, so the payload carries no text and needs no persistent sequence counter.
/// </summary>
public sealed record StatusNotification(string Code, DateTimeOffset At);
