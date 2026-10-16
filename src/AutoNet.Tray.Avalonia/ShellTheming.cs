using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 页面视图共享的 Fluent 卡片小工具（#4 概览与 #6 诊断开始共用）：资源颜色经 observable
/// 绑定，明暗主题切换时无需重建控件树；语义色键与字形按 <see cref="OverviewTone"/> 分类。
/// </summary>
internal static class ShellTheming
{
    public static T Themed<T>(T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }

    public static Control Card(Control content)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 16),
            BorderThickness = new Thickness(1),
            Child = content
        };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("CardBackground"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("CardBorder"));
        return border;
    }

    public static Control Glyph(string data, OverviewTone tone, double size) => Themed(
        new ShapePath
        {
            Data = Geometry.Parse(data),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            VerticalAlignment = VerticalAlignment.Center
        },
        Shape.StrokeProperty, ToneKey(tone));

    public static string ToneKey(OverviewTone tone) => "Tone" + tone;
}
