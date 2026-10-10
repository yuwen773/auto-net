namespace AutoNet.Core;

public enum ActionKind { None, ReconnectWifi, Authenticate }
public sealed record Observation(bool WifiConnected, bool DirectOnline, bool PortalNeedsLogin);

public sealed class RecoveryPolicy
{
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
        if (now < NextAttempt) return ActionKind.None;
        if (!observation.WifiConnected) return ActionKind.ReconnectWifi;
        return observation.PortalNeedsLogin && hasCredentials && !CredentialsBlocked
            ? ActionKind.Authenticate : ActionKind.None;
    }

    public void Attempted(DateTimeOffset finishedAt)
    {
        Attempts++;
        var fast = OutageSince is { } start && finishedAt - start < TimeSpan.FromSeconds(15);
        // Fast phase <= 2s gaps; long failures use 10, 20, 40, 60 second gaps.
        var delay = fast ? 2 : Math.Min(60, 10 * (1 << Math.Min(Math.Max(Attempts - 3, 0), 3)));
        NextAttempt = finishedAt.AddSeconds(delay);
    }

    public void Reset()
    {
        OutageSince = null;
        NextAttempt = DateTimeOffset.MinValue;
        Attempts = 0;
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
    public string Tunnel { get; init; } = "未配置检测";
    public string Uu { get; init; } = "未核验";
    public int Attempts { get; init; }
    public DateTimeOffset? OutageSince { get; init; }
    public DateTimeOffset? NextAttempt { get; init; }
    public double? LastRecoverySeconds { get; init; }
    public bool CredentialsBlocked { get; init; }
}
