using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 设置页视觉构建（迁移工单 #8 / T07）：普通权限的只读视图——守护配置、凭据说明与
/// 修改入口三个卡片。事实行用「标签 + 值」成对呈现；不可信状态显示固定降级文案。
/// 修改入口把动作交给壳注入的委托（生产 = UAC 提权独立操作，QA = 受控记录）。
/// </summary>
internal static class SettingsView
{
    public static Control Build(SettingsContent content, Action modifyRequested)
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

        var guarded = new StackPanel { Spacing = 10 };
        guarded.Children.Add(SectionHeader("守护配置"));
        guarded.Children.Add(FactRow("自动恢复", content.RecoveryState));
        guarded.Children.Add(FactRow("守护的校园 Wi-Fi", content.GuardedNetwork));
        guarded.Children.Add(FactRow("认证地址", SettingsModel.PortalLine));
        page.Children.Add(ShellTheming.Card(guarded));

        page.Children.Add(ShellTheming.Card(new TextBlock
        {
            Text = SettingsModel.CredentialNote,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            FontSize = 13
        }));

        var modify = new StackPanel { Spacing = 10 };
        modify.Children.Add(SectionHeader("修改设置"));
        modify.Children.Add(new TextBlock
        {
            Text = SettingsModel.ModifyNote,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            FontSize = 13,
            Opacity = 0.85
        });
        var button = new Button { Content = content.ModifyLabel, MinWidth = 132, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => modifyRequested();
        modify.Children.Add(button);
        page.Children.Add(ShellTheming.Card(modify));
        return page;
    }

    /// <summary>一行事实：标签与值成对呈现，不依赖颜色表达状态。</summary>
    private static Control FactRow(string caption, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(ShellTheming.Themed(
            new TextBlock { Text = caption, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Width = 148 },
            TextBlock.ForegroundProperty, "TextMuted"));
        row.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 14,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        return row;
    }

    private static TextBlock SectionHeader(string text) =>
        new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold };
}
