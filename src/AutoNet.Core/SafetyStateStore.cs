using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoNet.Core;

public enum DisconnectRequestState { Completed, RequestPending }
public sealed record DisconnectBudget(int Attempts = 0, DateTimeOffset NextAttempt = default)
{
    public int Version { get; init; } = 1;
    public DisconnectRequestState State { get; init; }
    public DateTimeOffset CompletedAt { get; init; } = Attempts > 0 && NextAttempt != default ? NextAttempt.AddSeconds(-120) : default;
    public Guid Revision { get; init; }
}
public enum AuthenticationState { Ready, SubmissionPending, RetryCoolingDown, BindingRejected, CredentialsRejected, InteractionRequired, SafetyBlocked }
public sealed record AuthenticationBlock(PortalFailure Failure = PortalFailure.None)
{
    public int Version { get; init; } = 1;
    public AuthenticationState State { get; init; } = Failure switch
    {
        PortalFailure.BindingRejected => AuthenticationState.BindingRejected,
        PortalFailure.CredentialsRejected => AuthenticationState.CredentialsRejected,
        PortalFailure.InteractionRequired => AuthenticationState.InteractionRequired,
        _ => AuthenticationState.SafetyBlocked
    };
    public int Attempts { get; init; }
    public int RetryDelaySeconds { get; init; }
    public DateTimeOffset RetryNotBefore { get; init; }
    public Guid Revision { get; init; }
    public bool BlocksSubmissions => State is not (AuthenticationState.Ready or AuthenticationState.RetryCoolingDown);
}

// Uses the installer's restricted private directory; fixtures supply an isolated directory.
public sealed class SafetyStateStore(string directory)
{
    private string AuthBlockPath => Path.Combine(directory, "auth-block.txt");

    public AuthenticationBlock? LoadAuthenticationBlock()
    {
        using var gate = AuthenticationGate();
        return ReadAuthenticationBlock();
    }

    private AuthenticationBlock? ReadAuthenticationBlock()
    {
        if (!Exists(AuthBlockPath)) return null;
        try
        {
            using var json = JsonDocument.Parse(Storage.ReadShared(AuthBlockPath));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new();
            // Old fingerprints and failure-only records always remain a manual pause.
            if (!root.TryGetProperty("Version", out _))
            {
                var legacy = JsonSerializer.Deserialize<AuthenticationBlock>(root, Storage.Json);
                return legacy is not null && Enum.IsDefined(legacy.Failure) ? new(legacy.Failure) : new();
            }
            if (!HasFields(root, "Version", "State", "Failure", "Attempts", "RetryDelaySeconds", "RetryNotBefore", "Revision")) return new();
            var block = JsonSerializer.Deserialize<AuthenticationBlock>(root, Storage.Json);
            if (block is null || block.Version != 1 || !Enum.IsDefined(block.State) || !Enum.IsDefined(block.Failure) ||
                block.Attempts is < 0 or > 3 || block.State == AuthenticationState.SubmissionPending && block.Attempts == 0 ||
                block.State == AuthenticationState.RetryCoolingDown &&
                    (block.RetryDelaySeconds is < 2 or > 60 || block.RetryNotBefore == default) ||
                block.State != AuthenticationState.RetryCoolingDown && (block.RetryDelaySeconds != 0 || block.RetryNotBefore != default)) return new();
            var consistent = block.State switch
            {
                AuthenticationState.Ready or AuthenticationState.SubmissionPending => block.Failure == PortalFailure.None,
                AuthenticationState.RetryCoolingDown => block.Attempts > 0 && block.Failure is
                    PortalFailure.None or PortalFailure.HttpStatus or PortalFailure.MissingClientIpv4 or PortalFailure.MissingGatewayParameters or
                    PortalFailure.Timeout or PortalFailure.Transport or PortalFailure.StatusUnknown or PortalFailure.LoginRejected or PortalFailure.InvalidTarget,
                AuthenticationState.BindingRejected => block.Failure == PortalFailure.BindingRejected,
                AuthenticationState.CredentialsRejected => block.Failure == PortalFailure.CredentialsRejected,
                AuthenticationState.InteractionRequired => block.Failure == PortalFailure.InteractionRequired,
                _ => true
            };
            if (!consistent) return new();
            return block;
        }
        // Legacy fingerprints and damaged pause records remain blocked until manual release.
        catch (JsonException) { return new(); }
    }

    public void BlockAuthentication(AuthenticationBlock block)
    {
        using var gate = AuthenticationGate();
        Storage.AtomicWrite(AuthBlockPath, JsonSerializer.Serialize(block, Storage.Json));
    }

    public bool TryTransitionAuthentication(AuthenticationBlock? expected, AuthenticationBlock next)
    {
        using var gate = AuthenticationGate();
        if (ReadAuthenticationBlock() != expected) return false;
        Storage.AtomicWrite(AuthBlockPath, JsonSerializer.Serialize(next, Storage.Json));
        return true;
    }

    public void ClearAuthenticationBlock(AuthenticationBlock? expected = null)
    {
        using var gate = AuthenticationGate();
        var previous = ReadAuthenticationBlock();
        if (expected is not null && previous != expected) throw new InvalidOperationException("认证安全状态已变化，请重新诊断后确认。");
        if (previous is null) return;
        var audit = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            At = DateTimeOffset.UtcNow, Action = "人工确认解除认证暂停", previous.State, previous.Failure
        }) + Environment.NewLine);
        using (var file = new FileStream(Path.Combine(directory, "auth-release-audit.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            file.Write(audit); file.Flush(true);
        }
        File.Delete(AuthBlockPath);
    }

    private FileStream AuthenticationGate() => new(Path.Combine(directory, "auth-state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool HasFields(JsonElement root, params string[] names) =>
        names.All(name => root.EnumerateObject().Count(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 1);

    public DisconnectBudget LoadDisconnectBudget(Settings settings)
    {
        using var gate = BudgetGate(settings);
        return ReadDisconnectBudget(settings);
    }

    private DisconnectBudget ReadDisconnectBudget(Settings settings)
    {
        var path = BudgetPath(settings);
        if (!Exists(path)) return new();
        using var json = JsonDocument.Parse(Storage.ReadShared(path));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !HasFields(root, "Attempts", "NextAttempt")) throw InvalidBudget();
        var legacy = !root.EnumerateObject().Any(p => p.Name.Equals("Version", StringComparison.OrdinalIgnoreCase));
        if (legacy ? root.EnumerateObject().Any(p => !new[] { "Attempts", "NextAttempt" }.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) :
            !HasFields(root, "Version", "State", "CompletedAt", "Revision")) throw InvalidBudget();
        var budget = JsonSerializer.Deserialize<DisconnectBudget>(root, Storage.Json);
        if (budget is null || budget.Version != 1 || !Enum.IsDefined(budget.State) || budget.Attempts is < 0 or > 3 ||
            budget.Attempts > 0 && budget.NextAttempt == default ||
            budget.State == DisconnectRequestState.RequestPending && (budget.Attempts == 0 || budget.CompletedAt != default) ||
            budget.State == DisconnectRequestState.Completed && budget.Attempts > 0 && budget.CompletedAt == default)
            throw new InvalidDataException("主动断线额度记录无效；停止自动操作，需检查本地状态文件。");
        // The only accepted old schema has both required fields; migrate without granting an extra attempt.
        if (legacy) Storage.AtomicWrite(path, JsonSerializer.Serialize(budget, Storage.Json));
        return budget;
    }

    public void SaveDisconnectBudget(Settings settings, DisconnectBudget budget)
    {
        using var gate = BudgetGate(settings);
        Storage.AtomicWrite(BudgetPath(settings), JsonSerializer.Serialize(budget, Storage.Json));
    }

    public bool TryTransitionDisconnectBudget(Settings settings, DisconnectBudget expected, DisconnectBudget next)
    {
        using var gate = BudgetGate(settings);
        if (ReadDisconnectBudget(settings) != expected) return false;
        Storage.AtomicWrite(BudgetPath(settings), JsonSerializer.Serialize(next, Storage.Json));
        return true;
    }

    private static InvalidDataException InvalidBudget() => new("主动断线额度结构或版本无效；停止自动操作，需检查本地状态文件。");
    private FileStream BudgetGate(Settings settings) => new(BudgetPath(settings) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private string BudgetPath(Settings settings)
    {
        var key = Guid.Parse(settings.InterfaceId).ToString("N") + "\n" + settings.WifiProfile;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(directory, "disconnect-" + hash + ".json");
    }
}
