namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 壳控制器：把导航选择、页面内容代次守卫与状态管道接到一起；不依赖 UI 类型，便于受控测试。
/// 概览与诊断页内容按状态投影同步重建（#4、#6）；其余页面经 <see cref="PageLoadCoordinator"/> 异步加载（后续工单）。
/// 事件在触发线程上回调（状态事件来自读取线程，UI 侧负责调度到界面线程）。
/// </summary>
internal sealed class ShellController : IDisposable
{
    private readonly StatusPipe pipe;
    private readonly PageLoadCoordinator coordinator = new();
    private readonly Func<ShellPage, CancellationToken, Task<PageContent>> load;

    public ShellController(StatusPipe pipe, Func<ShellPage, CancellationToken, Task<PageContent>>? load = null)
    {
        this.pipe = pipe;
        this.load = load ?? ShellContent.LoadAsync;
        pipe.Updated += OnStatusUpdated;
    }

    /// <summary>当前选中的页面；选中立即生效，内容经代次守卫异步应用。</summary>
    public ShellPage SelectedPage { get; private set; } = ShellPages.Default;

    /// <summary>最近一次已应用的页面内容。</summary>
    public PageContent? Content { get; private set; }

    /// <summary>最近一次已应用的状态读取（含可信度与状态栏文本）。</summary>
    public StatusReading? Status => pipe.Current;

    public event Action<ShellPage>? SelectionChanged;
    public event Action<PageContent>? ContentChanged;
    public event Action<StatusReading>? StatusChanged;

    /// <summary>启动：开始状态轮询并选中默认页。</summary>
    public void Start()
    {
        pipe.Start();
        _ = SelectAsync(ShellPages.Default);
    }

    /// <summary>选中页面：立即更新选择；内容按代次守卫异步应用（旧读取不覆写当前页）。</summary>
    public Task SelectAsync(ShellPage page)
    {
        SelectedPage = page;
        SelectionChanged?.Invoke(page);
        if (page == ShellPage.Overview)
        {
            // 概览内容由当前状态投影而来：纯函数、无 I/O，同步应用，无需代次守卫。
            ApplyOverview(pipe.Current);
            return Task.CompletedTask;
        }

        if (page == ShellPage.Diagnostics)
        {
            // 诊断页同样由状态投影（#6）：纯函数、无 I/O、无动作；同步应用，状态更新时跟随重建。
            ApplyDiagnostics(pipe.Current);
            return Task.CompletedTask;
        }

        return coordinator.NavigateAsync(page, load, ApplyContent, ApplyFailure);
    }

    /// <summary>立即刷新一次状态（窗口激活时调用；结果同样按序应用，旧读不覆盖新读）。</summary>
    public Task RefreshStatusAsync(CancellationToken cancellationToken = default) =>
        pipe.RefreshAsync(cancellationToken);

    private void ApplyOverview(StatusReading? reading)
    {
        Content = OverviewModel.From(reading);
        ContentChanged?.Invoke(Content);
    }

    private void ApplyDiagnostics(StatusReading? reading)
    {
        Content = DiagnosticsModel.From(reading);
        ContentChanged?.Invoke(Content);
    }

    private void ApplyContent(ShellPage page, PageContent content)
    {
        // 双保险：守卫之外的当前选择核对，防止任何路径把旧页面内容写进新页面。
        if (SelectedPage != page) return;
        Content = content;
        ContentChanged?.Invoke(content);
    }

    /// <summary>
    /// 页内交互更新的内容应用（#7 日志页等）：与导航内容走同一守卫——页面已切走时结果丢弃，
    /// 旧任务不覆写当前页（故事 16、50）。
    /// </summary>
    internal void ApplyExternal(PageContent content)
    {
        if (SelectedPage != content.Page) return;
        Content = content;
        ContentChanged?.Invoke(content);
    }

    private void ApplyFailure(ShellPage page, Exception exception)
    {
        if (SelectedPage != page) return;
        var info = ShellPages.Info(page);
        // 只披露异常类型，不带任何消息细节（与既有界面的受控错误口径一致）。
        Content = new PageScaffold(page, info.Name, info.Description,
            "此页暂时无法加载（" + exception.GetType().Name + "）；可稍后重试。");
        ContentChanged?.Invoke(Content);
    }

    private void OnStatusUpdated(StatusReading reading)
    {
        StatusChanged?.Invoke(reading);
        // 概览与诊断页跟随状态更新重建（两者都是纯投影）；其他页面不被状态更新改写。
        if (SelectedPage == ShellPage.Overview) ApplyOverview(reading);
        else if (SelectedPage == ShellPage.Diagnostics) ApplyDiagnostics(reading);
    }

    public void Dispose()
    {
        pipe.Updated -= OnStatusUpdated;
        coordinator.Dispose();
        pipe.Dispose();
    }
}
