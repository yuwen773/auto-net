namespace AutoNet.Core;

public sealed class ChannelSubmissionLease : IDisposable
{
    private readonly IDisposable execution;
    private bool disposed;
    internal ChannelSubmissionLease(SafetyStateStore owner, IDisposable execution, AuthenticationBlock authentication, SubmissionBudget budget)
        => (Owner, this.execution, Authentication, Budget) = (owner, execution, authentication, budget);
    internal SafetyStateStore Owner { get; }
    internal bool Active => !disposed;
    public AuthenticationBlock Authentication { get; }
    public SubmissionBudget Budget { get; }
    public void Dispose() { if (disposed) return; disposed = true; execution.Dispose(); }
}

public sealed partial class SafetyStateStore
{
    public ChannelSubmissionLease? TryBeginChannelSubmission(AuthenticationBlock? expected, SubmissionBudget? budget,
        PortalFailure structuralFailure, SubmissionRetryPolicy policy, DateTimeOffset utc, TimeSpan elapsed)
    {
        if (!PortalLayoutSignal.IsStructural(structuralFailure)) return null;
        var execution = TryAcquireSubmissionExecution();
        if (execution is null) return null;
        var success = false;
        try
        {
            var current = LoadAuthenticationBlock();
            if (current != expected || current is null ||
                !(current.State == AuthenticationState.SubmissionPending && (budget is { Attempts: > 0 } || IsConfirmedSubmissionRestart(budget)) ||
                  current.State == AuthenticationState.InteractionRequired &&
                  PortalLayoutSignal.IsStructural(current.ChannelFallbackCause))) return null;
            if (LoadSubmissionBudget() != budget || policy.Evaluate(budget, utc, elapsed) != SubmissionRetryDecision.Authorized)
                return null;
            var reservation = TryReserveSubmissionBudget(budget, utc);
            if (reservation is not { Outcome: SubmissionReservationOutcome.Reserved, Budget: { } reserved }) return null;
            var pending = new AuthenticationBlock
            {
                State = AuthenticationState.SubmissionPending,
                Attempts = Math.Max(1, current.Attempts),
                Revision = Guid.NewGuid()
            };
            if (!TryTransitionAuthentication(current, pending) || LoadAuthenticationBlock() != pending ||
                LoadSubmissionBudget() != reserved) return null;
            success = true;
            return new ChannelSubmissionLease(this, execution, pending, reserved);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (!success) execution.Dispose();
        }
    }
}
