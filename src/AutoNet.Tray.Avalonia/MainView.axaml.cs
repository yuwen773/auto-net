using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 壳视图：左侧四页导航、页面宿主与底部状态栏。只负责把 <see cref="ShellController"/>
/// 的选择、内容与状态渲染出来；导航与读取语义都在控制器一侧，便于受控测试。
/// </summary>
public partial class MainView : UserControl
{
    private ShellController? controller;
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

    /// <summary>接入壳控制器并建立渲染通路。应用内只会接入一次。</summary>
    internal void Attach(ShellController shell)
    {
        controller = shell;
        BuildNavigation();
        shell.SelectionChanged += page => Dispatcher.UIThread.Post(() => SyncSelection(page));
        shell.ContentChanged += scaffold => Dispatcher.UIThread.Post(() => RenderContent(scaffold));
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

    private void RenderContent(PageScaffold scaffold)
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
        pageHost.Content = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { title, description, note }
        };
    }

    private void RenderStatus(StatusReading reading) => statusText.Text = reading.StripText;
}
