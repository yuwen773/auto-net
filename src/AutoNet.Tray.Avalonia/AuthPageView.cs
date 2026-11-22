using AutoNet.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 认证页视觉构建（工单 #37）：状态卡、固定门户地址、浏览器宿主区域与受控说明。
///
/// 视图只渲染传入的 <see cref="AuthPageContent"/>；不读取配置、不访问网络、不提供填表控件。
/// 手动认证修复只发起“经确认后启动辅助进程”的意图，表单与提交都由辅助进程完成。
/// 宿主表面由协调器注入，状态刷新只更新文字与宿主表面，**不重建页面**，因此已填内容不会被清空。
///
/// 布局（2026-11-22）：浏览器宿主占满页面的剩余高度，门户页面按自己的视口排版。
/// 但原生表面只能放在确定高度的容器里，所以只有当视口放得下“固定内容 + 最小宿主高度”时，
/// 页面才停止滚动并挂载浏览器；放不下时退回滚动并显示占位，窗口放大后自动恢复——
/// 见 <see cref="UpdateViewport"/>。滚动状态下不挂原生表面，避免窗口跟随滚动的未验证路径。
/// 视觉构件统一取自 <see cref="ShellTheming"/>；为给门户页留出高度，状态卡压缩成
/// “图标 + 结论 + 一行说明”，门户地址与宿主标题同行。但 <see cref="AuthPageContent.BackgroundNote"/>
/// 必须保留：ADR-0017 要求页面明确“不代表发生过网页操作”，它是生产界面里唯一的载体。
/// </summary>
internal sealed class AuthPageView
{
    /// <summary>浏览器宿主的最小高度：低于它时门户页只剩很窄的视口，不再值得挂载。</summary>
    internal const double MinimumHostHeight = 180;

    /// <summary>放不下时压缩到的高度；与“宿主不可用”的降级形态一致。</summary>
    private const double FallbackHostHeight = 140;

    private const double RowSpacing = 10;

    private readonly Func<object?> surfaceProvider;
    private readonly Grid root;
    private readonly Control headerPanel;
    private readonly ContentControl glyphHost = new();
    private readonly TextBlock statusTitle = new() { FontSize = 21, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock statusLine = new()
    {
        TextWrapping = TextWrapping.Wrap, LineHeight = 21, FontSize = 13.5
    };
    private readonly TextBlock background = ShellTheming.Muted("", 12, 19);
    private readonly TextBlock address = new()
    {
        FontSize = 13, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right
    };
    private readonly TextBlock hostTitle = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock hostDetail = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 20, FontSize = 12.5 };
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
    private readonly Control hostCard;
    private readonly Button retry;
    private readonly Control statusCard;
    private readonly Expander channelCard;
    private readonly TextBlock channelState = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 19, FontSize = 12, Opacity = 0.85 };
    private readonly Button channelButton;
    private readonly TextBlock channelNotice = new()
    {
        TextWrapping = TextWrapping.Wrap, LineHeight = 19, FontSize = 12, IsVisible = false
    };

    private AuthPageContent? content;
    private object? slotItemShown;
    private bool hostFits = true;

    public AuthPageView(Action retryRequested, Func<object?> surfaceProvider, Action? channelLaunch = null)
    {
        this.surfaceProvider = surfaceProvider;

        // Grid + 星号行：宿主卡吃掉剩余高度，门户页因此得到确定的视口尺寸。
        root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = RowSpacing,
            MaxWidth = 1320,
            // 拉满可用宽度：门户页需要横向空间，Left 对齐会让整页只按文字宽度收缩。
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        headerPanel = ShellTheming.PageHeader(
            ShellPages.Info(ShellPage.Authentication).Name,
            ShellPages.Info(ShellPage.Authentication).Description);
        Grid.SetRow(headerPanel, 0);
        root.Children.Add(headerPanel);

        // 状态卡压缩成“图标 + 结论 + 一行说明”：每省下的一行都直接变成门户页的可用高度。
        // 免责说明已由页面顶部的描述承担，不在这里重复。
        var statusRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        glyphHost.VerticalAlignment = VerticalAlignment.Top;
        glyphHost.Width = 42;
        glyphHost.Height = 42;
        var statusText = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(14, 0, 0, 0),
            Children = { statusTitle, statusLine }
        };
        Grid.SetColumn(glyphHost, 0);
        Grid.SetColumn(statusText, 1);
        statusRow.Children.Add(glyphHost);
        statusRow.Children.Add(statusText);
        AutomationProperties.SetName(statusRow, ShellAccessibility.AuthStatus);
        statusCard = ShellTheming.Card(new StackPanel
        {
            Spacing = 12,
            Children = { statusRow, ShellTheming.Hairline(), background }
        });
        Grid.SetRow(statusCard, 1);
        root.Children.Add(statusCard);

        // 宿主卡内部同样是 Grid：标题行（含门户地址）与说明固定，承载框吃掉卡片内的剩余高度。
        var hostHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        hostHeader.Children.Add(hostTitle);
        AutomationProperties.SetName(address, ShellAccessibility.AuthAddress);
        Grid.SetColumn(address, 1);
        hostHeader.Children.Add(address);
        var hostBody = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 8,
            Children = { hostHeader, hostDetail }
        };
        Grid.SetRow(hostHeader, 0);
        Grid.SetRow(hostDetail, 1);
        hostSlot = new Border
        {
            MinHeight = MinimumHostHeight,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = hostPlaceholder
        };
        hostSlot.Bind(Border.BorderBrushProperty, hostSlot.GetResourceObservable("CardBorder"));
        ShellTheming.Themed(hostSlot, Border.BackgroundProperty, "SurfaceSunken");
        AutomationProperties.SetName(hostSlot, ShellAccessibility.AuthHost);
        Grid.SetRow(hostSlot, 2);
        hostBody.Children.Add(hostSlot);
        retry = ShellAccessibility.Named(ShellTheming.SecondaryButton(ShellAccessibility.AuthRetry),
            ShellAccessibility.AuthRetry);
        retry.MinWidth = 170;
        retry.IsVisible = false;
        retry.Click += (_, _) => retryRequested();
        Grid.SetRow(retry, 3);
        hostBody.Children.Add(retry);
        hostCard = ShellTheming.Card(hostBody);
        Grid.SetRow(hostCard, 2);
        root.Children.Add(hostCard);

        // 工单 #24 / ADR-0009：会提权并可能提交凭据的手动入口从只读诊断页移到这里，
        // 与“本页不填表、不提交”的说明分开呈现，并在展开后明确警示。
        // 折叠成 Expander 是为了不抢占浏览器的空间：只有用户主动展开时才让出宿主高度。
        var channelBody = new StackPanel { Spacing = 10 };
        channelBody.Children.Add(new TextBlock
        {
            Text = "仅在协议认证反复失败、门户结构变化时使用。点击后会请求管理员授权（UAC），"
                + "以辅助进程打开固定门户页，并可能自动填写、提交已保存的校园网凭据。",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 12
        });
        channelBody.Children.Add(channelState);
        channelBody.Children.Add(channelNotice);
        channelButton = ShellAccessibility.Named(ShellTheming.SecondaryButton("发起手动认证修复…"),
            "手动认证修复：会请求管理员授权并可能提交已保存的凭据");
        channelButton.MinWidth = 180;
        channelButton.IsEnabled = false;
        channelButton.IsVisible = false;
        if (channelLaunch is not null) channelButton.Click += (_, _) => channelLaunch();
        channelBody.Children.Add(channelButton);
        channelCard = new Expander
        {
            Header = new TextBlock { Text = "手动认证修复（会提交凭据）", FontSize = 15, FontWeight = FontWeight.SemiBold },
            Content = channelBody,
            IsExpanded = false,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetRow(channelCard, 3);
        root.Children.Add(channelCard);
    }

    public Control Root => root;

    /// <summary>
    /// 手动入口的受控结果提示。发起动作返回“不可信/关闭/忙/UAC 取消”时，用户必须看到
    /// 明确反馈，而不是按钮点击后毫无动静；传 null 清除提示。
    /// </summary>
    internal void SetChannelNotice(string? text)
    {
        channelNotice.Text = text ?? "";
        channelNotice.IsVisible = !string.IsNullOrEmpty(text);
        if (!string.IsNullOrEmpty(text)) channelCard.IsExpanded = true;
    }

    /// <summary>放得下浏览器所需的页面高度：固定行实际高度 + 行间距 + 宿主卡自身开销 + 最小宿主高度。</summary>
    internal double RequiredHeight
    {
        get
        {
            // 宿主卡的标题、说明与内边距也占高度，计算时必须一起扣除，否则浏览器会被挤到最小值以下。
            var hostChrome = Math.Max(0, hostCard.Bounds.Height - hostSlot.Bounds.Height);
            return MinimumHostHeight + hostChrome + RowSpacing * 3
                + headerPanel.Bounds.Height + statusCard.Bounds.Height + channelCard.Bounds.Height;
        }
    }

    /// <summary>
    /// 视口是否放得下浏览器（容差半个像素，避免布局取整造成来回切换）。
    /// 纯函数，离线回归直接钉住默认窗口与最小窗口两侧。
    /// </summary>
    internal static bool HostFitsViewport(double availableHeight, double requiredHeight) =>
        availableHeight > 0 && availableHeight + 0.5 >= requiredHeight;

    /// <summary>
    /// 用滚动容器的可用高度决定是否挂载浏览器：放得下就停止滚动让宿主吃满剩余高度，
    /// 放不下就退回滚动并显示占位（占位状态下没有原生表面，滚动不会带来窗口错位）。
    /// 返回当前是否放得下。
    /// </summary>
    public bool UpdateViewport(double availableHeight)
    {
        if (availableHeight <= 0) return hostFits;
        var fits = HostFitsViewport(availableHeight, RequiredHeight);
        if (fits == hostFits) return fits;
        hostFits = fits;
        UpdateHostSlot();
        return fits;
    }

    /// <summary>用新的状态投影更新可见文字与宿主表面；不导航、不重建控件树。</summary>
    public void Render(AuthPageContent content)
    {
        this.content = content;
        glyphHost.Content = ShellTheming.IconBadge(ShellIcons.ToneGlyph(content.Tone), content.Tone, 42);
        statusTitle.Text = content.StatusTitle;
        // 结论与下一步合并成一行：默认窗口高度有限，这一行是门户页能多出来的高度。
        statusLine.Text = string.IsNullOrWhiteSpace(content.NextStep)
            ? content.StatusExplanation
            : content.StatusExplanation + " " + content.NextStep;
        background.Text = content.BackgroundNote;
        address.Text = content.PortalAddress;
        hostTitle.Text = content.HostTitle;
        hostDetail.Text = content.HostDetail;
        retry.IsVisible = content.HostRetryVisible;

        // 手动认证修复入口只在装配了发起器时出现；忙态与模式约束与诊断页原实现一致。
        channelCard.IsVisible = content.Channel is not null;
        channelButton.IsVisible = content.Channel is not null;
        if (content.Channel is { } channel)
        {
            channelState.Text = $"模式：{AuthPageModel.ChannelModeLabel(channel.Mode)}｜门户结构信号：{(channel.SignalPresent ? "存在" : "无")}"
                + (channel.LastConclusion is { } conclusion ? $"｜最近结论：{AuthPageModel.ChannelConclusionLabel(conclusion)}" : "")
                + (channel.Busy ? "｜通道会话进行中" : "");
            channelButton.IsEnabled = channel.SignalPresent && channel.Mode != AuthChannelModes.Off && !channel.Busy;
        }

        UpdateHostSlot();
    }

    /// <summary>
    /// 宿主槽位的内容与高度：只有“宿主可用且视口放得下”时才挂原生表面。
    /// 受控降级或窗口过小时显示占位并压缩高度，保证重试入口留在首屏。
    /// </summary>
    private void UpdateHostSlot()
    {
        var available = content?.HostAvailable == true;
        var showSurface = available && hostFits;
        var item = showSurface ? surfaceProvider() : null;
        hostPlaceholder.Text = available && !hostFits
            ? "窗口太小，放大后可查看门户页面。"
            : content is null ? "" : AuthPageModel.HostCopyFor(content.HostStatus).Placeholder;
        hostSlot.MinHeight = showSurface ? MinimumHostHeight : FallbackHostHeight;
        if (ReferenceEquals(item, slotItemShown) && hostSlot.Child is not null) return;
        slotItemShown = item;
        hostSlot.Child = item as Control ?? hostPlaceholder;
    }
}

/// <summary>
/// 认证页渲染门（#37 验收条件 4）：决定一次内容投影是否需要重新渲染，并始终复用同一个视图实例。
/// 纯逻辑、不依赖 Avalonia，因此可以用假视图直接回归“相同内容不重建、切页返回不丢输入”。
/// </summary>
internal sealed class AuthPageRenderGate<TView> where TView : class
{
    private TView? view;
    private string? renderedKey;

    /// <summary>指纹未变化时返回 false，调用方跳过本次渲染；内容变化时才返回 true。</summary>
    public bool ShouldRender(AuthPageContent content)
    {
        if (renderedKey == content.ContentKey) return false;
        renderedKey = content.ContentKey;
        return true;
    }

    /// <summary>视图实例只在首次创建，之后的状态刷新与切页都复用同一实例。</summary>
    public TView View(Func<TView> create) => view ??= create();

    /// <summary>切到其他页面：只清指纹缓存、保留视图实例；返回认证页时会重新渲染同一实例。</summary>
    public void Detach() => renderedKey = null;
}
