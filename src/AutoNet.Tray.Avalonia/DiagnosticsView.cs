using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 网络诊断页视觉构建（迁移工单 #6）：把 <see cref="DiagnosticsContent"/> 渲染为 Fluent 卡片式界面。
/// 五类诊断、认证信号与受控认证分类各成卡片；每行文字、图标与语义色同时呈现；
/// 只读边界声明固定在页脚。本类只负责构建；页面没有动作，交互面为零（故事 23）。
/// </summary>
internal static class DiagnosticsView
{
    public static Control Build(DiagnosticsContent content)
    {
        var page = new StackPanel { Spacing = 14, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = content.Title, FontSize = 26, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = ShellPages.Info(content.Page).Description,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                }
            }
        });
        page.Children.Add(RowCard("五类诊断", content.Categories));
        page.Children.Add(RowCard("认证信号", content.Signals));
        page.Children.Add(RowCard("认证证据", content.Attempts));
        page.Children.Add(ShellTheming.Themed(
            new TextBlock
            {
                Text = DiagnosticsModel.ReadOnlyNotice,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 18,
                Margin = new Thickness(2, 0, 0, 0)
            },
            TextBlock.ForegroundProperty, "TextMuted"));
        return page;
    }

    /// <summary>一个证据分区：标题 + 若干「类别 / 值 + 边界说明」行；不可信状态同样保持分列结构。</summary>
    private static Control RowCard(string header, IReadOnlyList<DiagnosticsRow> rows)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = header, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        foreach (var row in rows)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
            var caption = ShellTheming.Themed(
                new TextBlock
                {
                    Text = row.Caption,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top
                },
                TextBlock.ForegroundProperty, "TextMuted");
            Grid.SetColumn(caption, 0);
            var value = new StackPanel { Spacing = 2 };
            value.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    ShellTheming.Glyph(ShellIcons.ToneGlyph(row.Tone), row.Tone, 15),
                    ShellTheming.Themed(
                        new TextBlock
                        {
                            Text = row.Value,
                            FontSize = 13,
                            TextWrapping = TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        TextBlock.ForegroundProperty, ShellTheming.ToneKey(row.Tone))
                }
            });
            if (row.Note.Length > 0) value.Children.Add(ShellTheming.Themed(
                new TextBlock
                {
                    Text = row.Note,
                    FontSize = 11.5,
                    LineHeight = 17,
                    TextWrapping = TextWrapping.Wrap
                },
                TextBlock.ForegroundProperty, "TextMuted"));
            Grid.SetColumn(value, 1);
            line.Children.Add(caption);
            line.Children.Add(value);
            body.Children.Add(line);
        }

        return ShellTheming.Card(body);
    }
}
