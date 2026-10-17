using AutoNet.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 网络诊断页视觉构建（迁移工单 #6）：把 <see cref="DiagnosticsContent"/> 渲染为 Fluent 卡片式界面。
/// 五类诊断、认证信号与受控认证分类各成卡片；每行文字、图标与语义色同时呈现；
/// 只读边界声明固定在页脚。诊断包导出卡（#12）独立于只读证据呈现：发起入口、进度与
/// 最终结果（成功/取消/失败 + 缺口明细）都在本卡内，页面其余部分保持零交互（故事 23）。
/// </summary>
internal static class DiagnosticsView
{
    public static Control Build(DiagnosticsContent content, DiagnosticsExportState? export = null, Action? exportRequested = null, AuthChannelLaunchState? authChannel = null, Action? channelLaunch = null)
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
        if (export is { } exportState) page.Children.Add(ExportCard(exportState, exportRequested));
        if (authChannel is { } channelState) page.Children.Add(AuthChannelCard(channelState, channelLaunch));
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

    /// <summary>
    /// 诊断包导出卡（#12 / 故事 71–74、78、80、81）：入口按钮（进行中禁用）、受控进度/结果文案
    /// 与缺口明细逐行呈现。卡片不改变页面其余部分的只读边界；提权语义由流程状态机承载。
    /// </summary>
    private static Control ExportCard(DiagnosticsExportState state, Action? exportRequested)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "诊断包导出", FontSize = 15, FontWeight = FontWeight.SemiBold });
        body.Children.Add(ShellTheming.Themed(
            new TextBlock
            {
                Text = state.Message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                LineHeight = 19,
                Opacity = 0.9
            },
            TextBlock.ForegroundProperty, ShellTheming.ToneKey(state.Tone)));
        if (state.NotesList.Count > 0)
        {
            var notes = new StackPanel { Spacing = 3 };
            notes.Children.Add(ShellTheming.Themed(
                new TextBlock { Text = "缺口与超时说明（同时写入包内 README.txt）", FontSize = 11.5 },
                TextBlock.ForegroundProperty, "TextMuted"));
            foreach (var note in state.NotesList)
                notes.Children.Add(ShellTheming.Themed(
                    new TextBlock
                    {
                        Text = "- " + note,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 11.5,
                        LineHeight = 17
                    },
                    TextBlock.ForegroundProperty, "TextMuted"));
            body.Children.Add(notes);
        }

        // 名称随导出状态更新（#14 / 故事 33）：进行中时如实反映，与禁用状态一致。
        var button = ShellAccessibility.Named(new Button
        {
            Content = "导出诊断包…",
            IsEnabled = !state.Running && exportRequested is not null,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 0)
        }, ShellAccessibility.Export(state));
        if (exportRequested is not null) button.Click += (_, _) => exportRequested();
        body.Children.Add(button);
        return ShellTheming.Card(body);
    }

    /// <summary>一个证据分区：标题 + 若干「类别 / 值 + 边界说明」行；不可信状态同样保持分列结构。</summary>
    /// <summary>
    /// 内嵌认证通道卡（工单 #24 / ADR-0009）：只读投影（模式、结构信号、最近结论）+
    /// 明确动作按钮。按钮仅在结构信号存在且模式非关闭时可用；点击前重验由
    /// <see cref="AuthChannelLauncher.LaunchManually"/> 承担，这里只发起意图。
    /// </summary>
    private static Control AuthChannelCard(AuthChannelLaunchState state, Action? channelLaunch)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "内嵌认证通道", FontSize = 15, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = "门户改版导致协议认证失效时，以内嵌浏览器加载真实门户页自动填写并提交；加密与提交仍由学校页面完成。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8
        });
        body.Children.Add(new TextBlock
        {
            Text = $"模式：{state.Mode}｜门户结构信号：{(state.SignalPresent ? "存在" : "无")}"
                + (state.LastConclusion is { } conclusion ? $"｜最近结论：{conclusion}" : ""),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
        var button = new Button
        {
            Content = "发起内嵌认证修复",
            MinWidth = 148,
            IsEnabled = state.SignalPresent && state.Mode != AuthChannelModes.Off && !state.Busy
        };
        button.Click += (_, _) => channelLaunch?.Invoke();
        body.Children.Add(button);
        return ShellTheming.Card(body);
    }

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
