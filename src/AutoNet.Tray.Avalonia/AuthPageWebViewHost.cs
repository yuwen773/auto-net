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
    private AuthPageBrowserSession? session;
    private int generation;
    private int attachAttempt;
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
        if (channelSessionActive())
        {
            // 后台通道会话进行中：不创建第二个浏览器，也不保留自己的代理。
            EnterChannelSession();
            return Snapshot;
        }
        if (Snapshot.Status is AuthPageHostStatus.Attached or AuthPageHostStatus.Attaching)
        {
            // 已附着或正在加载：不重新导航；只补一次错过的表面初始化。
            if (session is { NavigationRequested: false, Starting: false } pending && proxy is not null && surface.IsCreated)
                _ = InitializeBrowserAsync(pending, cancellationToken);
            return Snapshot;
        }

        // 上一次尝试残留的代理/浏览器不再有意义；进程组保留并复用，避免重复嵌套作业。
        ResetBrowser();
        await StopProxyAsync().ConfigureAwait(true);
        // 附着本身也会跨越 await：旧尝试晚于新尝试完成时，只能丢弃自己刚启动的代理，
        // 不能覆盖新尝试的 proxy 字段或发布旧状态。
        var attempt = Interlocked.Increment(ref attachAttempt);
        var (failure, boundary) = await AuthPageNetworkBoundary.PrepareAsync(
            portal, runtimeProbe(), readLink, EnsureJob, startProxy, cancellationToken).ConfigureAwait(true);
        var current = attempt == Volatile.Read(ref attachAttempt) && Volatile.Read(ref disposed) == 0;
        if (failure is not null)
        {
            if (current) Publish(failure);
            return Snapshot;
        }
        if (!current)
        {
            try { await boundary!.Proxy.DisposeAsync().ConfigureAwait(true); }
            catch (Exception) { }
            return Snapshot;
        }

        proxy = boundary!.Proxy;
        // 后台会话可能在准备边界期间开始：丢弃刚启动的代理，只投影会话状态。
        if (channelSessionActive())
        {
            EnterChannelSession();
            return Snapshot;
        }
        // 发布 Attaching：视图据此挂载原生表面（HostAvailable 包含该状态），
        // 表面创建后启动浏览器；只有导航成功才会进入 Attached。
        var created = BeginSession();
        // 原生表面可能还没创建（首次打开时视图尚未渲染），由 CreateNativeControlCore 回调补上。
        if (surface.IsCreated) _ = InitializeBrowserAsync(created, cancellationToken);
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
        if (visible && !channelSessionActive() && proxy is not null && surface.IsCreated
            && session is { NavigationRequested: false, Starting: false } pending)
            _ = InitializeBrowserAsync(pending, CancellationToken.None);
    }

    /// <summary>原生表面创建后回调：此时才有 HWND 可承载 WebView2。</summary>
    internal void OnSurfaceCreated()
    {
        if (proxy is not null && !channelSessionActive() && session is { } pending)
            _ = InitializeBrowserAsync(pending, CancellationToken.None);
    }

    private ChannelProcessJob EnsureJob() => job ??= ChannelProcessJob.CreateForCurrentProcess();

    /// <summary>开始一次新的附着尝试：递增代次，使旧会话的异步回调和事件全部失效。</summary>
    private AuthPageBrowserSession BeginSession()
    {
        ResetBrowser();
        generation++;
        var created = new AuthPageBrowserSession(generation,
            Path.Combine(Path.GetTempPath(), "autonet-authpage-" + Guid.NewGuid().ToString("N")));
        session = created;
        Publish(new AuthPageHostSnapshot(AuthPageHostStatus.Attaching, AuthPageModel.PortalAddress, false,
            AuthPageModel.HostCopyFor(AuthPageHostStatus.Attaching).Detail));
        return created;
    }

    /// <summary>代次校验：旧会话、已销毁宿主和被替换的会话都不再允许写状态或导航。</summary>
    private bool IsCurrent(AuthPageBrowserSession candidate) =>
        IsCurrentSession(Volatile.Read(ref disposed), Volatile.Read(ref generation), session, candidate);

    internal static bool IsCurrentSession(int disposed, int generation, AuthPageBrowserSession? active, AuthPageBrowserSession candidate) =>
        disposed == 0 && !candidate.IsDisposed && candidate.Generation == generation && ReferenceEquals(active, candidate);

    private async Task InitializeBrowserAsync(AuthPageBrowserSession current, CancellationToken cancellationToken)
    {
        if (!IsCurrent(current) || current.Starting || current.NavigationRequested || proxy is null || job is null) return;
        var processJob = job;
        var proxyPort = proxy.Port;
        current.Starting = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(current.Cancellation.Token, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // 必须用命名参数：位置参数会把用户数据目录绑定到 browserExecutableFolder，
            // 随后误报“运行时未安装”，把路径问题伪装成环境问题。
            var options = new CoreWebView2EnvironmentOptions(AuthPageBrowserArguments.For(proxyPort));
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: current.Directory, options: options)
                .WaitAsync(timeout.Token).ConfigureAwait(true);
            if (!IsCurrent(current)) return;
            current.Environment = environment;
            environment.BrowserProcessExited += (_, _) => current.MarkExited();
            var view = new WebView { Dock = WinForms.DockStyle.Fill };
            current.WebView = view;
            surface.Add(view);
            await view.EnsureCoreWebView2Async(environment).WaitAsync(timeout.Token).ConfigureAwait(true);
            // 后台会话可能在初始化等待期间开始：不再导航，立即拆除并投影会话状态，避免第二条会话。
            if (!IsCurrent(current)) return;
            if (channelSessionActive())
            {
                EnterChannelSession();
                return;
            }
            var core = view.CoreWebView2;
            if (!processJob.Contains(checked((int)core.BrowserProcessId)) ||
                environment.GetProcessInfos().Any(info => !processJob.Contains(checked((int)info.ProcessId))))
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
            // 事件可能来自已被替换的会话：在界面线程再次校验代次后才允许改写状态。
            core.ProcessFailed += (_, e) =>
            {
                if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited
                    or CoreWebView2ProcessFailedKind.RenderProcessExited
                    or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsCurrent(current)) FailBrowser(new InvalidOperationException("浏览器进程异常退出。"));
                });
            };
            // 弹窗仍会走同一个浏览器与代理，但认证页只承载一个固定门户页面，不需要新窗口。
            core.NewWindowRequested += (_, e) => e.Handled = true;
            // 资源过滤只作为纵深防御，**不是**隔离依据：真正的边界是代理。
            core.NavigationStarting += (_, e) =>
            {
                if (!IsPortalOrigin(e.Uri)) e.Cancel = true;
            };
            // 只有导航成功才算附着：导航失败/被取消时页面是空白的，状态不能停在“已加载”。
            // 首次结果决定状态；确认成功之后的主框架导航（门户自身跳转、被来源校验取消的跳转）
            // 不再改写页面状态，避免迟到的失败事件把已加载页面打成可重试失败。
            core.NavigationCompleted += (_, e) => Dispatcher.UIThread.Post(() =>
            {
                if (!IsCurrent(current)) return;
                switch (AuthPageNavigationGate.Decide(current.NavigationConfirmed, e.IsSuccess))
                {
                    case AuthPageNavigationGate.Decision.Loaded:
                        current.NavigationConfirmed = true;
                        Publish(new AuthPageHostSnapshot(AuthPageHostStatus.Attached, AuthPageModel.PortalAddress, true,
                            AuthPageModel.HostCopyFor(AuthPageHostStatus.Attached).Detail));
                        break;
                    case AuthPageNavigationGate.Decision.Failed:
                        FailBrowser(new InvalidOperationException("门户页面导航未成功。"));
                        break;
                }
            });
            current.NavigationRequested = true;
            core.Navigate(AuthPageModel.PortalAddress);
        }
        catch (Exception e)
        {
            // 旧代次的取消/销毁异常只丢弃；只有当前会话的失败才改写页面状态。
            if (IsCurrent(current)) FailBrowser(e);
        }
        finally
        {
            current.Starting = false;
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

    /// <summary>
    /// 丢弃当前浏览器会话，但保留进程组；下一次附着会重新创建浏览器。
    /// 取消旧代次后，其未完成的 await 与事件一律被代次校验丢弃。
    /// </summary>
    private void ResetBrowser()
    {
        // 任何拆除动作都会让在途的边界准备失效：它完成时不得再改写新会话的代理或状态。
        Interlocked.Increment(ref attachAttempt);
        var discarding = session;
        session = null;
        if (discarding is null) return;
        discarding.Cancel();
        var exited = discarding.BrowserExited;
        discarding.Dispose();
        // 临时目录里有门户 cookie/缓存：等浏览器进程退出后再删，失败留给启动清扫。
        _ = Task.Run(() => DeleteSessionDirectoryAsync(discarding.Directory, exited));
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
        generation++;
        ResetBrowser();
        // 不阻塞界面线程等待代理收尾；监听端口随进程/宿主销毁一并消失。
        _ = StopProxyAsync();
    }

    /// <summary>
    /// 删除一次会话的临时用户目录：先有界等待浏览器进程退出，再按退避重试。
    /// 总等待约 30 秒；失败不抛给调用方，目录留给下次启动清扫。
    /// </summary>
    private static async Task DeleteSessionDirectoryAsync(string path, Task browserExited)
    {
        try { await browserExited.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (System.TimeoutException) { }
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1))).ConfigureAwait(false);
            }
        }
    }

    /// <summary>启动时清扫超过 24 小时的会话目录；不阻塞界面线程。</summary>
    internal static void SweepStaleSessions() =>
        _ = Task.Run(() => SweepStaleSessions(Path.GetTempPath(), DateTimeOffset.UtcNow));

    /// <summary>
    /// 只删除本应用创建、已过期且当前没有会话占用的临时目录。
    /// 名称必须是 <c>autonet-authpage-</c> + 32 位十六进制；必须是临时目录的直接子项；
    /// 目录本身是重解析点时跳过；删除时也不进入任何重解析点。删除异常只跳过该项。
    /// </summary>
    internal static int SweepStaleSessions(string tempRoot, DateTimeOffset now, TimeSpan? maxAge = null)
    {
        var removed = 0;
        var cutoff = now - (maxAge ?? TimeSpan.FromHours(24));
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(tempRoot, "autonet-authpage-*"))
            {
                try
                {
                    if (!IsOwnedSessionDirectory(directory, tempRoot)) continue;
                    if (AuthPageBrowserSession.IsActiveDirectory(directory)) continue;
                    if (Directory.GetLastWriteTimeUtc(directory) >= cutoff.UtcDateTime) continue;
                    DeleteOwnedDirectory(directory);
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
        return removed;
    }

    /// <summary>名称形状校验：固定前缀 + Guid "N" 的 32 位十六进制，防止误删相近名称。</summary>
    internal static bool IsOwnedSessionName(string name)
    {
        const string prefix = "autonet-authpage-";
        if (name.Length != prefix.Length + 32 || !name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        foreach (var character in name.AsSpan(prefix.Length))
            if (!char.IsAsciiHexDigit(character)) return false;
        return true;
    }

    internal static bool IsOwnedSessionDirectory(string path, string tempRoot)
    {
        try
        {
            if (!IsOwnedSessionName(Path.GetFileName(path))) return false;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            return string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
                Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>逐项删除但决不进入重解析点；残留链接会让本次删除失败并留给下次清扫。</summary>
    private static void DeleteOwnedDirectory(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            if ((attributes & FileAttributes.Directory) != 0) DeleteOwnedDirectory(entry);
            else File.Delete(entry);
        }
        Directory.Delete(root, recursive: false);
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
