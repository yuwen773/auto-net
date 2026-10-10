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
    private readonly Dictionary<ActionKind, (int Attempts, DateTimeOffset NextAttempt)> retries = [];
    public DateTimeOffset? OutageSince { get; private set; }
    public DateTimeOffset NextAttempt { get; private set; } = DateTimeOffset.MinValue;
    public int Attempts { get; private set; }
    public bool CredentialsBlocked { get; set; }

    public ActionKind Decide(Observation observation, DateTimeOffset now, bool enabled, bool hasCredentials)
    {
        if (observation.DirectOnline)
        {
            Reset();
            return ActionKind.None;
        }
        if (!enabled) { Reset(); return ActionKind.None; }
        OutageSince ??= now;
        var stalled = observation.Condition is NetworkCondition.PortalUnknown or NetworkCondition.AuthenticatedOffline
            && hasCredentials && !CredentialsBlocked;
        if (stalled) stalledSince ??= now;
        else stalledSince = null;
        var action = observation.Wifi == WifiState.Disconnected ? ActionKind.ReconnectWifi :
            observation.Condition == NetworkCondition.NeedsAuthentication && hasCredentials && !CredentialsBlocked
                ? ActionKind.Authenticate : stalled ? ActionKind.DisconnectWifi : ActionKind.None;
        NextAttempt = DateTimeOffset.MinValue;
        if (action == ActionKind.None) return ActionKind.None;
        NextAttempt = retries.TryGetValue(action, out var retry) ? retry.NextAttempt : DateTimeOffset.MinValue;
        if (action == ActionKind.DisconnectWifi && stalledSince!.Value.AddSeconds(10) > NextAttempt)
            NextAttempt = stalledSince.Value.AddSeconds(10);
        if (now < NextAttempt) return ActionKind.None;
        lastAction = action;
        return action;
    }

    public void Attempted(DateTimeOffset finishedAt)
    {
        Attempts++;
        var actionAttempts = retries.TryGetValue(lastAction, out var retry) ? retry.Attempts + 1 : 1;
        var fast = OutageSince is { } start && finishedAt - start < TimeSpan.FromSeconds(15);
        // Stalled links get a separate 60/120/240/300s cooldown, never a disconnect storm.
        var delay = lastAction == ActionKind.DisconnectWifi
            ? Math.Min(300, 60 * (1 << Math.Min(actionAttempts - 1, 3)))
            : fast ? 2 : Math.Min(60, 10 * (1 << Math.Min(Math.Max(actionAttempts - 3, 0), 3)));
        NextAttempt = finishedAt.AddSeconds(delay);
        retries[lastAction] = (actionAttempts, NextAttempt);
    }

    public void Reset()
    {
        OutageSince = null;
        NextAttempt = DateTimeOffset.MinValue;
        Attempts = 0;
        lastAction = ActionKind.None;
        stalledSince = null;
        retries.Clear();
        // Credential rejection persists until credentials change, even if connectivity recovers.
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
    public DateTimeOffset? OutageSince { get; init; }
    public DateTimeOffset? NextAttempt { get; init; }
    public double? LastRecoverySeconds { get; init; }
    public DateTimeOffset? LastRecoveryAt { get; init; }
    public DateTimeOffset? ActionStartedAt { get; init; }
    public DateTimeOffset? ActionFinishedAt { get; init; }
    public DateTimeOffset? AuthenticationAcceptedAt { get; init; }
    public string AuthenticationStage { get; init; } = "Unknown";
    public string AuthenticationFailure { get; init; } = "None";
    public int? AuthenticationHttpStatus { get; init; }
    public bool CredentialRequestStarted { get; init; }
    public string LastError { get; init; } = "";
    public bool CredentialsBlocked { get; init; }
}
