using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 壳内设计系统的单一来源：间距刻度、字号阶、卡片/分隔/胶囊/图标徽章等共享构件。
/// 所有颜色经主题资源键绑定，明暗切换无需重建控件树；语义色键按 <see cref="OverviewTone"/> 分类，
/// 文字、图标与底色并行表达，任何状态都不只依赖颜色。
/// </summary>
internal static class ShellTheming
{
    /// <summary>页面内容最大宽度：设置页、日志页与兜底页共用同一阅读栏宽。</summary>
    public const double PageMaxWidth = 840;

    /// <summary>宽屏页面（概览、诊断）的最大宽度：窗口足够宽时把卡片排成两栏。</summary>
    public const double WidePageMaxWidth = 1180;

    /// <summary>页面开始随窗口加宽的阈值（页面可用宽度，而非窗口宽度）。</summary>
    public const double WideGrowthThreshold = 900;

    /// <summary>
    /// 两栏切换阈值：两栏各自仍能保持“标签—值”同行（每栏 ≥ 约 560px）时才分栏，
    /// 更窄的窗口维持单列加宽，避免卡片被挤成窄条。
    /// </summary>
    public const double TwoColumnThreshold = 1160;

    /// <summary>卡片之间的竖向间距。</summary>
    public const double CardGap = 18;

    /// <summary>卡片内部的区块间距。</summary>
    public const double SectionGap = 14;

    /// <summary>标签—值行的标签列宽。</summary>
    public const double LabelWidth = 148;

    /// <summary>卡片内边距。</summary>
    public static readonly Thickness CardPadding = new(22, 20);

    public static T Themed<T>(T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }

    /// <summary>
    /// 标准卡片：12px 圆角、1px 边框、轻阴影（暗色主题由主题资源置零）。
    /// 采用横向留白大于纵向的排版比例，让文字行与卡片边缘之间呼吸一致。
    /// </summary>
    public static Control Card(Control content)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = CardPadding,
            BorderThickness = new Thickness(1),
            Child = content
        };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("CardBackground"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("CardBorder"));
        border.Bind(Border.BoxShadowProperty, border.GetResourceObservable("CardShadow"));
        return border;
    }

    /// <summary>页面标题区：28px 主标题 + 承载页面说明的次要文字，底部与首张卡片留出更大间距。</summary>
    public static Control PageHeader(string title, string description)
    {
        var heading = Themed(
            new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeight.SemiBold },
            TextBlock.ForegroundProperty, "TextPrimary");
        // 说明限宽只为控制行宽；必须显式左对齐，否则 Stretch + MaxWidth 会被居中到页面中线。
        var note = Muted(description, 13, 21, maxWidth: 620);
        note.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 4),
            Children = { heading, note }
        };
    }

    /// <summary>卡片内区块标题：比正文大一档、半粗，成为卡片内的第一层视觉锚点。</summary>
    public static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 0, 0, 2)
    };

    /// <summary>次要文字：用于标签、说明与脚注；字号与行高成对给出，避免稀疏的孤立小字。</summary>
    public static TextBlock Muted(string text, double size, double? lineHeight = null, double maxWidth = double.PositiveInfinity)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            LineHeight = lineHeight ?? size + 7,
            TextWrapping = TextWrapping.Wrap
        };
        if (!double.IsPositiveInfinity(maxWidth)) block.MaxWidth = maxWidth;
        return Themed(block, TextBlock.ForegroundProperty, "TextMuted");
    }

    /// <summary>正文：13px、21px 行高，适合中文的阅读密度。</summary>
    public static TextBlock Body(string text, double size = 13)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            LineHeight = size + 8,
            TextWrapping = TextWrapping.Wrap
        };
    }

    /// <summary>标签：12.5px 次要色，用于标签—值行的左侧与微型表头。</summary>
    public static TextBlock Label(string text, double size = 12.5) => Themed(
        new TextBlock
        {
            Text = text,
            FontSize = size,
            LineHeight = size + 6,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        },
        TextBlock.ForegroundProperty, "TextMuted");

    /// <summary>1px 分隔线：卡片内行与行之间的低对比度节奏线。</summary>
    public static Control Hairline(double top = 0, double bottom = 0)
    {
        var line = new Border
        {
            Height = 1,
            Margin = new Thickness(0, top, 0, bottom),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        return Themed(line, Border.BackgroundProperty, "RowSeparator");
    }

    /// <summary>语义胶囊：浅色语义底 + 语义前景，用于状态小标签。</summary>
    public static Control Pill(string text, OverviewTone tone)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Themed(
                new TextBlock { Text = text, FontSize = 11.5, LineHeight = 16 },
                TextBlock.ForegroundProperty, ToneKey(tone))
        };
        return Themed(border, Border.BackgroundProperty, ToneSurfaceKey(tone));
    }

    /// <summary>
    /// 描边步骤胶囊：用于已着色横幅内部的“下一步”，以语义色描边保证在浅底上仍可辨认，
    /// 不用同色底造成胶囊与横幅融为一体。
    /// </summary>
    public static Control StepPill(string text, OverviewTone tone)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 3, 10, 3),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Themed(
                new TextBlock { Text = text, FontSize = 11.5, LineHeight = 16 },
                TextBlock.ForegroundProperty, ToneKey(tone))
        };
        return Themed(border, Border.BorderBrushProperty, ToneKey(tone));
    }

    /// <summary>
    /// 图标徽章：圆形语义浅底承载线性字形，给状态一个可扫读的视觉锚点。
    /// 字形本身仍是装饰，状态文字始终同时出现。
    /// </summary>
    public static Control IconBadge(string glyphData, OverviewTone tone, double size)
    {
        var glyph = new ShapePath
        {
            Data = Geometry.Parse(glyphData),
            Width = size * 0.52,
            Height = size * 0.52,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Themed(glyph, Shape.StrokeProperty, ToneKey(tone));
        var badge = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Child = glyph
        };
        return Themed(badge, Border.BackgroundProperty, ToneSurfaceKey(tone));
    }

    /// <summary>主按钮：深墨填充（暗色主题为浅色填充），用于每页唯一的主要动作。</summary>
    public static Button PrimaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 108
        };
        button.Classes.Add("primary");
        return button;
    }

    /// <summary>次按钮：描边样式，用于可逆或需要再次确认的动作。</summary>
    public static Button SecondaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Classes.Add("secondary");
        return button;
    }

    /// <summary>左侧强调条的说明区：用于页脚只读声明与凭据说明。</summary>
    public static Control NoticeStrip(string text, OverviewTone tone = OverviewTone.Neutral)
    {
        var bar = Themed(new Border { Width = 3, CornerRadius = new CornerRadius(2) },
            Border.BackgroundProperty, ToneKey(tone));
        var body = new TextBlock
        {
            Text = text,
            FontSize = 12,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Themed(body, TextBlock.ForegroundProperty, "TextMuted");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3,*") };
        bar.Margin = new Thickness(0, 1, 14, 1);
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(bar);
        grid.Children.Add(body);
        return grid;
    }

    public static Control Glyph(string data, OverviewTone tone, double size, double strokeThickness = 1.6) => Themed(
        new ShapePath
        {
            Data = Geometry.Parse(data),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            StrokeThickness = strokeThickness,
            VerticalAlignment = VerticalAlignment.Center
        },
        Shape.StrokeProperty, ToneKey(tone));

    public static string ToneKey(OverviewTone tone) => "Tone" + tone;

    public static string ToneSurfaceKey(OverviewTone tone) => "Tone" + tone + "Surface";
}
