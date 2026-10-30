namespace AutoNet.Core;

public enum SubmissionRetryDecision { Authorized, Waiting, WaitClock, Exhausted, InFlight, Concluded, InternetOnline, InvalidTime }

// Pure decision boundary: callers supply wall time and elapsed monotonic time.
public sealed class SubmissionRetryPolicy
{
    /// <summary>首次提交后最多再自动提交两次；预检不占用该上限。</summary>
    public const int AttemptCap = 3;

    private SubmissionBudget? observed;
    private TimeSpan retryAfter;
    private DateTimeOffset? lastUtc;
    private TimeSpan lastElapsed;

    /// <summary>
    /// 最近一次判定给出的最早下次提交墙钟时刻（#35）；只在 <see cref="SubmissionRetryDecision.Waiting"/>
    /// 时提供。外网在线、时间不可信、执行中、耗尽或已明确结束时无法给出可信时刻，保持 null。
    /// </summary>
    public DateTimeOffset? NextPossibleAt { get; private set; }

    public SubmissionRetryDecision Evaluate(SubmissionBudget? budget, DateTimeOffset utc, TimeSpan elapsed, bool internetOnline = false)
    {
        NextPossibleAt = null;
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
        if (budget is { Attempts: >= AttemptCap }) return SubmissionRetryDecision.Exhausted;
        if (budget is { Attempts: > 0, State: SubmissionBudgetState.Concluded }) return SubmissionRetryDecision.Concluded;
        if (budget?.State == SubmissionBudgetState.Reserved) return SubmissionRetryDecision.InFlight;
        if (clockChanged || budget is not null && utc < budget.ReservedAt) return SubmissionRetryDecision.WaitClock;
        if (internetOnline) return SubmissionRetryDecision.InternetOnline;
        if (elapsed < retryAfter || budget is { Attempts: > 0 } && utc < budget.NextPossibleAt)
        {
            var next = utc + (retryAfter > elapsed ? retryAfter - elapsed : TimeSpan.Zero);
            if (budget is { Attempts: > 0 } && budget.NextPossibleAt > next) next = budget.NextPossibleAt;
            NextPossibleAt = next;
            return SubmissionRetryDecision.Waiting;
        }
        return SubmissionRetryDecision.Authorized;
    }
}
