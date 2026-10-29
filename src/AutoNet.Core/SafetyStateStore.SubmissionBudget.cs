using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoNet.Core;

public enum SubmissionBudgetState { Reserved, PossiblySent, Concluded }
public sealed record SubmissionBudget
{
    public int Version { get; init; } = 1;
    public SubmissionBudgetState State { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset ReservedAt { get; init; }
    public DateTimeOffset NextPossibleAt { get; init; }
    public Guid Revision { get; init; }
}

public enum SubmissionReservationOutcome { Reserved, StateChanged, Waiting, WaitClock, Exhausted, InFlight, Concluded, InvalidState, StorageUnavailable }
public sealed record SubmissionReservation(SubmissionReservationOutcome Outcome, SubmissionBudget? Budget = null);

public sealed partial class SafetyStateStore
{
    private string SubmissionBudgetPath => Path.Combine(directory, "submission-budget.json");
    private static readonly JsonSerializerOptions SubmissionJson = new()
    {
        MaxDepth = 4,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<SubmissionBudgetState>(allowIntegerValues: false) }
    };

    public SubmissionBudget? LoadSubmissionBudget()
    {
        using var gate = AuthenticationGate();
        return ReadSubmissionBudget();
    }

    private SubmissionBudget? ReadSubmissionBudget()
    {
        if (!Exists(SubmissionBudgetPath)) return null;
        try
        {
            using var json = JsonDocument.Parse(Storage.ReadShared(SubmissionBudgetPath), new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            string[] fields = ["Version", "State", "Attempts", "ReservedAt", "NextPossibleAt", "Revision"];
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields.Length ||
                !HasFields(root, fields) || root.EnumerateObject().Any(property => !fields.Contains(property.Name, StringComparer.Ordinal)) ||
                root.GetProperty("State").ValueKind != JsonValueKind.String)
                throw InvalidSubmissionBudget();
            var budget = JsonSerializer.Deserialize<SubmissionBudget>(root, SubmissionJson);
            if (budget is null || budget.Version != 1 || budget.Revision == Guid.Empty || budget.Attempts is < 0 or > 3 ||
                !Enum.IsDefined(budget.State) || root.GetProperty("State").GetString() != budget.State.ToString() ||
                (budget.Attempts == 0 ? budget.State != SubmissionBudgetState.Concluded || budget.ReservedAt != default || budget.NextPossibleAt != default :
                    budget.ReservedAt == default || budget.NextPossibleAt - budget.ReservedAt < TimeSpan.FromSeconds(120)))
                throw InvalidSubmissionBudget();
            return budget;
        }
        catch (JsonException) { throw InvalidSubmissionBudget(); }
    }

    private static InvalidDataException InvalidSubmissionBudget() => new("提交额度记录结构、版本或状态无效；停止凭据提交，需检查本地状态文件。");

    public bool TryRestartAuthentication(AuthenticationBlock? expectedAuthentication, SubmissionBudget? expectedBudget)
    {
        using var execution = TryAcquireSubmissionExecution();
        if (execution is null) return false;
        using var gate = AuthenticationGate();
        if (ReadAuthenticationBlock() != expectedAuthentication || ReadSubmissionBudget() != expectedBudget) return false;
        AppendSubmissionResetAudit("人工确认重新开始认证", expectedBudget);
        var reset = NewResetSubmissionBudget();
        Storage.AtomicWrite(Path.Combine(directory, "submission-restart.json"), JsonSerializer.Serialize(new { Version = 1, reset.Revision }));
        WriteResetSubmissionBudget(reset);
        return true;
    }

    public bool ResolveSubmissionPending(AuthenticationBlock? expectedAuthentication, SubmissionBudget? expectedBudget, AuthenticationBlock next, bool directConfirmation = false)
    {
        using var gate = AuthenticationGate();
        if (expectedAuthentication?.State != AuthenticationState.SubmissionPending || next.State != AuthenticationState.Ready ||
            ReadAuthenticationBlock() != expectedAuthentication || ReadSubmissionBudget() != expectedBudget) return false;
        AppendReleaseAudit(directConfirmation ? "门户提交成功消解" : "复检消解", expectedAuthentication);
        AppendSubmissionResetAudit("门户确认成功重置提交额度", expectedBudget);
        WriteResetSubmissionBudget(NewResetSubmissionBudget());
        Storage.AtomicWrite(AuthBlockPath, JsonSerializer.Serialize(next, Storage.Json));
        return true;
    }

    private void AppendSubmissionResetAudit(string action, SubmissionBudget? previous)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            At = DateTimeOffset.UtcNow, Action = action, Attempts = previous?.Attempts ?? 0,
            Revision = previous?.Revision
        }) + Environment.NewLine);
        using var file = new FileStream(Path.Combine(directory, "auth-release-audit.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
        file.Write(bytes);
        file.Flush(true);
    }

    private static SubmissionBudget NewResetSubmissionBudget() => new() { State = SubmissionBudgetState.Concluded, Revision = Guid.NewGuid() };

    private void WriteResetSubmissionBudget(SubmissionBudget budget) => Storage.AtomicWrite(SubmissionBudgetPath,
        JsonSerializer.Serialize(budget, SubmissionJson));

    public bool IsConfirmedSubmissionRestart(SubmissionBudget? budget)
    {
        if (budget is not { Attempts: 0, State: SubmissionBudgetState.Concluded }) return false;
        try
        {
            using var gate = AuthenticationGate();
            if (ReadSubmissionBudget() != budget) return false;
            var path = Path.Combine(directory, "submission-restart.json");
            if (!Exists(path)) return false;
            using var document = JsonDocument.Parse(Storage.ReadShared(path));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 2 &&
                HasFields(root, "Version", "Revision") && root.TryGetProperty("Version", out var versionValue) &&
                versionValue.ValueKind == JsonValueKind.Number && versionValue.TryGetInt32(out var version) && version == 1 &&
                root.TryGetProperty("Revision", out var revisionValue) && revisionValue.ValueKind == JsonValueKind.String &&
                revisionValue.TryGetGuid(out var revision) && revision == budget.Revision;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        { return false; }
    }

    public SubmissionReservation TryReserveSubmissionBudget(SubmissionBudget? expected, DateTimeOffset now)
    {
        try { return ReserveSubmissionBudget(expected, now); }
        catch (InvalidDataException) { return new(SubmissionReservationOutcome.InvalidState); }
        catch (IOException) { return new(SubmissionReservationOutcome.StorageUnavailable); }
        catch (UnauthorizedAccessException) { return new(SubmissionReservationOutcome.StorageUnavailable); }
    }

    private SubmissionReservation ReserveSubmissionBudget(SubmissionBudget? expected, DateTimeOffset now)
    {
        using var gate = AuthenticationGate();
        var current = ReadSubmissionBudget();
        if (current != expected) return new(SubmissionReservationOutcome.StateChanged, current);
        if (now == default || now > DateTimeOffset.MaxValue.AddSeconds(-120)) return new(SubmissionReservationOutcome.InvalidState, current);
        if (current is not null && now < current.ReservedAt) return new(SubmissionReservationOutcome.WaitClock, current);
        if (current is { Attempts: >= 3 }) return new(SubmissionReservationOutcome.Exhausted, current);
        if (current?.State == SubmissionBudgetState.Reserved) return new(SubmissionReservationOutcome.InFlight, current);
        if (current is { Attempts: > 0, State: SubmissionBudgetState.Concluded }) return new(SubmissionReservationOutcome.Concluded, current);
        if (current is not null && now < current.NextPossibleAt) return new(SubmissionReservationOutcome.Waiting, current);
        var next = new SubmissionBudget
        {
            State = SubmissionBudgetState.Reserved,
            Attempts = (current?.Attempts ?? 0) + 1,
            ReservedAt = now.ToUniversalTime(),
            NextPossibleAt = now.ToUniversalTime().AddSeconds(120),
            Revision = Guid.NewGuid()
        };
        Storage.AtomicWrite(SubmissionBudgetPath, JsonSerializer.Serialize(next, SubmissionJson));
        return new(SubmissionReservationOutcome.Reserved, next);
    }

    public bool TryTransitionSubmissionBudget(SubmissionBudget expected, SubmissionBudgetState nextState)
    {
        using var gate = AuthenticationGate();
        if (ReadSubmissionBudget() != expected) return false;
        if (!(expected.State == SubmissionBudgetState.Reserved && nextState is SubmissionBudgetState.PossiblySent or SubmissionBudgetState.Concluded ||
            expected.State == SubmissionBudgetState.PossiblySent && nextState == SubmissionBudgetState.Concluded))
            return false;
        var next = expected with { State = nextState, Revision = Guid.NewGuid() };
        Storage.AtomicWrite(SubmissionBudgetPath, JsonSerializer.Serialize(next, SubmissionJson));
        return true;
    }
}
