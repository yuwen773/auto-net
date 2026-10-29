using System.Diagnostics;
using System.Text.Json;

namespace AutoNet.Core;

public sealed partial class SafetyStateStore
{
    // Held for the entire credential attempt, including completion writes. A process exit
    // releases the OS handle; a live attempt must never be mistaken for an interrupted one.
    public IDisposable? TryAcquireSubmissionExecution()
    {
        try
        {
            var execution = new FileStream(Path.Combine(directory, "submission-execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (ChannelSessionEnded()) return execution;
            execution.Dispose();
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private string ChannelSessionPath => Path.Combine(directory, "submission-channel-session.json");

    public bool TryRegisterChannelSession(ChannelSubmissionLease lease, int processId, int ownerId, string jobName)
    {
        if (!ReferenceEquals(lease.Owner, this) || !lease.Active || processId <= 0 || ownerId <= 0 ||
            !ChannelProcessJob.IsMember(jobName, processId) || !ChannelProcessJob.IsMember(jobName, ownerId)) return false;
        try
        {
            if (LoadAuthenticationBlock() != lease.Authentication || LoadSubmissionBudget() != lease.Budget || Exists(ChannelSessionPath)) return false;
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return false;
            var started = process.StartTime.ToUniversalTime().Ticks;
            using var owner = Process.GetProcessById(ownerId);
            if (owner.HasExited) return false;
            var ownerStarted = owner.StartTime.ToUniversalTime().Ticks;
            Storage.AtomicWrite(ChannelSessionPath, JsonSerializer.Serialize(new
            {
                Version = 3, ProcessId = processId, StartedUtcTicks = started,
                OwnerProcessId = owner.Id, OwnerStartedUtcTicks = ownerStarted, JobName = jobName
            }));
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private bool ChannelSessionEnded()
    {
        try
        {
            if (!Exists(ChannelSessionPath)) return true;
            using var document = JsonDocument.Parse(Storage.ReadShared(ChannelSessionPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 6 ||
                !root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var v) || v != 3 ||
                !root.TryGetProperty("ProcessId", out var pid) || !pid.TryGetInt32(out var id) || id <= 0 ||
                !root.TryGetProperty("StartedUtcTicks", out var ticks) || !ticks.TryGetInt64(out var started) || started <= 0 ||
                !root.TryGetProperty("OwnerProcessId", out var ownerPid) || !ownerPid.TryGetInt32(out var ownerId) || ownerId <= 0 ||
                !root.TryGetProperty("OwnerStartedUtcTicks", out var ownerTicks) || !ownerTicks.TryGetInt64(out var ownerStarted) || ownerStarted <= 0 ||
                !root.TryGetProperty("JobName", out var jobName) || jobName.ValueKind != JsonValueKind.String || jobName.GetString() is not { } name)
                return false;
            if (ChannelProcessJob.IsActive(name) != false || SessionProcessActive(id, started) || SessionProcessActive(ownerId, ownerStarted)) return false;
            using var gate = AuthenticationGate();
            var budget = ReadSubmissionBudget();
            if (budget is not { Attempts: > 0 }) return false;
            var now = DateTimeOffset.UtcNow;
            if (now < budget.ReservedAt) now = budget.ReservedAt;
            if (now == default || now > DateTimeOffset.MaxValue.AddSeconds(-120)) return false;
            var next = budget with
            {
                State = budget.State == SubmissionBudgetState.Reserved ? SubmissionBudgetState.PossiblySent : budget.State,
                ReservedAt = now, NextPossibleAt = now.AddSeconds(120), Revision = Guid.NewGuid()
            };
            Storage.AtomicWrite(SubmissionBudgetPath, JsonSerializer.Serialize(next, SubmissionJson));
            File.Delete(ChannelSessionPath);
            return true;
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static bool SessionProcessActive(int id, long started)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started;
        }
        catch (ArgumentException) { return false; }
    }
}
