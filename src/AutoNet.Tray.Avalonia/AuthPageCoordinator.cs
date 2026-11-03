namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 认证页协调器（工单 #37）：把状态读取投影为页面内容，并管理浏览器宿主的生命周期。
///
/// 关键不变量：
/// - 状态刷新只重新投影文案，**不**调用宿主导航；因此普通轮询不会重建页面或丢输入。
/// - 每次状态刷新先让宿主核对后台会话（#39）：会话进行中不保留第二个浏览器，会话结束后
///   只对本页打开过的情况恢复一次只读附着，不重放页面操作。
/// - 打开、关闭、切换页面与最小化只改变宿主可见性，**不**导航、不提交。
/// - 只有页面首次打开、用户明确重试，或通道会话结束后恢复一个成功附着过的页面，才调用
///   <see cref="IAuthPageBrowserHost.AttachAsync"/>；同一地址重复附着由宿主自身去重。
/// - 宿主失败被转换为受控状态，异常不逃逸到界面事件循环。
///
/// 线程契约：<see cref="Update"/> 可从任意线程调用（状态管道在读取线程上回调），它会把
/// 投影与 <see cref="Updated"/> 交给注入的界面线程调度器；<see cref="EnsureAttachedAsync"/>、
/// <see cref="RetryAsync"/>、<see cref="SetPageVisible"/>、<see cref="SetWindowVisible"/> 与
/// <see cref="Dispose"/> 必须在界面线程调用，因此宿主只会收到界面线程上的
/// <c>AttachAsync</c>/<c>SetVisible</c>/<c>Dispose</c>。
/// </summary>
internal sealed class AuthPageCoordinator : IDisposable
{
    private readonly IAuthPageBrowserHost host;
    private readonly Action<Action> dispatch;
    private readonly Func<AuthChannelLaunchState?>? channelState;
    private StatusReading? lastReading;
    private AuthPageHostSnapshot? failureOverride;
    private AuthPageHostSnapshot? failureObserved;
    private AuthPageContent current;
    private bool attachAttempted;
    // 只有成功附着过才允许会话结束后自动恢复；受控失败仍要求用户显式重试。
    private bool hadAttached;
    private bool pageVisible;
    private bool windowVisible = true;
    private bool disposed;

    /// <param name="dispatch">
    /// 界面线程调度器。默认同步执行，便于无 UI 的回归；生产装配传入 Avalonia 的
    /// <c>Dispatcher.UIThread</c>。协调器自身不引用任何 UI 类型。
    /// </param>
    /// <param name="channelState">
    /// 手动认证修复卡片的只读投影。延迟绑定：发起器在协调器之后装配，这里只读取当前状态。
    /// </param>
    public AuthPageCoordinator(IAuthPageBrowserHost host, Action<Action>? dispatch = null,
        Func<AuthChannelLaunchState?>? channelState = null)
    {
        this.host = host;
        this.dispatch = dispatch ?? (action => action());
        this.channelState = channelState;
        current = AuthPageModel.From(null, host.Snapshot, channelState?.Invoke());
    }

    public IAuthPageBrowserHost Host => host;

    /// <summary>最近一次投影的页面内容。</summary>
    public AuthPageContent Current => current;

    /// <summary>宿主界面表面；未就绪时为 null。模型不读取它。</summary>
    public object? Surface => host.Surface;

    /// <summary>页面内容变化（状态投影或宿主附着完成）；总是在界面线程触发。</summary>
    public event Action<AuthPageContent>? Updated;

    /// <summary>
    /// 重新投影状态；不触发导航或提交。可在任意线程调用：投影与事件都经界面线程调度器执行，
    /// 因此返回的可能是调度前的旧内容（壳只依赖 <see cref="Updated"/>）。
    /// </summary>
    public AuthPageContent Update(StatusReading? reading)
    {
        var content = current;
        dispatch(() =>
        {
            lastReading = reading;
            // 先核对后台会话：会话开始时宿主会丢弃自己的浏览器与代理，只投影会话状态。
            // 核对失败不阻断投影：保留上一次状态，下一次刷新再试。
            if (!disposed)
            {
                try { host.Refresh(); }
                catch (Exception) { }
            }
            content = current = AuthPageModel.From(reading, Snapshot(), channelState?.Invoke());
            NoteHostState();
            if (!disposed) Updated?.Invoke(current);
            ResumeAfterChannelSession();
        });
        return content;
    }

    /// <summary>
    /// 记录“本页打开过且边界已就绪”：只有它才允许会话结束后自动恢复。Attaching 也算——
    /// 页面已打开、代理已启动，只是导航尚未完成；这不等于宣称已加载（状态仍是 Attaching）。
    /// 受控失败会清掉这个事实，未附着与会话进行中都不改变它。
    /// </summary>
    private void NoteHostState()
    {
        if (host.Snapshot.Attached || host.Snapshot.Status == AuthPageHostStatus.Attaching) hadAttached = true;
        else if (host.Snapshot.Status is AuthPageHostStatus.RuntimeMissing
            or AuthPageHostStatus.NetworkBoundaryUnavailable or AuthPageHostStatus.Failed)
            hadAttached = false;
    }

    /// <summary>
    /// 后台通道会话结束（工单 #39）：宿主回到未附着时，只有成功附着过的页面才恢复一次只读附着；
    /// 从未打开过、或上次附着受控失败的页面都不会被自动拉起。恢复不重放页面操作，也不提交凭据。
    /// </summary>
    private void ResumeAfterChannelSession()
    {
        if (disposed || !hadAttached || host.Snapshot.Status != AuthPageHostStatus.NotAttached) return;
        attachAttempted = false;
        _ = EnsureAttachedAsync();
    }

    /// <summary>
    /// 宿主状态以宿主自己的快照为准；只有“附着抛异常”的失败投影会保留到宿主状态变化
    /// 或用户显式重试，避免后续刷新把受控失败误报成未附着/成功。
    /// </summary>
    private AuthPageHostSnapshot Snapshot()
    {
        if (failureOverride is null) return host.Snapshot;
        if (host.Snapshot == failureObserved) return failureOverride;
        failureOverride = null;
        failureObserved = null;
        return host.Snapshot;
    }

    /// <summary>
    /// 首次打开认证页时附着固定门户地址。已附着或已尝试过时不重复导航；
    /// 失败后只允许用户显式重试，不会在轮询中自动重试。
    /// </summary>
    public Task<AuthPageContent> EnsureAttachedAsync(CancellationToken cancellationToken = default)
    {
        if (disposed || host.Snapshot.Attached || attachAttempted)
            return Task.FromResult(current);
        // 在进入异步方法前同步置位，避免同步完成时重复进入（宿主可能立即返回受控失败）。
        attachAttempted = true;
        return AttachCoreAsync(cancellationToken);
    }

    /// <summary>用户显式重试：清除失败记录后重新附着；仍只加载固定门户根地址。</summary>
    public Task<AuthPageContent> RetryAsync(CancellationToken cancellationToken = default)
    {
        if (disposed) return Task.FromResult(current);
        failureOverride = null;
        failureObserved = null;
        attachAttempted = false;
        return EnsureAttachedAsync(cancellationToken);
    }

    /// <summary>页面是否被选中；与窗口可见性共同决定宿主表面是否显示，不影响后台。</summary>
    public void SetPageVisible(bool visible)
    {
        pageVisible = visible;
        ApplyVisibility();
    }

    /// <summary>窗口是否可见（最小化或隐藏时为 false）；不导航、不提交、不重试附着。</summary>
    public void SetWindowVisible(bool visible)
    {
        windowVisible = visible;
        ApplyVisibility();
    }

    private async Task<AuthPageContent> AttachCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await host.AttachAsync(new Uri(AuthPageModel.PortalAddress), cancellationToken).ConfigureAwait(false);
            return Publish(() =>
            {
                NoteHostState();
                return AuthPageModel.From(lastReading, Snapshot(), channelState?.Invoke());
            });
        }
        catch (OperationCanceledException)
        {
            // 取消不是失败：保留当前投影，下一次打开页面仍可附着。状态只在界面线程上改写。
            dispatch(() => attachAttempted = false);
        }
        catch (Exception)
        {
            // 受控失败：只披露宿主失败分类，不携带异常文本、URL 或页面内容。
            return Publish(() =>
            {
                failureObserved = host.Snapshot;
                failureOverride = new(AuthPageHostStatus.Failed, AuthPageModel.PortalAddress, false,
                    AuthPageModel.HostCopyFor(AuthPageHostStatus.Failed).Detail);
                hadAttached = false;
                return AuthPageModel.From(lastReading, failureOverride, channelState?.Invoke());
            });
        }
        return current;
    }

    /// <summary>
    /// 在界面线程上应用一次投影并触发 <see cref="Updated"/>；附着可能在线程池上完成，
    /// 因此这里不能直接改写状态。返回调度前的旧内容（同步调度时即为新内容）。
    /// </summary>
    private AuthPageContent Publish(Func<AuthPageContent> project)
    {
        var content = current;
        dispatch(() =>
        {
            content = current = project();
            if (!disposed) Updated?.Invoke(current);
        });
        return content;
    }

    private void ApplyVisibility() => host.SetVisible(pageVisible && windowVisible);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        host.Dispose();
    }
}
