using AutoNet.Core;

internal static class SubmissionEpisodeRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("提交回合回归：前两次复检按 2/6 秒推进，之后进入 60 秒低频", () =>
        {
            var episode = new SubmissionEpisode();
            var t0 = DateTimeOffset.UnixEpoch;
            Require(!episode.Pending, "初始状态被当成待消解");
            episode.ObservePending(false, t0);
            Require(!episode.IsRecheckDue(t0), "未待消解却允许复检");

            episode.ObservePending(true, t0);
            Require(episode.Pending, "待消解状态未记录");
            Require(!episode.IsRecheckDue(t0.AddSeconds(1)), "首次复检提前到期");
            Require(episode.IsRecheckDue(t0.AddSeconds(2)), "首次复检未在 2 秒到期");

            var first = episode.BeginRecheck(t0.AddSeconds(2));
            Require(first == 1 && episode.ShouldAnnounceRecheckStarted(first), "首次复检序号或通知不符");
            Require(!episode.IsRecheckDue(t0.AddSeconds(3)), "第二次复检在 6 秒前到期");
            Require(episode.IsRecheckDue(t0.AddSeconds(6)), "第二次复检未在 6 秒到期");

            var second = episode.BeginRecheck(t0.AddSeconds(6));
            Require(second == 2 && episode.IsBeyondFastRechecks(second) && episode.ShouldAnnounceUnresolved(second),
                "第二次复检未结束快速窗口或未通知结果未定");
            Require(!episode.IsRecheckDue(t0.AddSeconds(65)), "低频复检在 60 秒前到期");
            Require(episode.IsRecheckDue(t0.AddSeconds(66)), "低频复检未在 60 秒到期");

            var third = episode.BeginRecheck(t0.AddSeconds(66));
            Require(third == 3 && !episode.ShouldAnnounceUnresolved(third), "第三次复检重复通知结果未定");
            return Task.CompletedTask;
        }),
        ("提交回合回归：离开待消解后清空复检节奏", () =>
        {
            var episode = new SubmissionEpisode();
            var t0 = DateTimeOffset.UnixEpoch;
            episode.ObservePending(true, t0);
            episode.BeginRecheck(t0.AddSeconds(2));
            Require(episode.Rechecks == 1, "复检次数未登记");

            episode.ObservePending(false, t0.AddSeconds(3));
            Require(!episode.Pending && episode.Rechecks == 0, "离开待消解后仍保留复检节奏");

            episode.ObservePending(true, t0.AddSeconds(4));
            Require(!episode.IsRecheckDue(t0.AddSeconds(5)), "重新待消解后复检未重新计时");
            Require(episode.IsRecheckDue(t0.AddSeconds(6)), "重新待消解后首次复检未在 2 秒到期");
            return Task.CompletedTask;
        }),
        ("提交回合回归：提交授权后从新时刻重新计时", () =>
        {
            var episode = new SubmissionEpisode();
            var t0 = DateTimeOffset.UnixEpoch;
            episode.ObservePending(true, t0);
            episode.BeginRecheck(t0.AddSeconds(2));
            episode.Restart(t0.AddSeconds(10));
            Require(episode.Pending && episode.Rechecks == 0, "重新授权未清空复检次数");
            Require(!episode.IsRecheckDue(t0.AddSeconds(11)), "重新授权后复检提前到期");
            Require(episode.IsRecheckDue(t0.AddSeconds(12)), "重新授权后首次复检未在 2 秒到期");
            return Task.CompletedTask;
        }),
        ("提交回合回归：额度耗尽与即将用尽通知各只发一次", () =>
        {
            var episode = new SubmissionEpisode();
            Require(episode.ShouldAnnounceExhausted(true, false), "首次耗尽未通知");
            Require(!episode.ShouldAnnounceExhausted(true, false), "耗尽重复通知");
            Require(!episode.ShouldAnnounceExhausted(false, false), "未耗尽时仍通知");
            Require(episode.ShouldAnnounceExhausted(true, false), "耗尽状态恢复后未重新通知");
            Require(!episode.ShouldAnnounceExhausted(true, true), "布局通知占用时仍抢先通知耗尽");

            Require(episode.ShouldAnnounceNearLimit(true, false, false), "首次即将用尽未通知");
            Require(!episode.ShouldAnnounceNearLimit(true, false, false), "即将用尽重复通知");
            Require(!episode.ShouldAnnounceNearLimit(true, true, false), "布局通知占用时仍通知即将用尽");
            Require(!episode.ShouldAnnounceNearLimit(true, false, true), "本轮已有通知时仍通知即将用尽");
            Require(!episode.ShouldAnnounceNearLimit(false, false, false), "未接近上限时仍通知");
            Require(episode.ShouldAnnounceNearLimit(true, false, false), "离开接近上限后未重新武装");
            return Task.CompletedTask;
        }),
        ("提交回合回归：额度重置通知只发一次并保留待发时机", () =>
        {
            var episode = new SubmissionEpisode();
            Require(!episode.ShouldAnnounceReset(SubmissionBudgetCodes.NearLimit, false, false, false),
                "非重置码被当成重置");
            Require(episode.ShouldAnnounceReset(SubmissionBudgetCodes.Reset, false, false, false), "重置未通知");
            Require(!episode.ShouldAnnounceReset(SubmissionBudgetCodes.Reset, false, false, false), "重置重复通知");

            var deferred = new SubmissionEpisode();
            deferred.ShouldAnnounceReset(SubmissionBudgetCodes.NearLimit, false, false, false);
            Require(!deferred.ShouldAnnounceReset(SubmissionBudgetCodes.Reset, true, false, false),
                "认证暂停时仍通知重置");
            Require(!deferred.ShouldAnnounceReset(SubmissionBudgetCodes.Reset, false, true, false),
                "布局通知占用时仍通知重置");
            Require(!deferred.ShouldAnnounceReset(SubmissionBudgetCodes.Reset, false, false, true),
                "本轮已有通知时仍通知重置");
            Require(deferred.ShouldAnnounceReset(SubmissionBudgetCodes.Reset, false, false, false),
                "条件恢复后未补发重置通知");

            var cleared = new SubmissionEpisode();
            cleared.ShouldAnnounceReset(SubmissionBudgetCodes.NearLimit, false, false, false);
            cleared.ShouldAnnounceReset(SubmissionBudgetCodes.Reset, true, false, false);
            cleared.ShouldAnnounceReset(SubmissionBudgetCodes.NearLimit, false, false, false);
            Require(!cleared.ShouldAnnounceReset(SubmissionBudgetCodes.NearLimit, false, false, false),
                "额度码离开重置后仍补发旧通知");
            Require(cleared.ShouldAnnounceReset(SubmissionBudgetCodes.Reset, false, false, false),
                "新的重置转换未通知");
            return Task.CompletedTask;
        })
    ];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
