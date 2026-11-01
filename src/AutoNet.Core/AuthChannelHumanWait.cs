namespace AutoNet.Core;

/// <summary>
/// 人工等待期间的只读观察（工单 #40 / ADR-0019）：每 2 秒轮询一次，只有观察到已登录才
/// 提前结束；等待时间到或用户关闭时按原结论收尾。轮询、延时与每轮钩子由调用方注入，
/// 因此不依赖浏览器或真实时间；这里不做任何提交、不改变额度、不写认证状态。
/// </summary>
public sealed class AuthChannelHumanWait(
    Func<CancellationToken, Task<bool>> poll,
    Func<TimeSpan, CancellationToken, Task> delay,
    TimeProvider clock,
    Action? onTick = null)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    public async Task<AuthChannelConclusion> ObserveAsync(
        AuthChannelConclusion fallback, TimeSpan wait, CancellationToken ct)
    {
        if (wait <= TimeSpan.Zero) return fallback;
        var start = clock.GetTimestamp();
        try
        {
            while (true)
            {
                // 先算剩余时间再延时：最后一轮按剩余时间收尾，不会越过等待上限。
                var remaining = wait - clock.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero) break;
                await delay(remaining < Interval ? remaining : Interval, ct).ConfigureAwait(false);
                onTick?.Invoke();
                if (await poll(ct).ConfigureAwait(false)) return AuthChannelConclusion.LoggedIn;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        return fallback;
    }
}
