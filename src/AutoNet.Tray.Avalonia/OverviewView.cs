using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 概览页视觉构建（迁移工单 #4）：把 <see cref="OverviewContent"/> 渲染为卡片式界面。
/// 状态作为首屏主视觉——语义浅底的状态区承载图标、标题、解释与下一步；认证事实并列其中。
/// 「AutoNet 正在做什么」以标签—值行呈现进度，动作紧邻对应状态。文字与语义色并行表达，
/// 任何状态都不只依赖颜色。本类只负责构建，交互语义在 <see cref="OverviewActions"/> 一侧。
/// </summary>
internal static class OverviewView
{
    public static Control Build(
        OverviewContent content,
        bool technicalExpanded,
        Action<bool> technicalExpandedChanged,
        Action<RecoveryIntent> recoveryRequested,
        Action unblockRequested,
        Action portalOpenRequested,
        Action? restartAuthenticationRequested = null)
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

        // 宽屏两栏：左栏“当前状态”（必要时附浏览器兜底卡），右栏“AutoNet 正在做什么”（含重新认证）。
        // 窄屏按同一顺序堆叠，与原来的单列顺序一致。
        var statusColumn = new StackPanel { Spacing = ShellTheming.CardGap };
        statusColumn.Children.Add(StatusCard(content, technicalExpanded, technicalExpandedChanged, unblockRequested));
        if (content.PortalOpen.Visible)
            statusColumn.Children.Add(PortalOpenCard(content.PortalOpen, portalOpenRequested));

        var progressColumn = new StackPanel { Spacing = ShellTheming.CardGap };
        progressColumn.Children.Add(ProgressCard(content, recoveryRequested, restartAuthenticationRequested));

        page.Children.Add(new CardColumns
        {
            Breakpoint = ShellTheming.TwoColumnThreshold,
            Gap = 24,
            Children = { statusColumn, progressColumn }
        });
        return page;
    }

    /// <summary>
    /// 浏览器兜底开页卡（#11 / 故事 61–69）：独立于认证暂停事实呈现；说明逐字来自投影，
    /// 按钮发起手动开页（自动开页由控制器按授予规则驱动，不经本卡）。
    /// </summary>
    private static Control PortalOpenCard(PortalOpenEntryState entry, Action portalOpenRequested)
    {
        var body = new StackPanel { Spacing = ShellTheming.SectionGap };
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                ShellTheming.IconBadge(ShellIcons.AuthColumnGlyph(OverviewAuthKind.Suspended), entry.Tone, 38),
                ShellTheming.Themed(
                    new TextBlock
                    {
                        Text = "需要浏览器认证",
                        FontSize = 15,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    TextBlock.ForegroundProperty, ShellTheming.ToneKey(entry.Tone))
            }
        });
        body.Children.Add(ShellTheming.Body(entry.Note, 12.5));
        var button = ShellAccessibility.Named(ShellTheming.SecondaryButton(entry.Label), ShellAccessibility.PortalOpen(entry));
        button.IsEnabled = entry.Enabled;
        button.Click += (_, _) => portalOpenRequested();
        body.Children.Add(button);
        return ShellTheming.Card(body);
    }

    /// <summary>
    /// 状态卡：主状态以语义浅底横幅成为首屏焦点，认证事实并列（在线与认证暂停可并存）；
    /// “下一步”作为胶囊紧邻解释文字，技术详情按需展开。
    /// </summary>
    private static Control StatusCard(
        OverviewContent content,
        bool technicalExpanded,
        Action<bool> technicalExpandedChanged,
        Action unblockRequested)
    {
        var body = new StackPanel { Spacing = ShellTheming.SectionGap };
        body.Children.Add(ShellTheming.SectionHeader("当前网络状态"));

        var columns = new AdaptiveColumns { Breakpoint = 540, Gap = 32 };
        columns.Children.Add(NetworkColumn(content));
        if (content.Auth.Visible) columns.Children.Add(AuthColumn(content, unblockRequested));
        var banner = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 16),
            Child = columns
        };
        ShellTheming.Themed(banner, Border.BackgroundProperty, ShellTheming.ToneSurfaceKey(content.Tone));
        body.Children.Add(banner);

        if (content.HasTechnical)
        {
            var expander = ShellAccessibility.Named(new Expander
            {
                Header = "技术详情",
                IsExpanded = technicalExpanded,
                Content = ShellTheming.Muted(content.TechnicalDetail, 12.5, 19)
            }, ShellAccessibility.TechnicalDetail);
            // 重建时保留展开状态；状态刷新不会把已展开的详情折叠回去。
            expander.PropertyChanged += (_, e) =>
            {
                if (e.Property == Expander.IsExpandedProperty) technicalExpandedChanged(expander.IsExpanded);
            };
            body.Children.Add(expander);
        }

        if (content.Chips.Count > 0)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            foreach (var chip in content.Chips)
            {
                var pill = ShellTheming.Pill(chip.Text, chip.Tone);
                pill.Margin = new Thickness(0, 0, 8, 6);
                chips.Children.Add(pill);
            }
            body.Children.Add(chips);
        }

        return ShellTheming.Card(body);
    }

    private static Control NetworkColumn(OverviewContent content)
    {
        var column = new StackPanel { Spacing = 10 };
        column.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children =
            {
                ShellTheming.IconBadge(ShellIcons.StatusGlyph(content.Primary), content.Tone, 46),
                ShellTheming.Themed(
                    new TextBlock
                    {
                        Text = content.StatusTitle,
                        FontSize = 21,
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
            LineHeight = 21
        });
        column.Children.Add(ShellTheming.StepPill("下一步：" + content.NextStep, content.Tone));
        return column;
    }

    /// <summary>认证列：认证暂停、门户结构信号或不可确认独立呈现；解除入口紧邻认证事实。</summary>
    private static Control AuthColumn(OverviewContent content, Action unblockRequested)
    {
        var column = new StackPanel { Spacing = 8 };
        column.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                ShellTheming.IconBadge(ShellIcons.AuthColumnGlyph(content.Auth.Kind), content.Auth.Tone, 34),
                ShellTheming.Themed(
                    new TextBlock
                    {
                        Text = content.Auth.Title,
                        FontSize = 14.5,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    TextBlock.ForegroundProperty, ShellTheming.ToneKey(content.Auth.Tone))
            }
        });
        column.Children.Add(ShellTheming.Muted(content.Auth.Note, 12.5, 19));
        if (content.Unblock.Visible)
        {
            // 名称携带当前认证事实（故事 33）：内容重建即更新，不依据陈旧信息。
            var button = ShellAccessibility.Named(ShellTheming.SecondaryButton(content.Unblock.Label),
                ShellAccessibility.Unblock(content));
            button.IsEnabled = content.Unblock.Enabled;
            button.Margin = new Thickness(0, 2, 0, 0);
            button.Click += (_, _) => unblockRequested();
            column.Children.Add(button);
        }

        return column;
    }

    /// <summary>
    /// 进度卡：回答“AutoNet 正在做什么”；恢复开关就在自动恢复行上（动作靠近对应状态）。
    /// 行间用发丝线分隔，标签列在窄窗口自动折到值上方。
    /// </summary>
    private static Control ProgressCard(
        OverviewContent content,
        Action<RecoveryIntent> recoveryRequested,
        Action? restartAuthenticationRequested)
    {
        var body = new StackPanel { Spacing = ShellTheming.SectionGap };
        body.Children.Add(ShellTheming.SectionHeader("AutoNet 正在做什么"));

        // 名称携带当前状态与明确意图（故事 33、40）：状态变化经内容重建更新名称。
        var button = ShellAccessibility.Named(ShellTheming.SecondaryButton(content.Recovery.Label),
            ShellAccessibility.RecoveryToggle(content.Recovery));
        button.IsEnabled = content.Recovery.Enabled;
        button.MinWidth = 0;
        if (content.Recovery.Intent is { } intent) button.Click += (_, _) => recoveryRequested(intent);
        var recoveryValue = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = content.Recovery.State,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                },
                button
            }
        };
        body.Children.Add(FieldRow("自动恢复", recoveryValue));

        foreach (var row in content.Progress)
        {
            body.Children.Add(ShellTheming.Hairline());
            var value = new StackPanel { Spacing = 3 };
            value.Children.Add(ShellTheming.Body(row.Value, 13));
            if (row.Note.Length > 0) value.Children.Add(ShellTheming.Muted(row.Note, 11.5, 17));
            body.Children.Add(FieldRow(row.Caption, value));
        }

        // 重新认证是额度用尽后的唯一手动出口：放在额度行所在的卡片里，说明它重置什么、不重置什么。
        if (restartAuthenticationRequested is not null)
        {
            body.Children.Add(ShellTheming.Hairline());
            var restart = ShellAccessibility.Named(ShellTheming.SecondaryButton(ShellAccessibility.RestartAuthentication),
                ShellAccessibility.RestartAuthentication);
            restart.IsEnabled = content.Trusted;
            restart.MinWidth = 0;
            restart.Click += (_, _) => restartAuthenticationRequested();
            var restartValue = new StackPanel { Spacing = 7 };
            restartValue.Children.Add(ShellTheming.Muted(
                "重置凭据提交额度，之后可再次提交；认证暂停与主动断线额度保持不变，需管理员确认。", 11.5, 17));
            restartValue.Children.Add(restart);
            body.Children.Add(FieldRow("重新认证", restartValue));
        }

        return ShellTheming.Card(body);
    }

    /// <summary>标签—值行：宽窗口固定标签列，窄窗口折为上下两段。</summary>
    private static Control FieldRow(string caption, Control value) => new AdaptiveField
    {
        Breakpoint = 520,
        LabelWidth = 150,
        Gap = 12,
        Children =
        {
            ShellTheming.Muted(caption, 13, 20),
            value
        }
    };
}
