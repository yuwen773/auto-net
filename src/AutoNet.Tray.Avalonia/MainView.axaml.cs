using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 壳视图：左侧五页导航、页面宿主与底部状态栏。只负责把 <see cref="ShellController"/>
/// 的选择、内容与状态渲染出来；导航与读取语义都在控制器一侧，动作语义在
/// <see cref="OverviewActions"/> 一侧，便于受控测试。
/// 窄窗口下导航轨折叠为图标列，页面边距同步收窄（响应式只改布局密度，不改变任何语义）。
/// </summary>
public partial class MainView : UserControl
{
    /// <summary>导航轨折叠阈值：低于该宽度时只保留图标（窗口最小宽度 720）。</summary>
    private const double NavCollapseWidth = 880;

    private ShellController? controller;
    private OverviewActions? actions;
    private LogsPage? logsPage;
    private Func<string, Task>? copyHandler;
    private Action? folderHandler;
    private SettingsPage? settingsPage;
    private Control? settingsControl;
    private Action? portalOpenHandler;
    private DiagnosticsExporter? diagnosticsExporter;
    private AuthChannelLauncher? authChannelLauncher;
    private AuthPageCoordinator? authPage;
    private readonly AuthPageRenderGate<AuthPageView> authGate = new();
    private readonly List<(ListBoxItem Item, Border Indicator, TextBlock Label)> navEntries = [];
    private AuthPageView? authViewShown;
    private DiagnosticsExportState? exportStateShown;
    private Func<ThemePreference>? themeProvider;
    private Action<ThemePreference>? themeSelectedHandler;
    private OverviewContent? overviewShown;
    private DiagnosticsContent? diagnosticsShown;
    private LogsContent? logsShown;
    private ShellPage? pageShown;
    private bool technicalExpanded;
    private bool syncingSelection;
    private bool channelConfirming;
    private bool navCollapsed;

    public MainView()
    {
        InitializeComponent();
        nav.SelectionChanged += (_, _) =>
        {
            UpdateNavIndicators();
            if (syncingSelection) return;
            if (nav.SelectedItem is ListBoxItem { Tag: ShellPage page } && page != controller?.SelectedPage)
                NavigateSafelyAsync(page);
        };
        // 认证页的浏览器需要确定的高度：窗口变化后重新判断是否放得下宿主。
        pageScroll.LayoutUpdated += (_, _) => UpdateAuthPageFit();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    /// <summary>接入壳控制器、概览动作、日志页（#7）、设置页（#8 / #41）、兜底开页（#11）、诊断包导出（#12）与主题选择（#9）并建立渲染通路。应用内只会接入一次。</summary>
    internal void Attach(
        ShellController shell,
        OverviewActions? overviewActions = null,
        LogsPage? logs = null,
        Func<string, Task>? copy = null,
        Action? openFolder = null,
        SettingsPage? settings = null,
        Action? portalOpen = null,
        Func<ThemePreference>? themeProvider = null,
        Action<ThemePreference>? themeSelected = null,
        DiagnosticsExporter? export = null,
        AuthChannelLauncher? authChannel = null,
        AuthPageCoordinator? authPage = null)
    {
        controller = shell;
        actions = overviewActions;
        logsPage = logs;
        copyHandler = copy;
        folderHandler = openFolder;
        settingsPage = settings;
        portalOpenHandler = portalOpen;
        this.themeProvider = themeProvider;
        themeSelectedHandler = themeSelected;
        authChannelLauncher = authChannel;
        this.authPage = authPage;
        diagnosticsExporter = export;
        if (export is { } exporter)
        {
            // 导出状态在流程任务线程上推进：调度回界面线程后，若诊断页正在显示就立即重绘
            // （内容指纹未变但导出卡需要刷新）。
            exporter.StateChanged += () => Dispatcher.UIThread.Post(() =>
            {
                if (controller?.SelectedPage == ShellPage.Diagnostics && controller.Content is { } content)
                {
                    diagnosticsShown = null;
                    RenderContent(content);
                }
            });
        }
        BuildNavigation();
        shell.SelectionChanged += page => Dispatcher.UIThread.Post(() =>
        {
            SyncSelection(page);
            // 进入设置页时重新枚举 Wi-Fi 配置：启动时 WLAN 未就绪或之后新建的配置
            // 不应要求重启托盘才能选择。是否需要刷新由页面路由声明。
            if (ShellRoutes.For(page).RefreshesProfilesOnEnter) settingsPage?.RefreshProfiles();
        });
        shell.ContentChanged += content => Dispatcher.UIThread.Post(() => RenderContent(content));
        shell.StatusChanged += reading => Dispatcher.UIThread.Post(() => RenderStatus(reading));
        SyncSelection(shell.SelectedPage);
        if (shell.Content is { } content) RenderContent(content);
        if (shell.Status is { } status) RenderStatus(status);
    }

    private async void NavigateSafelyAsync(ShellPage page)
    {
        try
        {
            await controller!.SelectAsync(page);
        }
        catch (Exception)
        {
            // 导航路径上的异常不逃逸到 UI 事件循环；页面脚手架由控制器降级呈现。
        }
    }

    /// <summary>动作点击共用的安全包装：概览动作自身失败关闭，异常也不逃逸到 UI 事件循环。</summary>
    private async void RunActionSafelyAsync(Func<Task<bool>> operation)
    {
        try { await operation(); }
        catch (Exception)
        {
        }
    }

    private void BuildNavigation()
    {
        nav.Items.Clear();
        navEntries.Clear();
        foreach (var info in ShellPages.All)
        {
            var icon = new ShapePath
            {
                Data = Geometry.Parse(ShellIcons.For(info.Page)),
                Width = 17,
                Height = 17,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            // 图标跟随条目前景色（含选中态）。
            icon.Bind(Shape.StrokeProperty, new Binding(nameof(TextBlock.Foreground))
            {
                RelativeSource = new RelativeSource { Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(ListBoxItem) }
            });

            var label = new TextBlock
            {
                Text = info.Name,
                FontSize = 13,
                FontWeight = FontWeight.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            // 选中指示：设置式导航的左侧短竖条，仅在当前页可见（形状之外的冗余提示）。
            var indicator = ShellTheming.Themed(new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(2),
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Center
            }, Border.BackgroundProperty, "NavSelectedText");
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children = { indicator, icon, label }
            };
            // 导航项的可访问名称与页面可见名称一致（#14 / 故事 33）；图标是装饰，文本承载信息。
            var item = ShellAccessibility.Named(
                new ListBoxItem
                {
                    Tag = info.Page,
                    Content = content,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                },
                ShellAccessibility.NavItem(info.Page));
            // 折叠态只剩图标：工具提示补回名称，悬停即可辨认。
            ToolTip.SetTip(item, info.Name);
            nav.Items.Add(item);
            navEntries.Add((item, indicator, label));
        }
        ApplyResponsiveLayout();
        UpdateNavIndicators();
    }

    /// <summary>当前页的导航项显示左侧短竖条；其余隐藏。</summary>
    private void UpdateNavIndicators()
    {
        foreach (var (item, indicator, _) in navEntries)
            indicator.IsVisible = nav.SelectedItem == item;
    }

    /// <summary>
    /// 窄窗口布局：导航轨折叠为图标列（品牌文字换成标记），页面边距按宽度分档收窄。
    /// 只在密度阈值上切换，不重建控件树，也不影响焦点与选择。
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        var width = Bounds.Width;
        var collapsed = width > 0 && width < NavCollapseWidth;
        if (collapsed != navCollapsed)
        {
            navCollapsed = collapsed;
            navRail.Width = collapsed ? 76 : 236;
            brandBlock.IsVisible = !collapsed;
            brandMark.IsVisible = collapsed;
            foreach (var (item, _, label) in navEntries)
            {
                label.IsVisible = !collapsed;
                item.HorizontalContentAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            }
        }
        pageHost.Margin = width < 900
            ? new Thickness(24, 26, 24, 32)
            : new Thickness(36, 32, 36, 40);
    }

    /// <summary>
    /// 页面切换的一次性入场：先回到页首，再以轻微淡入（0.7 → 1）过渡，避免新页面继承上一页的滚动位置，
    /// 也避免首帧完全透明导致内容短暂不可见。系统关闭客户端动画时直接呈现终态。
    /// </summary>
    private void AnimatePageIn()
    {
        pageScroll.Offset = new Vector(0, 0);
        if (!MotionSettings.AnimationsEnabled)
        {
            pageHost.Opacity = 1;
            return;
        }
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(170),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.7d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } }
            }
        };
        _ = animation.RunAsync(pageHost);
    }

    private void SyncSelection(ShellPage page)
    {
        var index = ShellPages.All.Select(info => info.Page).ToList().IndexOf(page);
        if (index < 0 || nav.SelectedIndex == index) return;
        syncingSelection = true;
        try
        {
            nav.SelectedIndex = index;
        }
        finally
        {
            syncingSelection = false;
        }
    }

    private void RenderContent(PageContent content)
    {
        // 认证页按视口高度布局并承载浏览器；其余页面保持顶部对齐与滚动。
        if (content is not AuthPageContent)
        {
            pageHost.VerticalAlignment = VerticalAlignment.Top;
            if (pageScroll.VerticalScrollBarVisibility != ScrollBarVisibility.Auto)
                pageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        switch (content)
        {
            case OverviewContent overview:
                // 指纹未变化时不重建：轮询刷新不打断技术详情展开状态与键盘焦点。
                if (overviewShown is { } shown && shown.ContentKey == overview.ContentKey) return;
                overviewShown = overview;
                ClearShown(except: overview);
                pageHost.Content = OverviewView.Build(
                    overview,
                    technicalExpanded,
                    expanded => technicalExpanded = expanded,
                    intent => RunActionSafelyAsync(() => actions is null ? Task.FromResult(false) : actions.RequestRecoveryChangeAsync(intent)),
                    () => RunActionSafelyAsync(() => actions is null ? Task.FromResult(false) : actions.RequestUnblockAsync()),
                    () => portalOpenHandler?.Invoke(),
                    () => RunActionSafelyAsync(() => actions is null ? Task.FromResult(false) : actions.RequestRestartAuthenticationAsync()));
                break;
            case DiagnosticsContent diagnostics:
                // 指纹未变化且导出状态未推进时不重建：诊断页避免轮询期的无谓控件树重建；
                // 导出卡（#12）由状态机推进（StateChanged 已单独触发重绘），这里按引用比对。
                var exportState = diagnosticsExporter?.State;
                if (diagnosticsShown is { } shownDiagnostics
                    && shownDiagnostics.ContentKey == diagnostics.ContentKey
                    && ReferenceEquals(exportStateShown, exportState)) return;
                exportStateShown = exportState;
                diagnosticsShown = diagnostics;
                ClearShown(except: diagnostics);
                pageHost.Content = DiagnosticsView.Build(
                    diagnostics,
                    exportState,
                    () => RunActionSafelyAsync(() =>
                    {
                        if (diagnosticsExporter is { } exporter) _ = exporter.RunAsync();
                        return Task.FromResult(true);
                    }));
                break;
            case LogsContent logs:
                // 指纹未变化时不重建：日志页交互（日期、筛选、选中行）都会改变指纹，重建即换内容。
                if (logsShown is { } shownLogs && shownLogs.ContentKey == logs.ContentKey) return;
                logsShown = logs;
                ClearShown(except: logs);
                pageHost.Content = LogsView.Build(
                    logs,
                    date => RunActionSafelyAsync(() => SelectDateAsync(date)),
                    filter => logsPage?.SetFilter(filter),
                    () => RunActionSafelyAsync(() => RefreshLogsAsync()),
                    () => RunCopyRequestedAsync(),
                    () => RunFolderRequested(),
                    index => logsPage?.SelectRow(index));
                break;
            case SettingsContent settings:
                // 设置页（#8 / #41）是可编辑表单：控件树只构建一次并长期复用，状态轮询只更新
                // 只读事实与结果文本，绝不重建正在输入的表单。主题选择（#9）同样不走状态投影。
                // 进入设置页时清空其他页面的指纹缓存：否则从设置页返回未变化的概览/诊断/日志时，
                // 指纹命中会提前 return，页面仍停留在设置控件上。
                ClearShown(except: null);
                settingsPage ??= new SettingsPage(
                    () => [],
                    (_, _) => Task.FromResult(new HandoffResult(HandoffOutcome.Rejected)));
                settingsPage.Update(settings);
                settingsControl ??= SettingsView.Build(
                    settingsPage,
                    themeProvider?.Invoke() ?? ThemePreference.System,
                    preference => themeSelectedHandler?.Invoke(preference));
                pageHost.Content = settingsControl;
                break;
            case AuthPageContent auth:
                // 认证页（#37）：视图与浏览器表面只创建一次，状态刷新只更新文字与宿主表面。
                // 切走再回来复用同一实例，因此不会重新导航，也不会丢失页面内已填内容。
                // 指纹未变化时跳过重复渲染；切页会清空指纹缓存，返回时仍会重新渲染同一实例。
                if (!authGate.ShouldRender(auth)) return;
                ClearShown(except: auth);
                AuthPageView? pendingView = null;
                pendingView = authGate.View(() => new AuthPageView(
                    () => RunActionSafelyAsync(async () =>
                    {
                        if (authPage is { } coordinator) await coordinator.RetryAsync();
                        return true;
                    }),
                    () => authPage?.Surface,
                    // 会提权并可能提交凭据，先明确确认；取消时不启动辅助进程。
                    // 连续点击只允许一个确认流程；发起器会再次读取可信快照并拒绝忙态。
                    () => RunActionSafelyAsync(async () =>
                    {
                        if (authChannelLauncher is not { } channel || channelConfirming) return true;
                        channelConfirming = true;
                        try
                        {
                            if (!await ConfirmWindow.AskAsync("手动认证修复（会提交凭据）",
                                    "这会请求管理员授权并启动辅助进程，打开固定门户页，可能自动填写并提交已保存的校园网凭据。"
                                    + "仅在协议认证失败且你确认要提交时继续。",
                                    "确认发起"))
                            {
                                pendingView?.SetChannelNotice("已取消：未请求管理员授权，也未启动辅助进程。");
                                return true;
                            }
                            var outcome = await channel.LaunchManuallyAsync();
                            // 受控结果必须呈现：不可信/关闭/忙/UAC 取消不能表现为“点了没反应”。
                            pendingView?.SetChannelNotice(AuthChannelLauncher.ManualOutcomeText(outcome));
                            return true;
                        }
                        finally { channelConfirming = false; }
                    })));
                var authView = pendingView!;
                authView.Render(auth);
                pageHost.Content = authView.Root;
                authViewShown = authView;
                // 认证页撑满视口：浏览器宿主按剩余高度伸缩，门户页按自己的视口排版。
                pageHost.VerticalAlignment = VerticalAlignment.Stretch;
                UpdateAuthPageFit();
                break;
            case PageScaffold scaffold:
                ClearShown(except: null);
                pageHost.Content = ScaffoldView(scaffold);
                break;
        }
        MarkPageEntered(content.Page);
    }

    /// <summary>页面内容首次挂载时记录并播放一次入场淡入；同一页的状态重建不重播。</summary>
    private void MarkPageEntered(ShellPage page)
    {
        if (pageShown == page) return;
        pageShown = page;
        AnimatePageIn();
    }

    /// <summary>
    /// 认证页的宿主适配：视口放得下“固定内容 + 最小宿主高度”时停止滚动，让浏览器吃满剩余高度；
    /// 放不下时退回滚动并显示占位（占位状态不挂原生表面，滚动不会让浏览器窗口错位）。
    /// 只在认证页显示期间生效；窗口尺寸变化由 <see cref="UpdateAuthPageFit"/> 反复收敛。
    /// </summary>
    private void UpdateAuthPageFit()
    {
        if (authViewShown is not { } view || !ReferenceEquals(pageHost.Content, view.Root)) return;
        var available = pageScroll.Viewport.Height - pageHost.Margin.Top - pageHost.Margin.Bottom;
        var fits = view.UpdateViewport(available);
        var visibility = fits ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        if (pageScroll.VerticalScrollBarVisibility != visibility)
            pageScroll.VerticalScrollBarVisibility = visibility;
    }

    /// <summary>只保留当前页面的内容缓存：其余页面的指纹缓存一律清空，避免跨页误判跳过。</summary>
    private void ClearShown(PageContent? except)
    {
        if (!ReferenceEquals(except, overviewShown)) overviewShown = null;
        if (!ReferenceEquals(except, diagnosticsShown)) diagnosticsShown = null;
        if (!ReferenceEquals(except, logsShown)) logsShown = null;
        // 设置页（#41）改用长期复用的表单控件，不再有设置内容的指纹缓存需要清理。
        // 认证页切走时只清指纹缓存，视图实例保留在渲染门里，返回时继续复用。
        if (except is not AuthPageContent) authGate.Detach();
    }

    private async Task<bool> SelectDateAsync(string? date)
    {
        if (logsPage is null) return false;
        await logsPage.SelectDateAsync(date);
        return true;
    }

    private async Task<bool> RefreshLogsAsync()
    {
        if (logsPage is null) return false;
        await logsPage.RefreshAsync();
        return true;
    }

    private Task RunCopyRequestedAsync()
    {
        var text = logsShown?.CopyText ?? "";
        if (text.Length == 0) return Task.CompletedTask;
        if (copyHandler is { } handler) return handler(text);
        return CopyToClipboardAsync(text);
    }

    private async Task CopyToClipboardAsync(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // 剪贴板正忙或不可用时静默：复制动作不打断界面（与既有托盘的容错口径一致）。
        }
    }

    private void RunFolderRequested()
    {
        if (folderHandler is { } handler) { handler(); return; }
        LogsPage.OpenLogDirectory();
    }

    private static Control ScaffoldView(PageScaffold scaffold)
    {
        var note = ShellTheming.NoticeStrip(scaffold.Note);
        note.Margin = new Thickness(0, 8, 0, 0);
        return new StackPanel
        {
            Spacing = ShellTheming.CardGap,
            MaxWidth = ShellTheming.PageMaxWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                ShellTheming.PageHeader(scaffold.Title, scaffold.Description),
                ShellTheming.Card(note)
            }
        };
    }

    private void RenderStatus(StatusReading reading)
    {
        statusText.Text = reading.StripText;
        // 状态点只表达可信度：可信=绿、过期=琥珀、缺失或损坏=中性；文字仍是唯一完整信息。
        var tone = reading.Snapshot is null || !reading.Valid ? "ToneNeutral"
            : reading.Fresh ? "ToneGood" : "ToneWarning";
        statusDot.Bind(Shape.FillProperty, statusDot.GetResourceObservable(tone));
    }
}
