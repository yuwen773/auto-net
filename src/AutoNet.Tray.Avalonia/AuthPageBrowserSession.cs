using Microsoft.Web.WebView2.Core;
using WebView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 认证页的一次附着尝试（工单 #38）：代次 + 该次尝试独占的浏览器、用户目录与取消源。
/// 宿主在每个 await 之后用代次校验；旧代次的导航完成、进程退出或失败事件只能丢弃，
/// 不能改写新会话的状态，也不能让旧异步任务继续导航。
///
/// 每个会话还会把用户目录登记为“活跃”，启动清扫据此排除任何仍在使用的目录。
///
/// 线程契约：状态只在界面线程读写；本类不承诺线程安全，<see cref="Cancel"/> 与
/// <see cref="IsDisposed"/> 只用于跨线程的“是否仍然有效”查询。
/// </summary>
internal sealed class AuthPageBrowserSession : IDisposable
{
    private static readonly object ActiveGate = new();
    private static readonly HashSet<string> ActiveDirectories = new(StringComparer.OrdinalIgnoreCase);

    private readonly TaskCompletionSource browserExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    public AuthPageBrowserSession(int generation, string directory)
    {
        Generation = generation;
        Directory = directory;
        lock (ActiveGate) ActiveDirectories.Add(Path.GetFullPath(directory));
    }

    public int Generation { get; }

    /// <summary>本次尝试独占的 WebView2 用户目录；销毁后由后台等待浏览器退出再删除。</summary>
    public string Directory { get; }

    public CancellationTokenSource Cancellation { get; } = new();

    public CoreWebView2Environment? Environment { get; set; }
    public WebView? WebView { get; set; }

    /// <summary>初始化已开始：阻止同一代次重复创建浏览器。</summary>
    public bool Starting { get; set; }

    /// <summary>已发出导航请求：避免表面创建回调重复导航。</summary>
    public bool NavigationRequested { get; set; }

    /// <summary>首次导航已确认成功：之后的主框架导航结果不再改写，也不重复发布 Attached。</summary>
    public bool NavigationConfirmed { get; set; }

    /// <summary>浏览器进程退出信号；从未创建环境时永远不完成，删除方按有界等待处理。</summary>
    public Task BrowserExited => browserExited.Task;

    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>启动清扫用：该目录仍被某个未销毁的会话占用。</summary>
    internal static bool IsActiveDirectory(string directory)
    {
        lock (ActiveGate) return ActiveDirectories.Contains(Path.GetFullPath(directory));
    }

    public void MarkExited() => browserExited.TrySetResult();

    public void Cancel()
    {
        try { Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { WebView?.Dispose(); } catch (Exception) { }
        WebView = null;
        Environment = null;
        lock (ActiveGate) ActiveDirectories.Remove(Path.GetFullPath(Directory));
        // 不 Dispose 取消源：在途初始化可能仍持有链接的令牌；Dispose 后的取消源会与
        // 链接源/超时定时器产生释放竞态，而这里没有定时器，由 GC 回收即可。
    }
}
