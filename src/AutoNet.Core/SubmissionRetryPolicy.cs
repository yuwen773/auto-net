namespace AutoNet.Core;

public enum SubmissionRetryDecision { Authorized, Waiting, WaitClock, Exhausted, InFlight, Concluded, InternetOnline, InvalidTime }

// Pure decision boundary: callers supply wall time and elapsed monotonic time.
public sealed class SubmissionRetryPolicy
{
    /// <summary>首次提交后最多再自动提交两次；预检不占用该上限。</summary>
    public const int AttemptCap = 3;

    /// <summary>相邻两次可能发出凭据的提交至少间隔的秒数；状态快照按此值投影。</summary>
    public const int SubmissionIntervalSeconds = 120;

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
        var decision = Compute(budget, utc, elapsed, internetOnline, commit: true, out var next);
        NextPossibleAt = next;
        return decision;
    }

    /// <summary>
    /// 只读预演：给出与 <see cref="Evaluate"/> 相同的判定与最早下次提交时刻，但不推进内部锚点。
    /// 状态投影必须使用本方法，读取状态不得改变下一次真实判定的等待起点。
    /// </summary>
    public SubmissionRetryDecision Preview(SubmissionBudget? budget, DateTimeOffset utc, TimeSpan elapsed, bool internetOnline, out DateTimeOffset? nextPossibleAt)
        => Compute(budget, utc, elapsed, internetOnline, commit: false, out nextPossibleAt);

    private SubmissionRetryDecision Compute(SubmissionBudget? budget, DateTimeOffset utc, TimeSpan elapsed, bool internetOnline, bool commit, out DateTimeOffset? next)
    {
        next = null;
        if (utc == default || utc > DateTimeOffset.MaxValue.AddSeconds(-SubmissionIntervalSeconds) || elapsed < TimeSpan.Zero)
        {
            if (commit) retryAfter = (elapsed < TimeSpan.Zero ? lastElapsed : elapsed) + TimeSpan.FromSeconds(SubmissionIntervalSeconds);
            return SubmissionRetryDecision.InvalidTime;
        }
        var previous = lastUtc;
        var previousElapsed = lastElapsed;
        var clockChanged = previous is { } earlier && (elapsed < previousElapsed ||
            ((utc - earlier) - (elapsed - previousElapsed)).Duration() > TimeSpan.FromSeconds(2));
        var anchor = retryAfter;
        if (clockChanged) anchor = elapsed + TimeSpan.FromSeconds(SubmissionIntervalSeconds);
        if (budget != observed) anchor = elapsed + TimeSpan.FromSeconds(SubmissionIntervalSeconds);
        if (commit)
        {
            lastUtc = utc;
            lastElapsed = elapsed;
            observed = budget;
            retryAfter = anchor;
        }
        if (budget is { Attempts: >= AttemptCap }) return SubmissionRetryDecision.Exhausted;
        if (budget is { Attempts: > 0, State: SubmissionBudgetState.Concluded }) return SubmissionRetryDecision.Concluded;
        if (budget?.State == SubmissionBudgetState.Reserved) return SubmissionRetryDecision.InFlight;
        if (clockChanged || budget is not null && utc < budget.ReservedAt) return SubmissionRetryDecision.WaitClock;
        if (internetOnline) return SubmissionRetryDecision.InternetOnline;
        if (elapsed < anchor || budget is { Attempts: > 0 } && utc < budget.NextPossibleAt)
        {
            var candidate = utc + (anchor > elapsed ? anchor - elapsed : TimeSpan.Zero);
            if (budget is { Attempts: > 0 } && budget.NextPossibleAt > candidate) candidate = budget.NextPossibleAt;
            next = candidate;
            return SubmissionRetryDecision.Waiting;
        }
        return SubmissionRetryDecision.Authorized;
    }
}
