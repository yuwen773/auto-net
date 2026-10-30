using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 认证页视觉构建（工单 #37）：状态卡、固定门户地址、浏览器宿主区域与受控说明。
///
/// 视图只渲染传入的 <see cref="AuthPageContent"/>；不读取配置、不访问网络、不提供填表或提交控件。
/// 宿主表面由协调器注入，状态刷新只更新文字与宿主表面，**不重建页面**，因此已填内容不会被清空。
/// </summary>
internal sealed class AuthPageView
{
    private readonly Func<object?> surfaceProvider;
    private readonly Action retryRequested;
    private readonly StackPanel root;
    private readonly ContentControl glyphHost = new();
    private readonly TextBlock statusTitle = new() { FontSize = 22, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock statusExplanation = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 21, FontSize = 14 };
    private readonly TextBlock nextStep = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 21, FontSize = 13, Opacity = 0.85 };
    private readonly TextBlock address = new() { FontSize = 14, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock background = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 20, FontSize = 12, Opacity = 0.8 };
    private readonly TextBlock hostTitle = new() { FontSize = 15, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock hostDetail = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 20, FontSize = 13, Opacity = 0.85 };
    private readonly TextBlock hostPlaceholder = new()
    {
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 21,
        FontSize = 13,
        Opacity = 0.65,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center
    };
    private readonly Border hostSlot;
    private readonly Button retry;
    private object? surfaceShown;

    public AuthPageView(Action retryRequested, Func<object?> surfaceProvider)
    {
        this.retryRequested = retryRequested;
        this.surfaceProvider = surfaceProvider;

        root = new StackPanel { Spacing = 14, MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = ShellPages.Info(ShellPage.Authentication).Name, FontSize = 26, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = ShellPages.Info(ShellPage.Authentication).Description,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                }
            }
        });

        // 用 Grid 给状态文字一个受约束的宽度；横向 StackPanel 会以无限宽度测量子项，
        // 长文案（例如结果未知的额度/间隔说明）因此不会换行，只会被卡片裁切。
        var statusRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        glyphHost.VerticalAlignment = VerticalAlignment.Top;
        glyphHost.Width = 34;
        glyphHost.Height = 34;
        var statusText = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(14, 0, 0, 0),
            Children = { statusTitle, statusExplanation, nextStep }
        };
        Grid.SetColumn(glyphHost, 0);
        Grid.SetColumn(statusText, 1);
        statusRow.Children.Add(glyphHost);
        statusRow.Children.Add(statusText);
        var statusCard = new StackPanel { Spacing = 10, Children = { statusRow, background } };
        AutomationProperties.SetName(statusCard, ShellAccessibility.AuthStatus);
        root.Children.Add(ShellTheming.Card(statusCard));

        var addressRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        addressRow.Children.Add(new TextBlock
        {
            Text = "门户地址",
            FontSize = 13,
            Width = 148,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6
        });
        addressRow.Children.Add(address);
        AutomationProperties.SetName(address, ShellAccessibility.AuthAddress);
        root.Children.Add(ShellTheming.Card(addressRow));

        var host = new StackPanel { Spacing = 8, Children = { hostTitle, hostDetail } };
        hostSlot = new Border
        {
            MinHeight = 360,
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = hostPlaceholder
        };
        hostSlot.Bind(Border.BorderBrushProperty, hostSlot.GetResourceObservable("CardBorder"));
        AutomationProperties.SetName(hostSlot, ShellAccessibility.AuthHost);
        host.Children.Add(hostSlot);
        retry = ShellAccessibility.Named(new Button
        {
            Content = ShellAccessibility.AuthRetry,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 170,
            IsVisible = false
        }, ShellAccessibility.AuthRetry);
        retry.Click += (_, _) => retryRequested();
        host.Children.Add(retry);
        root.Children.Add(ShellTheming.Card(host));
    }

    public Control Root => root;

    /// <summary>用新的状态投影更新可见文字与宿主表面；不导航、不重建控件树。</summary>
    public void Render(AuthPageContent content)
    {
        glyphHost.Content = ShellTheming.Glyph(ShellIcons.ToneGlyph(content.Tone), content.Tone, 30);
        statusTitle.Text = content.StatusTitle;
        statusExplanation.Text = content.StatusExplanation;
        nextStep.Text = content.NextStep;
        address.Text = content.PortalAddress;
        background.Text = content.BackgroundNote;
        hostTitle.Text = content.HostTitle;
        hostDetail.Text = content.HostDetail;
        retry.IsVisible = content.HostRetryVisible;
        // 受控降级时不显示旧宿主表面，并压缩宿主区高度，保证重试入口留在首屏。
        hostSlot.MinHeight = content.HostAvailable ? 360 : 140;
        hostPlaceholder.Text = content.HostStatus switch
        {
            AuthPageHostStatus.Attached => "浏览器宿主已附着，但没有可显示的表面。",
            AuthPageHostStatus.RuntimeMissing => "浏览器运行时缺失，无法显示门户页面。",
            AuthPageHostStatus.NetworkBoundaryUnavailable => "受控网络边界未接入，门户页面不会加载。",
            AuthPageHostStatus.Failed => "门户页面加载失败，可点击下方按钮重试。",
            _ => "打开本页后在此显示门户页面；本页不会填表或提交凭据。"
        };

        var surface = content.HostAvailable ? surfaceProvider() : null;
        if (ReferenceEquals(surface, surfaceShown)) return;
        surfaceShown = surface;
        hostSlot.Child = surface as Control ?? hostPlaceholder;
    }
}
