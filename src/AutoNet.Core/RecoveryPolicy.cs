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

    public ActionKind Decide(Observation observation, DateTimeOffset now, bool enabled, bool hasCredentials, bool allowActiveDisconnect = true)
    {
        if (observation.DirectOnline)
        {
            var since = onlineSince ?? now;
            Reset();
            onlineSince = since;
            if (!disconnectPending && now - since >= TimeSpan.FromSeconds(60))
            {
                DisconnectAttempts = 0;
                NextDisconnectAttempt = DateTimeOffset.MinValue;
                disconnectCompletedAt = default;
            }
            return ActionKind.None;
        }
        onlineSince = null;
        if (!enabled) { Reset(); return ActionKind.None; }
        OutageSince ??= now;
        var stalled = observation.Condition is NetworkCondition.PortalUnknown or NetworkCondition.AuthenticatedOffline
            && hasCredentials && !CredentialsBlocked && allowActiveDisconnect;
        if (stalled) { stalledSince ??= now; stalledRounds++; }
        else { stalledSince = null; stalledRounds = 0; }
        var action = observation.Wifi == WifiState.Disconnected ? ActionKind.ReconnectWifi :
            observation.Condition == NetworkCondition.NeedsAuthentication && hasCredentials && !CredentialsBlocked
                ? ActionKind.Authenticate : stalled ? ActionKind.DisconnectWifi : ActionKind.None;
        NextAttempt = DateTimeOffset.MinValue;
        if (action == ActionKind.None) return ActionKind.None;
        if (action == ActionKind.DisconnectWifi && (DisconnectAttempts >= 3 || disconnectPending)) return ActionKind.None;
        NextAttempt = retries.TryGetValue(action, out var retry) ? retry.NextAttempt : DateTimeOffset.MinValue;
        if (action == ActionKind.DisconnectWifi)
        {
            NextAttempt = NextDisconnectAttempt > stalledSince!.Value.AddSeconds(30)
                ? NextDisconnectAttempt : stalledSince.Value.AddSeconds(30);
            if (stalledRounds < 3) return ActionKind.None;
        }
        if (now < NextAttempt) return ActionKind.None;
        lastAction = action;
        return action;
    }

    public void Attempted(DateTimeOffset finishedAt)
    {
        Attempts++;
        var actionAttempts = retries.TryGetValue(lastAction, out var retry) ? retry.Attempts + 1 : 1;
        var fast = OutageSince is { } start && finishedAt - start < TimeSpan.FromSeconds(15);
        // Reserve destructive actions before invoking Windows, including failed requests.
        var delay = lastAction == ActionKind.DisconnectWifi
            ? 120
            : fast ? 2 : Math.Min(60, 10 * (1 << Math.Min(Math.Max(actionAttempts - 3, 0), 3)));
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
        NextDisconnectAttempt = budget.Attempts == 0 ? DateTimeOffset.MinValue : now.AddSeconds(120);
    }

    public void DisconnectCompleted(DisconnectBudget budget, DateTimeOffset now)
    {
        disconnectPending = false;
        disconnectCompletedAt = budget.CompletedAt;
        NextAttempt = NextDisconnectAttempt = now.AddSeconds(120);
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
}
