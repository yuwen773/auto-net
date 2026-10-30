namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 认证页协调器（工单 #37）：把状态读取投影为页面内容，并管理浏览器宿主的生命周期。
///
/// 关键不变量：
/// - 状态刷新只重新投影文案，**不**调用宿主导航；因此普通轮询不会重建页面或丢输入。
/// - 打开、关闭、切换页面与最小化只改变宿主可见性，**不**导航、不提交。
/// - 只有页面首次打开或用户明确重试才调用 <see cref="IAuthPageBrowserHost.AttachAsync"/>；
///   同一地址重复附着由宿主自身去重。
/// - 宿主失败被转换为受控状态，异常不逃逸到界面事件循环。
/// </summary>
internal sealed class AuthPageCoordinator : IDisposable
{
    private readonly IAuthPageBrowserHost host;
    private readonly Uri portal;
    private StatusReading? lastReading;
    private AuthPageHostSnapshot? failureOverride;
    private AuthPageHostSnapshot? failureObserved;
    private AuthPageContent current;
    private bool attachAttempted;
    private bool pageVisible;
    private bool windowVisible = true;
    private bool disposed;

    public AuthPageCoordinator(IAuthPageBrowserHost host, Uri? portal = null)
    {
        this.host = host;
        this.portal = portal ?? new Uri(AuthPageModel.PortalAddress);
        current = AuthPageModel.From(null, host.Snapshot);
    }

    public IAuthPageBrowserHost Host => host;

    /// <summary>最近一次投影的页面内容。</summary>
    public AuthPageContent Current => current;

    /// <summary>宿主界面表面；未就绪时为 null。模型不读取它。</summary>
    public object? Surface => host.Surface;

    /// <summary>页面内容变化（状态投影或宿主附着完成）；由壳调度到界面线程。</summary>
    public event Action<AuthPageContent>? Updated;

    /// <summary>重新投影状态；不触发导航或提交。</summary>
    public AuthPageContent Update(StatusReading? reading)
    {
        lastReading = reading;
        current = AuthPageModel.From(reading, Snapshot());
        if (!disposed) Updated?.Invoke(current);
        return current;
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
            await host.AttachAsync(portal, cancellationToken).ConfigureAwait(false);
            current = AuthPageModel.From(lastReading, Snapshot());
        }
        catch (OperationCanceledException)
        {
            // 取消不是失败：保留当前投影，下一次打开页面仍可附着。
            attachAttempted = false;
        }
        catch (Exception)
        {
            // 受控失败：只披露宿主失败分类，不携带异常文本、URL 或页面内容。
            failureObserved = host.Snapshot;
            failureOverride = new(AuthPageHostStatus.Failed, AuthPageModel.PortalAddress, false,
                "浏览器宿主未能完成加载；可手动重试，后台认证不受影响。");
            current = AuthPageModel.From(lastReading, failureOverride);
        }
        finally
        {
            if (!disposed) Updated?.Invoke(current);
        }
        return current;
    }

    private void ApplyVisibility() => host.SetVisible(pageVisible && windowVisible);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        host.Dispose();
    }
}
