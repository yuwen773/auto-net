using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 概览页视觉构建（迁移工单 #4）：把 <see cref="OverviewContent"/> 渲染为 Fluent 卡片式界面。
/// 状态、进度与下一步各成区块；动作出现在对应状态附近（解除入口在认证列、恢复开关在进度区）。
/// 文字、图标与语义色同时呈现；本类只负责构建，交互语义在 <see cref="OverviewActions"/> 一侧。
/// </summary>
internal static class OverviewView
{
    public static Control Build(
        OverviewContent content,
        bool technicalExpanded,
        Action<bool> technicalExpandedChanged,
        Action<RecoveryIntent> recoveryRequested,
        Action unblockRequested)
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
        page.Children.Add(StatusCard(content, technicalExpanded, technicalExpandedChanged, unblockRequested));
        page.Children.Add(ProgressCard(content, recoveryRequested));
        return page;
    }

    /// <summary>状态卡：主状态与认证事实分列（在线与认证暂停并存），下一步紧跟状态，技术详情按需展开。</summary>
    private static Control StatusCard(
        OverviewContent content,
        bool technicalExpanded,
        Action<bool> technicalExpandedChanged,
        Action unblockRequested)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(SectionHeader("当前网络状态"));

        if (content.Auth.Visible)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
            var network = NetworkColumn(content);
            var divider = ShellTheming.Themed(new Border { Width = 1, Margin = new Thickness(18, 2, 18, 2) }, Border.BackgroundProperty, "ShellBorder");
            var auth = AuthColumn(content, unblockRequested);
            Grid.SetColumn(network, 0);
            Grid.SetColumn(divider, 1);
            Grid.SetColumn(auth, 2);
            row.Children.Add(network);
            row.Children.Add(divider);
            row.Children.Add(auth);
            body.Children.Add(row);
        }
        else
        {
            body.Children.Add(NetworkColumn(content));
        }

        if (content.HasTechnical)
        {
            var expander = new Expander
            {
                Header = "技术详情",
                IsExpanded = technicalExpanded,
                Content = new TextBlock
                {
                    Text = content.TechnicalDetail,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                    Margin = new Thickness(0, 2, 0, 2)
                }
            };
            // 重建时保留展开状态；状态刷新不会把已展开的详情折叠回去。
            expander.PropertyChanged += (_, e) =>
            {
                if (e.Property == Expander.IsExpandedProperty) technicalExpandedChanged(expander.IsExpanded);
            };
            body.Children.Add(expander);
        }

        if (content.Chips.Count > 0)
        {
            var chips = new WrapPanel();
            foreach (var chip in content.Chips) chips.Children.Add(ChipView(chip));
            body.Children.Add(chips);
        }

        return ShellTheming.Card(body);
    }

    private static Control NetworkColumn(OverviewContent content)
    {
        var column = new StackPanel { Spacing = 6 };
        column.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                ShellTheming.Glyph(ShellIcons.StatusGlyph(content.Primary), content.Tone, 30),
                ShellTheming.Themed(
                    new TextBlock
                    {
                        Text = content.StatusTitle,
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    TextBlock.ForegroundProperty, ShellTheming.ToneKey(content.Tone))
            }
        });
        column.Children.Add(new TextBlock
        {
            Text = content.StatusExplanation,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20
        });
        column.Children.Add(Muted("下一步：" + content.NextStep, 13));
        return column;
    }

    /// <summary>认证列：认证暂停、门户结构信号或不可确认独立呈现；解除入口紧邻认证事实。</summary>
    private static Control AuthColumn(OverviewContent content, Action unblockRequested)
    {
        var column = new StackPanel { Spacing = 6 };
        column.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                ShellTheming.Glyph(ShellIcons.AuthColumnGlyph(content.Auth.Kind), content.Auth.Tone, 22),
                ShellTheming.Themed(
                    new TextBlock
                    {
                        Text = content.Auth.Title,
                        FontSize = 15,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    TextBlock.ForegroundProperty, ShellTheming.ToneKey(content.Auth.Tone))
            }
        });
        column.Children.Add(new TextBlock
        {
            Text = content.Auth.Note,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 19,
            Opacity = 0.9
        });
        if (content.Unblock.Visible)
        {
            var button = new Button
            {
                Content = content.Unblock.Label,
                IsEnabled = content.Unblock.Enabled,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0)
            };
            button.Click += (_, _) => unblockRequested();
            column.Children.Add(button);
        }

        return column;
    }

    /// <summary>进度卡：回答“AutoNet 正在做什么”；恢复开关就在自动恢复行上（动作靠近对应状态）。</summary>
    private static Control ProgressCard(OverviewContent content, Action<RecoveryIntent> recoveryRequested)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(SectionHeader("AutoNet 正在做什么"));

        var recoveryRow = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,Auto") };
        var caption = Muted("自动恢复", 13);
        Grid.SetColumn(caption, 0);
        var state = new TextBlock
        {
            Text = content.Recovery.State,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(state, 1);
        var button = new Button
        {
            Content = content.Recovery.Label,
            IsEnabled = content.Recovery.Enabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (content.Recovery.Intent is { } intent) button.Click += (_, _) => recoveryRequested(intent);
        Grid.SetColumn(button, 2);
        recoveryRow.Children.Add(caption);
        recoveryRow.Children.Add(state);
        recoveryRow.Children.Add(button);
        body.Children.Add(recoveryRow);

        foreach (var row in content.Progress)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
            var rowCaption = Muted(row.Caption, 13);
            rowCaption.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(rowCaption, 0);
            var value = new StackPanel { Spacing = 2 };
            value.Children.Add(new TextBlock { Text = row.Value, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            if (row.Note.Length > 0) value.Children.Add(Muted(row.Note, 11.5));
            Grid.SetColumn(value, 1);
            line.Children.Add(rowCaption);
            line.Children.Add(value);
            body.Children.Add(line);
        }

        return ShellTheming.Card(body);
    }

    private static TextBlock SectionHeader(string text) =>
        new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) };

    private static TextBlock Muted(string text, double size) => ShellTheming.Themed(
        new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size + 6 },
        TextBlock.ForegroundProperty, "TextMuted");

    private static Control ChipView(OverviewChip chip)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(0, 0, 6, 4),
            Child = ShellTheming.Themed(new TextBlock { Text = chip.Text, FontSize = 11.5 }, TextBlock.ForegroundProperty, ShellTheming.ToneKey(chip.Tone))
        };
        return ShellTheming.Themed(border, Border.BackgroundProperty, ShellTheming.ToneKey(chip.Tone) + "Surface");
    }
}
