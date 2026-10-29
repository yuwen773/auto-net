namespace AutoNet.Core;

public enum SubmissionRetryDecision { Authorized, Waiting, WaitClock, Exhausted, InFlight, Concluded, InternetOnline, InvalidTime }

// Pure decision boundary: callers supply wall time and elapsed monotonic time.
public sealed class SubmissionRetryPolicy
{
    private SubmissionBudget? observed;
    private TimeSpan retryAfter;
    private DateTimeOffset? lastUtc;
    private TimeSpan lastElapsed;

    public SubmissionRetryDecision Evaluate(SubmissionBudget? budget, DateTimeOffset utc, TimeSpan elapsed, bool internetOnline = false)
    {
        if (utc == default || utc > DateTimeOffset.MaxValue.AddSeconds(-120) || elapsed < TimeSpan.Zero)
        {
            retryAfter = (elapsed < TimeSpan.Zero ? lastElapsed : elapsed) + TimeSpan.FromSeconds(120);
            return SubmissionRetryDecision.InvalidTime;
        }
        var clockChanged = lastUtc is { } previous && (elapsed < lastElapsed ||
            ((utc - previous) - (elapsed - lastElapsed)).Duration() > TimeSpan.FromSeconds(2));
        lastUtc = utc;
        lastElapsed = elapsed;
        if (clockChanged) retryAfter = elapsed + TimeSpan.FromSeconds(120);
        if (budget != observed)
        {
            observed = budget;
            retryAfter = elapsed + TimeSpan.FromSeconds(120);
        }
        if (budget is { Attempts: >= 3 }) return SubmissionRetryDecision.Exhausted;
        if (budget is { Attempts: > 0, State: SubmissionBudgetState.Concluded }) return SubmissionRetryDecision.Concluded;
        if (budget?.State == SubmissionBudgetState.Reserved) return SubmissionRetryDecision.InFlight;
        if (clockChanged || budget is not null && utc < budget.ReservedAt) return SubmissionRetryDecision.WaitClock;
        if (internetOnline) return SubmissionRetryDecision.InternetOnline;
        return elapsed < retryAfter || budget is { Attempts: > 0 } && utc < budget.NextPossibleAt
            ? SubmissionRetryDecision.Waiting : SubmissionRetryDecision.Authorized;
    }
}
