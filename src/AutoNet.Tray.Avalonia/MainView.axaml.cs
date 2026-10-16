using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 壳视图：左侧四页导航、页面宿主与底部状态栏。只负责把 <see cref="ShellController"/>
/// 的选择、内容与状态渲染出来；导航与读取语义都在控制器一侧，动作语义在
/// <see cref="OverviewActions"/> 一侧，便于受控测试。
/// </summary>
public partial class MainView : UserControl
{
    private ShellController? controller;
    private OverviewActions? actions;
    private LogsPage? logsPage;
    private Func<string, Task>? copyHandler;
    private Action? folderHandler;
    private OverviewContent? overviewShown;
    private DiagnosticsContent? diagnosticsShown;
    private LogsContent? logsShown;
    private bool technicalExpanded;
    private bool syncingSelection;

    public MainView()
    {
        InitializeComponent();
        nav.SelectionChanged += (_, _) =>
        {
            if (syncingSelection) return;
            if (nav.SelectedItem is ListBoxItem { Tag: ShellPage page } && page != controller?.SelectedPage)
                NavigateSafelyAsync(page);
        };
    }

    /// <summary>接入壳控制器、概览动作与日志页（#7）并建立渲染通路。应用内只会接入一次。</summary>
    internal void Attach(
        ShellController shell,
        OverviewActions? overviewActions = null,
        LogsPage? logs = null,
        Func<string, Task>? copy = null,
        Action? openFolder = null)
    {
        controller = shell;
        actions = overviewActions;
        logsPage = logs;
        copyHandler = copy;
        folderHandler = openFolder;
        BuildNavigation();
        shell.SelectionChanged += page => Dispatcher.UIThread.Post(() => SyncSelection(page));
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
        foreach (var info in ShellPages.All)
        {
            var icon = new ShapePath
            {
                Data = Geometry.Parse(ShellIcons.For(info.Page)),
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.4,
                VerticalAlignment = VerticalAlignment.Center
            };
            // 图标跟随条目前景色（含选中态）。
            icon.Bind(Shape.StrokeProperty, new Binding(nameof(TextBlock.Foreground))
            {
                RelativeSource = new RelativeSource { Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(ListBoxItem) }
            });

            var label = new TextBlock { Text = info.Name, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { icon, label }
            };
            nav.Items.Add(new ListBoxItem { Tag = info.Page, Content = content, Padding = new Thickness(12, 9) });
        }
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
                    () => RunActionSafelyAsync(() => actions is null ? Task.FromResult(false) : actions.RequestUnblockAsync()));
                break;
            case DiagnosticsContent diagnostics:
                // 指纹未变化时不重建：诊断页无动作、无展开状态，但避免轮询期的无谓控件树重建。
                if (diagnosticsShown is { } shownDiagnostics && shownDiagnostics.ContentKey == diagnostics.ContentKey) return;
                diagnosticsShown = diagnostics;
                ClearShown(except: diagnostics);
                pageHost.Content = DiagnosticsView.Build(diagnostics);
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
            case PageScaffold scaffold:
                ClearShown(except: null);
                pageHost.Content = ScaffoldView(scaffold);
                break;
        }
    }

    /// <summary>只保留当前页面的内容缓存：其余页面的指纹缓存一律清空，避免跨页误判跳过。</summary>
    private void ClearShown(PageContent? except)
    {
        if (!ReferenceEquals(except, overviewShown)) overviewShown = null;
        if (!ReferenceEquals(except, diagnosticsShown)) diagnosticsShown = null;
        if (!ReferenceEquals(except, logsShown)) logsShown = null;
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
        var title = new TextBlock { Text = scaffold.Title, FontSize = 26, FontWeight = FontWeight.SemiBold };
        var description = new TextBlock
        {
            Text = scaffold.Description,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            FontSize = 14
        };
        var note = new TextBlock
        {
            Text = scaffold.Note,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.55,
            FontSize = 12,
            Margin = new Thickness(0, 12, 0, 0)
        };
        return new StackPanel
        {
            Spacing = 10,
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { title, description, note }
        };
    }

    private void RenderStatus(StatusReading reading) => statusText.Text = reading.StripText;
}
