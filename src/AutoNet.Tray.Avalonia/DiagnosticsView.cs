using AutoNet.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 网络诊断页视觉构建（迁移工单 #6）：把 <see cref="DiagnosticsContent"/> 渲染为卡片式界面。
/// 五类诊断、认证信号与受控认证分类各成卡片；每行文字、图标与语义色同时呈现，行间以发丝线分隔。
/// 只读边界声明固定在页脚。诊断包导出卡（#12）独立于只读证据呈现：发起入口、进度与
/// 最终结果（成功/取消/失败 + 缺口明细）都在本卡内，页面其余部分保持零交互（故事 23）。
/// </summary>
internal static class DiagnosticsView
{
    public static Control Build(DiagnosticsContent content, DiagnosticsExportState? export = null, Action? exportRequested = null)
    {
        var page = new PageLayout
        {
            NarrowMaxWidth = ShellTheming.PageMaxWidth,
            WideMaxWidth = ShellTheming.WidePageMaxWidth,
            WideThreshold = ShellTheming.WideGrowthThreshold,
            Gap = ShellTheming.CardGap,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        page.Children.Add(ShellTheming.PageHeader(content.Title, ShellPages.Info(content.Page).Description));

        // 宽屏两栏：左栏“五类诊断”（最长的一组证据），右栏认证信号、认证证据与导出。
        // 窄屏按同一顺序堆叠，与原来的单列顺序一致。
        var categoriesColumn = new StackPanel { Spacing = ShellTheming.CardGap };
        categoriesColumn.Children.Add(RowCard("五类诊断", content.Categories));

        var evidenceColumn = new StackPanel { Spacing = ShellTheming.CardGap };
        evidenceColumn.Children.Add(RowCard("认证信号", content.Signals));
        evidenceColumn.Children.Add(RowCard("认证证据", content.Attempts));
        if (export is { } exportState) evidenceColumn.Children.Add(ExportCard(exportState, exportRequested));
        // 只读声明作为页面级常驻说明：与设置页的凭据说明同形（卡片 + 左侧语义条），
        // 不随列表滚动，也不与诊断行混在一起。
        evidenceColumn.Children.Add(ShellTheming.Card(ShellTheming.NoticeStrip(DiagnosticsModel.ReadOnlyNotice)));

        page.Children.Add(new CardColumns
        {
            Breakpoint = ShellTheming.TwoColumnThreshold,
            Gap = 24,
            Children = { categoriesColumn, evidenceColumn }
        });
        return page;
    }

    /// <summary>
    /// 诊断包导出卡（#12 / 故事 71–74、78、80、81）：入口按钮（进行中禁用）、受控进度/结果文案
    /// 与缺口明细逐行呈现。卡片不改变页面其余部分的只读边界；提权语义由流程状态机承载。
    /// </summary>
    private static Control ExportCard(DiagnosticsExportState state, Action? exportRequested)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(ShellTheming.SectionHeader("诊断包导出"));
        body.Children.Add(new TextBlock
        {
            Text = state.Message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 20
        });
        if (state.NotesList.Count > 0)
        {
            var notes = new StackPanel { Spacing = 4 };
            notes.Children.Add(ShellTheming.Label("缺口与超时说明（同时写入包内 README.txt）", 11.5));
            foreach (var note in state.NotesList)
                notes.Children.Add(ShellTheming.Muted("- " + note, 11.5, 17));
            body.Children.Add(notes);
        }

        // 名称随导出状态更新（#14 / 故事 33）：进行中时如实反映，与禁用状态一致。
        var button = ShellAccessibility.Named(ShellTheming.PrimaryButton("导出诊断包…"), ShellAccessibility.Export(state));
        button.IsEnabled = !state.Running && exportRequested is not null;
        button.Margin = new Thickness(0, 4, 0, 0);
        if (exportRequested is not null) button.Click += (_, _) => exportRequested();
        body.Children.Add(button);
        return ShellTheming.Card(body);
    }

    /// <summary>一个证据分区：标题 + 若干「类别 / 值 + 边界说明」行；不可信状态同样保持分列结构。</summary>
    private static Control RowCard(string header, IReadOnlyList<DiagnosticsRow> rows)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(ShellTheming.SectionHeader(header));
        var index = 0;
        foreach (var row in rows)
        {
            if (index++ > 0) body.Children.Add(ShellTheming.Hairline());

            var value = new StackPanel { Spacing = 3 };
            value.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Children =
                {
                    ShellTheming.Glyph(ShellIcons.ToneGlyph(row.Tone), row.Tone, 15),
                    ShellTheming.Themed(
                        new TextBlock
                        {
                            Text = row.Value,
                            FontSize = 13,
                            LineHeight = 20,
                            TextWrapping = TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        TextBlock.ForegroundProperty, ShellTheming.ToneKey(row.Tone))
                }
            });
            if (row.Note.Length > 0) value.Children.Add(ShellTheming.Muted(row.Note, 11.5, 17));

            body.Children.Add(new AdaptiveField
            {
                Breakpoint = 500,
                LabelWidth = 170,
                Gap = 14,
                Children = { ShellTheming.Label(row.Caption), value }
            });
        }

        return ShellTheming.Card(body);
    }
}
