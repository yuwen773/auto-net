namespace AutoNet.Core;

/// <summary>
/// 人工等待期间的只读观察（工单 #40 / ADR-0019）：每 2 秒轮询一次，只有观察到已登录才
/// 提前结束；等待时间到或用户关闭时按原结论收尾。轮询与延时由调用方注入，因此不依赖
/// 浏览器或真实时间；这里不做任何提交、不改变额度、不写认证状态。
/// </summary>
public sealed class AuthChannelHumanWait(
    Func<CancellationToken, Task<AuthChannelConclusion?>> poll,
    Func<TimeSpan, CancellationToken, Task> delay,
    TimeProvider clock)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    public async Task<AuthChannelConclusion> ObserveAsync(
        AuthChannelConclusion fallback, TimeSpan wait, CancellationToken ct)
    {
        if (wait <= TimeSpan.Zero) return fallback;
        var start = clock.GetTimestamp();
        try
        {
            while (clock.GetElapsedTime(start) < wait)
            {
                await delay(Interval, ct).ConfigureAwait(false);
                if (await poll(ct).ConfigureAwait(false) == AuthChannelConclusion.LoggedIn)
                    return AuthChannelConclusion.LoggedIn;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        return fallback;
    }
}
