namespace AutoNet.Core;

/// <summary>
/// 「提交结果未知」之后的纯状态（ADR-0001 / 0011 / 0014 / 0015）：待消解计时、
/// 只读复检节奏，以及额度耗尽 / 重置通知的一次性仲裁。
///
/// 不读文件、不访问网络、不持久化；<c>MonitorWorker</c> 负责真正执行复检与写入，
/// 本类只回答「现在该不该复检」「这次复检是第几次」「这条通知是否已经发过」。
/// 时间由调用方传入策略时间，便于回归直接推进，不依赖整轮 tick。
/// </summary>
public sealed class SubmissionEpisode
{
    private static readonly TimeSpan[] RecheckDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];
    private static readonly TimeSpan LowFrequencyRecheck = TimeSpan.FromSeconds(60);

    private DateTimeOffset? pendingSince;
    private int rechecks;
    private DateTimeOffset nextRecheck;
    private bool exhaustedPublished;
    private bool nearLimitPublished;
    private bool resetPending;
    private string lastBudgetCode = "";

    /// <summary>当前是否处于「提交结果未知」的待消解状态。</summary>
    public bool Pending => pendingSince is not null;

    /// <summary>本轮待消解状态已执行的只读复检次数。</summary>
    public int Rechecks => rechecks;

    /// <summary>
    /// 每个循环用当前认证状态对齐：待消解时保留首次观察时间（复检节奏不因刷新重置），
    /// 否则清空计时与复检次数。
    /// </summary>
    public void ObservePending(bool submissionPending, DateTimeOffset now)
    {
        if (submissionPending) pendingSince ??= now;
        else ResetRechecks();
    }

    /// <summary>一次提交刚被授权：从此刻重新计时，并允许最快一次复检。</summary>
    public void Restart(DateTimeOffset now)
    {
        pendingSince = now;
        rechecks = 0;
        nextRecheck = default;
    }

    /// <summary>
    /// 只读复检是否到期。前两次按 2 秒 / 6 秒；之后进入 60 秒低频节奏。
    /// 调用方仍须自行确认链路就绪与执行锁可用，未就绪时不得调用 <see cref="BeginRecheck"/>。
    /// </summary>
    public bool IsRecheckDue(DateTimeOffset now) =>
        pendingSince is { } since &&
        (rechecks < RecheckDelays.Length ? now >= since + RecheckDelays[rechecks] : now >= nextRecheck);

    /// <summary>登记一次已开始的复检并返回序号（从 1 开始），同时排定下一次低频时间。</summary>
    public int BeginRecheck(DateTimeOffset now)
    {
        var ordinal = ++rechecks;
        nextRecheck = now + LowFrequencyRecheck;
        return ordinal;
    }

    /// <summary>第一次复检开始时通知一次「正在复检」。</summary>
    public bool ShouldAnnounceRecheckStarted(int ordinal) => ordinal == 1;

    /// <summary>前两次快速复检已用尽：之后仍可低频复检，但不再算快速窗口。</summary>
    public bool IsBeyondFastRechecks(int ordinal) => ordinal >= RecheckDelays.Length;

    /// <summary>快速复检用尽且仍无法定性时，通知一次「结果未定」。</summary>
    public bool ShouldAnnounceUnresolved(int ordinal) => ordinal == RecheckDelays.Length;

    /// <summary>
    /// 额度耗尽通知：停止提交且本轮没有更高优先级的布局通知时发一次；
    /// 一旦不再处于该状态就重新武装，下次耗尽仍会通知。
    /// </summary>
    public bool ShouldAnnounceExhausted(bool stopsSubmission, bool layoutWillPublish)
    {
        if (stopsSubmission && !exhaustedPublished && !layoutWillPublish)
        {
            exhaustedPublished = true;
            return true;
        }
        if (!stopsSubmission) exhaustedPublished = false;
        return false;
    }

    /// <summary>
    /// 额度即将用尽通知：只在额度成为当前主要问题时发一次；布局通知占用本轮或本轮
    /// 已有通知时保持待发。离开该状态后重新武装。
    /// </summary>
    public bool ShouldAnnounceNearLimit(bool stopsSubmission, bool layoutWillPublish, bool notifiedThisCycle)
    {
        if (stopsSubmission && !nearLimitPublished && !layoutWillPublish && !notifiedThisCycle)
        {
            nearLimitPublished = true;
            return true;
        }
        if (!stopsSubmission) nearLimitPublished = false;
        return false;
    }

    /// <summary>
    /// 额度重置通知：只在额度码从非重置转为重置时武装；认证仍暂停、布局通知占用本轮
    /// 或本轮已有通知时保持待发，直到可以通知或额度码离开重置。每个重置只通知一次。
    /// </summary>
    public bool ShouldAnnounceReset(string budgetCode, bool credentialsBlocked, bool layoutWillPublish,
        bool notifiedThisCycle)
    {
        if (budgetCode == SubmissionBudgetCodes.Reset &&
            lastBudgetCode is { Length: > 0 } and not SubmissionBudgetCodes.Reset)
            resetPending = true;
        var announce = resetPending && !credentialsBlocked && !layoutWillPublish && !notifiedThisCycle;
        if (announce) resetPending = false;
        if (budgetCode != SubmissionBudgetCodes.Reset) resetPending = false;
        lastBudgetCode = budgetCode;
        return announce;
    }

    private void ResetRechecks()
    {
        pendingSince = null;
        rechecks = 0;
        nextRecheck = default;
    }
}
