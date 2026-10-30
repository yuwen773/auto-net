using System.Runtime.InteropServices;
using AutoNet.Core;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using WinForms = System.Windows.Forms;
using WebView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 认证页的真实浏览器宿主（工单 #38）：Avalonia 主窗口经 <see cref="NativeControlHost"/>
/// 承载 WinForms WebView2，浏览器流量只经应用自带的允许列表代理出站。
///
/// 网络边界（与 #38 验收一一对应）：
/// - 允许列表只放行固定门户主机（<see cref="ProxyHostPolicy"/>），完整匹配、两侧规范化；
/// - 明文只允许 80，CONNECT 只允许 443，请求体与协议升级一律拒绝；
/// - 代理用 <see cref="DirectNetwork.ConnectAsync"/> 把连接钉到目标校园网卡；
/// - 代理启动失败只返回受控失败，绝不回退直连；代理只在失败时停止，正常切页保持运行；
/// - 系统代理设置不被读取或改写，代理只作为本 WebView 的启动参数传入。
///
/// 浏览器进程与托盘同属一个 <see cref="ChannelProcessJob"/>，代理据此拒绝会话外进程的环回连接。
///
/// 同一会话（工单 #39）：后台内嵌认证通道会话进行中时，本宿主不创建第二个浏览器、也不保留
/// 自己的代理，只投影“内嵌通道会话进行中”；会话结束后回到未附着，由协调器决定是否恢复
/// 只读附着。这样同一门户同一时刻只有一条会发送凭据的会话。
///
/// 线程契约：<see cref="AttachAsync"/>、<see cref="Refresh"/>、<see cref="SetVisible"/>、<see cref="Dispose"/>
/// 在界面线程调用；<see cref="Snapshot"/> 可任意线程读取。
/// </summary>
internal sealed class AuthPageWebViewHost : IAuthPageBrowserHost
{
    private readonly Func<WifiLink?> readLink;
    private readonly Func<bool> runtimeProbe;
    private readonly Func<WifiLink, ChannelProcessJob, Task<AuthChannelProxy>> startProxy;
    private readonly Func<bool> channelSessionActive;
    private readonly AuthPageWebViewSurface surface;
    private AuthPageHostSnapshot snapshot = AuthPageHostSnapshot.NotAttached;
    private AuthChannelProxy? proxy;
    private ChannelProcessJob? job;
    private CoreWebView2Environment? environment;
    private WebView? webView;
    private string? sessionDir;
    private bool browserStarted;
    private bool browserStarting;
    private int disposed;

    public AuthPageWebViewHost(
        Func<WifiLink?> readLink,
        Func<bool>? runtimeProbe = null,
        Func<WifiLink, ChannelProcessJob, Task<AuthChannelProxy>>? startProxy = null,
        Func<bool>? channelSessionActive = null)
    {
        this.readLink = readLink;
        this.runtimeProbe = runtimeProbe ?? AuthPageBrowserRuntime.IsWebView2RuntimePresent;
        this.startProxy = startProxy ?? ((link, processJob) => AuthChannelProxy.StartAsync(link, processJob));
        this.channelSessionActive = channelSessionActive ?? (static () => false);
        surface = new AuthPageWebViewSurface(this);
    }

    public object? Surface => surface;

    public AuthPageHostSnapshot Snapshot => Volatile.Read(ref snapshot);

    public async Task<AuthPageHostSnapshot> AttachAsync(Uri portal, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposed) != 0) return Snapshot;
        // 浏览器正在创建时不要重入：重试会拆掉正在初始化的会话。
        if (browserStarting) return Snapshot;
        if (channelSessionActive())
        {
            // 后台通道会话进行中：不创建第二个浏览器，也不保留自己的代理。
            EnterChannelSession();
            return Snapshot;
        }
        if (Snapshot.Status == AuthPageHostStatus.Attached)
        {
            // 已附着但浏览器还没起来（例如首次附着时原生表面尚未创建）：补一次初始化。
            if (proxy is not null && !browserStarted && surface.IsCreated)
                _ = InitializeBrowserAsync(cancellationToken);
            return Snapshot;
        }

        // 上一次尝试残留的代理/浏览器不再有意义；进程组保留并复用，避免重复嵌套作业。
        ResetBrowser();
        await StopProxyAsync().ConfigureAwait(true);
        var (failure, boundary) = await AuthPageNetworkBoundary.PrepareAsync(
            portal, runtimeProbe(), readLink, EnsureJob, startProxy, cancellationToken).ConfigureAwait(true);
        if (failure is not null)
        {
            Publish(failure);
            return Snapshot;
        }

        proxy = boundary!.Proxy;
        // 后台会话可能在准备边界期间开始：丢弃刚启动的代理，只投影会话状态。
        if (channelSessionActive())
        {
            EnterChannelSession();
            return Snapshot;
        }
        // 先发布 Attached：视图只在 HostAvailable（即 Attached）时挂载原生表面，
        // 表面创建后由 OnSurfaceCreated 再启动浏览器。浏览器启动失败会经 FailBrowser 降级为 Failed。
        Publish(new AuthPageHostSnapshot(AuthPageHostStatus.Attached, AuthPageModel.PortalAddress, true,
            AuthPageModel.HostCopyFor(AuthPageHostStatus.Attached).Detail));
        // 原生表面可能还没创建（首次打开时视图尚未渲染），由 CreateNativeControlCore 回调补上。
        if (surface.IsCreated) _ = InitializeBrowserAsync(cancellationToken);
        return Snapshot;
    }

    public void Refresh()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (channelSessionActive())
        {
            EnterChannelSession();
            return;
        }
        // 会话结束后不自行附着：本页是否恢复只读附着由协调器按“是否打开过”决定，
        // 未打开过的页面不会因为一次后台会话凭空拉起代理。
        if (Snapshot.Status == AuthPageHostStatus.ChannelSessionActive)
            Publish(AuthPageHostSnapshot.NotAttached);
    }

    /// <summary>
    /// 后台通道会话开始：丢弃本页自己的浏览器与代理，只保留受控状态。
    /// 同一门户同一时刻只允许一条会话，避免两个 WebView2 用户目录/两条凭据会话并存。
    /// </summary>
    private void EnterChannelSession()
    {
        // 每次都尝试拆除：会话可能在附着、初始化或失败处理中途开始，重复调用是安全的空操作。
        ResetBrowser();
        _ = StopProxyAsync();
        if (Snapshot.Status == AuthPageHostStatus.ChannelSessionActive) return;
        Publish(AuthPageHostSnapshot.ChannelSession());
    }

    public void SetVisible(bool visible)
    {
        surface.IsVisible = visible;
        // 每次切回本页时补一次初始化：OnSurfaceCreated 只触发一次，错过就再没有入口。
        if (visible && !channelSessionActive() && proxy is not null && !browserStarted && !browserStarting
            && surface.IsCreated)
            _ = InitializeBrowserAsync(CancellationToken.None);
    }

    /// <summary>原生表面创建后回调：此时才有 HWND 可承载 WebView2。</summary>
    internal void OnSurfaceCreated()
    {
        if (proxy is not null && !channelSessionActive()) _ = InitializeBrowserAsync(CancellationToken.None);
    }

    private ChannelProcessJob EnsureJob() => job ??= ChannelProcessJob.CreateForCurrentProcess();

    private async Task InitializeBrowserAsync(CancellationToken cancellationToken)
    {
        if (browserStarted || browserStarting || proxy is null || job is null) return;
        browserStarting = true;
        try
        {
            sessionDir = Path.Combine(Path.GetTempPath(), "autonet-authpage-" + Guid.NewGuid().ToString("N"));
            // 必须用命名参数：位置参数会把用户数据目录绑定到 browserExecutableFolder，
            // 随后误报“运行时未安装”，把路径问题伪装成环境问题。
            var options = new CoreWebView2EnvironmentOptions(AuthPageBrowserArguments.For(proxy.Port));
            environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: sessionDir, options: options)
                .WaitAsync(cancellationToken).ConfigureAwait(true);
            webView = new WebView { Dock = WinForms.DockStyle.Fill };
            surface.Add(webView);
            await webView.EnsureCoreWebView2Async(environment).WaitAsync(cancellationToken).ConfigureAwait(true);
            // 后台会话可能在初始化等待期间开始：不再导航，立即拆除并投影会话状态，避免第二条会话。
            if (channelSessionActive())
            {
                EnterChannelSession();
                return;
            }
            var core = webView.CoreWebView2;
            if (!job.Contains(checked((int)core.BrowserProcessId)) ||
                environment.GetProcessInfos().Any(info => !job.Contains(checked((int)info.ProcessId))))
                throw new InvalidOperationException("浏览器进程未全部进入会话进程组。");

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            // 浏览器进程崩溃后 WebView2 不会自行恢复；降级为可手动重试的失败状态，而不是停在 Attached。
            core.ProcessFailed += (_, e) =>
            {
                if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
                    or CoreWebView2ProcessFailedKind.RenderProcessExited
                    or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)
                    Dispatcher.UIThread.Post(() => FailBrowser(new InvalidOperationException("浏览器进程异常退出。")));
            };
            // 弹窗仍会走同一个浏览器与代理，但认证页只承载一个固定门户页面，不需要新窗口。
            core.NewWindowRequested += (_, e) => e.Handled = true;
            // 资源过滤只作为纵深防御，**不是**隔离依据：真正的边界是代理。
            core.NavigationStarting += (_, e) =>
            {
                if (!IsPortalOrigin(e.Uri)) e.Cancel = true;
            };
            core.Navigate(AuthPageModel.PortalAddress);
            browserStarted = true;
            Publish(new AuthPageHostSnapshot(AuthPageHostStatus.Attached, AuthPageModel.PortalAddress, true,
                AuthPageModel.HostCopyFor(AuthPageHostStatus.Attached).Detail));
        }
        catch (Exception e)
        {
            FailBrowser(e);
        }
        finally
        {
            browserStarting = false;
        }
    }

    private static bool IsPortalOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && Settings.IsPortalOrigin(uri);

    private void FailBrowser(Exception e)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        // 会话进行中的失败不该把状态改回“可重试的加载失败”：只投影会话状态。
        if (channelSessionActive())
        {
            EnterChannelSession();
            return;
        }
        var status = e is WebView2RuntimeNotFoundException
            ? AuthPageHostStatus.RuntimeMissing
            : AuthPageHostStatus.Failed;
        ResetBrowser();
        _ = StopProxyAsync();
        Publish(new AuthPageHostSnapshot(status, AuthPageModel.PortalAddress, false,
            AuthPageModel.HostCopyFor(status).Detail));
    }

    /// <summary>丢弃浏览器会话，但保留进程组；下一次附着会重新创建浏览器。</summary>
    private void ResetBrowser()
    {
        try { webView?.Dispose(); } catch (Exception) { }
        webView = null;
        environment = null;
        browserStarted = false;
        // 失败的会话不再保留：临时目录里有门户 cookie/缓存，重试前丢弃，避免越积越多。
        if (sessionDir is { } dir)
        {
            sessionDir = null;
            _ = Task.Run(() => DeleteSessionDirectory(dir));
        }
    }

    private void Publish(AuthPageHostSnapshot value) => Volatile.Write(ref snapshot, value);

    private async Task StopProxyAsync()
    {
        var stopping = proxy;
        proxy = null;
        if (stopping is null) return;
        try { await stopping.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { webView?.Dispose(); } catch (Exception) { }
        webView = null;
        environment = null;
        // 不阻塞界面线程等待代理收尾；监听端口随进程/宿主销毁一并消失。
        _ = StopProxyAsync();
        if (sessionDir is { } dir) _ = Task.Run(() => DeleteSessionDirectory(dir));
    }

    private static void DeleteSessionDirectory(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }

    /// <summary>
    /// 原生承载面：创建 WinForms Panel 并把句柄挂到 Avalonia 父窗口。
    /// 切页只解除父子关系、不销毁浏览器，因此返回认证页时已填内容仍在。
    /// </summary>
    private sealed class AuthPageWebViewSurface : NativeControlHost
    {
        private readonly AuthPageWebViewHost host;
        private WinForms.Panel? panel;

        public AuthPageWebViewSurface(AuthPageWebViewHost host)
        {
            this.host = host;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            MinHeight = 360;
        }

        public bool IsCreated => panel is { IsHandleCreated: true };

        public void Add(WebView view)
        {
            Panel().Controls.Add(view);
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            var handle = Panel().Handle;
            SetParent(handle, parent.Handle);
            host.OnSurfaceCreated();
            return new PlatformHandle(handle, "HWND");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (panel is { IsHandleCreated: true }) SetParent(panel.Handle, IntPtr.Zero);
            base.DestroyNativeControlCore(control);
        }

        private WinForms.Panel Panel() => panel ??= new WinForms.Panel { Dock = WinForms.DockStyle.Fill };

        [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    }
}
