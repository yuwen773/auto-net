namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 页面读取的代次守卫（迁移规格 T02 / 故事 16）：快速切换页面时，旧读取的结果不得覆写当前页面。
/// 语义：
/// - 每次导航开启新一代次，并取消上一代次仍在途的读取；
/// - 只有完成时仍属当前代次的结果才会被应用；
/// - 读取异常只在仍属当前代次时回调 <c>onFailure</c>（不向调用方抛出）；取消一律静默；
/// - <see cref="Dispose"/> 之后一切结果都被丢弃（销毁保护），后续导航直接忽略。
/// 本类不依赖任何 UI 类型，供壳与后续页面（诊断/日志/设置）共用，并可受控测试。
/// </summary>
internal sealed class PageLoadCoordinator : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? pending;
    private long generation;
    private bool disposed;

    /// <summary>开始一次页面读取；此调用会立即取代（并取消）上一代次的读取。</summary>
    public async Task NavigateAsync<TPage, TContent>(
        TPage page,
        Func<TPage, CancellationToken, Task<TContent>> load,
        Action<TPage, TContent> apply,
        Action<TPage, Exception>? onFailure = null)
    {
        CancellationTokenSource cancellation;
        long issued;
        lock (gate)
        {
            if (disposed) return;
            // 取代旧代次：取消其取消源即可；不主动 Dispose，避免与在途读取的注册/检查竞争。
            pending?.Cancel();
            cancellation = new CancellationTokenSource();
            pending = cancellation;
            issued = ++generation;
        }

        TContent content;
        try
        {
            content = await load(page, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 已被新代次取代（或销毁）：旧结果直接丢弃。
            return;
        }
        catch (Exception failure)
        {
            lock (gate) { if (disposed || issued != generation) return; }
            onFailure?.Invoke(page, failure);
            return;
        }

        lock (gate) { if (disposed || issued != generation) return; }
        apply(page, content);
    }

    /// <summary>销毁：取消在途读取并拒绝一切后续结果。幂等。</summary>
    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cancellation = pending;
            pending = null;
        }

        cancellation?.Cancel();
    }
}
