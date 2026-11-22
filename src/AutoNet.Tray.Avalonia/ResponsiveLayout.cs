using Avalonia;
using Avalonia.Controls;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 两列/多列自适应面板：可用宽度低于 <see cref="Breakpoint"/> 时改为纵向堆叠。
/// 只决定排布密度，不改变子元素本身；宽度为无穷时按内容宽度横向排列。
/// </summary>
internal sealed class AdaptiveColumns : Panel
{
    public static readonly StyledProperty<double> BreakpointProperty =
        AvaloniaProperty.Register<AdaptiveColumns, double>(nameof(Breakpoint), 560);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<AdaptiveColumns, double>(nameof(Gap), 36);

    static AdaptiveColumns()
    {
        AffectsMeasure<AdaptiveColumns>(BreakpointProperty, GapProperty);
    }

    public double Breakpoint
    {
        get => GetValue(BreakpointProperty);
        set => SetValue(BreakpointProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Children.Count;
        if (count == 0) return default;

        var finite = !double.IsInfinity(availableSize.Width);
        var stacked = finite && availableSize.Width < Breakpoint;
        var columnWidth = stacked ? availableSize.Width
            : finite ? Math.Max(0, (availableSize.Width - Gap * (count - 1)) / count)
            : double.PositiveInfinity;

        double height = 0, width = 0;
        var index = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            height = stacked ? height + child.DesiredSize.Height + (index > 0 ? Gap : 0)
                : Math.Max(height, child.DesiredSize.Height);
            width = stacked ? Math.Max(width, child.DesiredSize.Width)
                : width + child.DesiredSize.Width + (index > 0 ? Gap : 0);
            index++;
        }

        return new Size(finite ? availableSize.Width : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Children.Count;
        if (count == 0) return finalSize;

        var stacked = finalSize.Width < Breakpoint;
        var columnWidth = stacked ? finalSize.Width
            : Math.Max(0, (finalSize.Width - Gap * (count - 1)) / count);

        double y = 0;
        var index = 0;
        foreach (var child in Children)
        {
            if (stacked)
            {
                if (index > 0) y += Gap;
                child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height;
            }
            else
            {
                child.Arrange(new Rect(index * (columnWidth + Gap), 0, columnWidth,
                    Math.Max(child.DesiredSize.Height, finalSize.Height)));
            }
            index++;
        }

        return finalSize;
    }
}

/// <summary>
/// 页面主列：宽度上限随窗口分档——窄屏保持设置式定宽（<see cref="NarrowMaxWidth"/>），
/// 可用宽度达到 <see cref="WideThreshold"/> 后放宽到 <see cref="WideMaxWidth"/>。
/// 子项仍按顺序纵向排列、间距统一；用自定义面板而不是 StackPanel，是因为固定
/// <c>MaxWidth</c> 无法让页面在宽屏下“跟着窗口长大”。
/// </summary>
internal sealed class PageLayout : Panel
{
    public static readonly StyledProperty<double> NarrowMaxWidthProperty =
        AvaloniaProperty.Register<PageLayout, double>(nameof(NarrowMaxWidth), 840);

    public static readonly StyledProperty<double> WideMaxWidthProperty =
        AvaloniaProperty.Register<PageLayout, double>(nameof(WideMaxWidth), 1180);

    public static readonly StyledProperty<double> WideThresholdProperty =
        AvaloniaProperty.Register<PageLayout, double>(nameof(WideThreshold), 900);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<PageLayout, double>(nameof(Gap), 18);

    static PageLayout()
    {
        AffectsMeasure<PageLayout>(NarrowMaxWidthProperty, WideMaxWidthProperty, WideThresholdProperty, GapProperty);
    }

    public double NarrowMaxWidth
    {
        get => GetValue(NarrowMaxWidthProperty);
        set => SetValue(NarrowMaxWidthProperty, value);
    }

    public double WideMaxWidth
    {
        get => GetValue(WideMaxWidthProperty);
        set => SetValue(WideMaxWidthProperty, value);
    }

    public double WideThreshold
    {
        get => GetValue(WideThresholdProperty);
        set => SetValue(WideThresholdProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var finite = !double.IsInfinity(availableSize.Width);
        var cap = finite && availableSize.Width >= WideThreshold ? WideMaxWidth : NarrowMaxWidth;
        var width = finite ? Math.Min(availableSize.Width, cap) : cap;

        double height = 0;
        var index = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            if (index > 0) height += Gap;
            height += child.DesiredSize.Height;
            index++;
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        var index = 0;
        foreach (var child in Children)
        {
            if (index > 0) y += Gap;
            child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
            index++;
        }

        return finalSize;
    }
}

/// <summary>
/// 卡片分栏：宽屏把子列并排（概览“状态 / 进度”、诊断“分类 / 认证与导出”），
/// 窄屏按原顺序纵向堆叠，不改变任何卡片内部布局。只用于左右各一列的两栏场景。
/// </summary>
internal sealed class CardColumns : Panel
{
    public static readonly StyledProperty<double> BreakpointProperty =
        AvaloniaProperty.Register<CardColumns, double>(nameof(Breakpoint), 900);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<CardColumns, double>(nameof(Gap), 24);

    static CardColumns()
    {
        AffectsMeasure<CardColumns>(BreakpointProperty, GapProperty);
    }

    public double Breakpoint
    {
        get => GetValue(BreakpointProperty);
        set => SetValue(BreakpointProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Children.Count;
        if (count == 0) return default;

        var finite = !double.IsInfinity(availableSize.Width);
        var stacked = !finite || availableSize.Width < Breakpoint;
        var columnWidth = stacked ? availableSize.Width
            : Math.Max(0, (availableSize.Width - Gap * (count - 1)) / count);

        double height = 0, width = 0;
        var index = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            height = stacked ? height + child.DesiredSize.Height + (index > 0 ? Gap : 0)
                : Math.Max(height, child.DesiredSize.Height);
            width = stacked ? Math.Max(width, child.DesiredSize.Width)
                : width + child.DesiredSize.Width + (index > 0 ? Gap : 0);
            index++;
        }

        return new Size(finite ? availableSize.Width : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Children.Count;
        if (count == 0) return finalSize;

        var stacked = finalSize.Width < Breakpoint;
        var columnWidth = stacked ? finalSize.Width
            : Math.Max(0, (finalSize.Width - Gap * (count - 1)) / count);

        double y = 0;
        var index = 0;
        foreach (var child in Children)
        {
            if (stacked)
            {
                if (index > 0) y += Gap;
                child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height;
            }
            else
            {
                child.Arrange(new Rect(index * (columnWidth + Gap), 0, columnWidth,
                    Math.Max(child.DesiredSize.Height, finalSize.Height)));
            }
            index++;
        }

        return finalSize;
    }
}

/// <summary>
/// 标签—值成对行：宽时左侧固定 <see cref="LabelWidth"/> 标签列，窄时标签在上、值在下。
/// 用于设置页事实/表单、诊断行与概览进度行，避免窄窗口里标签列挤压正文。
/// </summary>
internal sealed class AdaptiveField : Panel
{
    public static readonly StyledProperty<double> BreakpointProperty =
        AvaloniaProperty.Register<AdaptiveField, double>(nameof(Breakpoint), 520);

    public static readonly StyledProperty<double> LabelWidthProperty =
        AvaloniaProperty.Register<AdaptiveField, double>(nameof(LabelWidth), 148);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<AdaptiveField, double>(nameof(Gap), 12);

    static AdaptiveField()
    {
        AffectsMeasure<AdaptiveField>(BreakpointProperty, LabelWidthProperty, GapProperty);
    }

    public double Breakpoint
    {
        get => GetValue(BreakpointProperty);
        set => SetValue(BreakpointProperty, value);
    }

    public double LabelWidth
    {
        get => GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2)
        {
            foreach (var child in Children) child.Measure(availableSize);
            return availableSize;
        }

        var label = Children[0];
        var value = Children[1];
        var finite = !double.IsInfinity(availableSize.Width);
        var stacked = finite && availableSize.Width < Breakpoint;

        if (stacked)
        {
            label.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            value.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var height = label.DesiredSize.Height + Gap + value.DesiredSize.Height;
            var width = Math.Max(label.DesiredSize.Width, value.DesiredSize.Width);
            return new Size(finite ? availableSize.Width : width, height);
        }

        label.Measure(new Size(LabelWidth, double.PositiveInfinity));
        value.Measure(new Size(Math.Max(0, availableSize.Width - LabelWidth - Gap), double.PositiveInfinity));
        return new Size(finite ? availableSize.Width : LabelWidth + Gap + value.DesiredSize.Width,
            Math.Max(label.DesiredSize.Height, value.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2) return finalSize;

        var label = Children[0];
        var value = Children[1];
        if (finalSize.Width < Breakpoint)
        {
            label.Arrange(new Rect(0, 0, finalSize.Width, label.DesiredSize.Height));
            var y = label.DesiredSize.Height + Gap;
            value.Arrange(new Rect(0, y, finalSize.Width, Math.Max(0, finalSize.Height - y)));
            return finalSize;
        }

        label.Arrange(new Rect(0, 0, LabelWidth, finalSize.Height));
        value.Arrange(new Rect(LabelWidth + Gap, 0, Math.Max(0, finalSize.Width - LabelWidth - Gap),
            finalSize.Height));
        return finalSize;
    }
}
