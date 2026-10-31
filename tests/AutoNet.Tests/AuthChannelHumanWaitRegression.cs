using AutoNet.Core;

/// <summary>
/// 人工观察回归（工单 #40 / ADR-0019）：人工等待期间保持会话存活，每 2 秒只读轮询一次；
/// 只有观察到已登录才提前结束，超时或用户关闭按原结论收尾。全部为纯逻辑，不启动浏览器、
/// 不连接门户、不读取真实凭据。
/// </summary>
internal static class AuthChannelHumanWaitRegression
{
    internal static readonly (string Name, Func<Task> Body)[] Cases =
    [
        ("人工观察：观察到登录成功立即结束", async () =>
        {
            var clock = new ManualClock();
            var polls = 0;
            var wait = new AuthChannelHumanWait(
                _ => Task.FromResult<AuthChannelConclusion?>(++polls == 3 ? AuthChannelConclusion.LoggedIn : null),
                (interval, _) => { clock.Advance(interval); return Task.CompletedTask; },
                clock);
            var result = await wait.ObserveAsync(AuthChannelConclusion.NeedsHuman, TimeSpan.FromSeconds(60), default);
            Require(result == AuthChannelConclusion.LoggedIn && polls == 3, "观察到登录后未立即结束");
        }),
        ("人工观察：未观察到成功则按等待时间结束并保留原结论", async () =>
        {
            var clock = new ManualClock();
            var polls = 0;
            var wait = new AuthChannelHumanWait(
                _ => { polls++; return Task.FromResult<AuthChannelConclusion?>(null); },
                (interval, _) => { clock.Advance(interval); return Task.CompletedTask; },
                clock);
            var result = await wait.ObserveAsync(AuthChannelConclusion.NeedsHuman, TimeSpan.FromSeconds(10), default);
            Require(result == AuthChannelConclusion.NeedsHuman && polls == 5, "等待结束未保留原结论或轮询次数不符");
        }),
        ("人工观察：用户关闭时按原结论收尾", async () =>
        {
            var clock = new ManualClock();
            using var close = new CancellationTokenSource();
            var polls = 0;
            var wait = new AuthChannelHumanWait(
                _ => { polls++; return Task.FromResult<AuthChannelConclusion?>(null); },
                (interval, ct) =>
                {
                    clock.Advance(interval);
                    if (polls >= 2) close.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                clock);
            var result = await wait.ObserveAsync(AuthChannelConclusion.Unknown, TimeSpan.FromSeconds(60), close.Token);
            Require(result == AuthChannelConclusion.Unknown && polls == 2, "用户关闭后未按原结论收尾");
        }),
        ("人工观察：只读轮询间隔为 2 秒", async () =>
        {
            var clock = new ManualClock();
            var intervals = new List<TimeSpan>();
            var wait = new AuthChannelHumanWait(
                _ => Task.FromResult<AuthChannelConclusion?>(null),
                (interval, _) => { intervals.Add(interval); clock.Advance(interval); return Task.CompletedTask; },
                clock);
            await wait.ObserveAsync(AuthChannelConclusion.NeedsHuman, TimeSpan.FromSeconds(6), default);
            Require(intervals.Count == 3 && intervals.All(interval => interval == TimeSpan.FromSeconds(2)),
                "轮询间隔不是 2 秒");
        }),
        ("人工观察：等待时间为零时不轮询", async () =>
        {
            var clock = new ManualClock();
            var polls = 0;
            var wait = new AuthChannelHumanWait(
                _ => { polls++; return Task.FromResult<AuthChannelConclusion?>(AuthChannelConclusion.LoggedIn); },
                (interval, _) => { clock.Advance(interval); return Task.CompletedTask; },
                clock);
            var result = await wait.ObserveAsync(AuthChannelConclusion.NeedsHuman, TimeSpan.Zero, default);
            Require(result == AuthChannelConclusion.NeedsHuman && polls == 0, "零等待仍轮询或改变了结论");
        })
    ];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
