using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 设置页视觉构建（迁移工单 #8 / T07；外观分区为 #9 / T04）：普通权限的只读视图——
/// 守护配置、凭据说明、外观与修改入口四个卡片。事实行用「标签 + 值」成对呈现；
/// 不可信状态显示固定降级文案。修改入口把动作交给壳注入的委托（生产 = UAC 提权独立操作，
/// QA = 受控记录）；主题选择经壳注入的偏好提供者读取当前值、经选择委托当场生效（不走状态投影）。
/// </summary>
internal static class SettingsView
{
    /// <summary>主题单选钮的组名（设置页与 QA 定位共用）。</summary>
    public const string GroupName = "AutoNetTheme";

    public static Control Build(
        SettingsContent content,
        Action modifyRequested,
        ThemePreference theme = ThemePreference.System,
        Action<ThemePreference>? themeSelected = null)
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

        // 外观（#9 / 故事 27–29）：跟随系统、浅色、深色三选一；选择即应用并持久化。
        var appearance = new StackPanel { Spacing = 10 };
        appearance.Children.Add(SectionHeader("外观"));
        appearance.Children.Add(new TextBlock
        {
            Text = SettingsModel.AppearanceNote,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            FontSize = 13,
            Opacity = 0.85
        });
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        choices.Children.Add(ThemeChoice(ThemeLabel(ThemePreference.System), ThemePreference.System, theme, themeSelected));
        choices.Children.Add(ThemeChoice(ThemeLabel(ThemePreference.Light), ThemePreference.Light, theme, themeSelected));
        choices.Children.Add(ThemeChoice(ThemeLabel(ThemePreference.Dark), ThemePreference.Dark, theme, themeSelected));
        appearance.Children.Add(choices);
        page.Children.Add(ShellTheming.Card(appearance));

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
        // 修改入口的标签随状态投影变化（不可信时为固定降级文案）；显式可访问名称与可见文本一致（#14 / 故事 33）。
        var button = ShellAccessibility.Named(
            new Button { Content = content.ModifyLabel, MinWidth = 132, HorizontalAlignment = HorizontalAlignment.Left },
            content.ModifyLabel);
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

    /// <summary>主题单选钮的文案（QA 以同一文案定位控件，单一来源避免逐字漂移）。</summary>
    internal static string ThemeLabel(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => "浅色",
        ThemePreference.Dark => "深色",
        _ => "跟随系统"
    };

    /// <summary>主题单选钮：同组互斥；先设初值再订阅选中变化，程序化赋值不回写偏好。</summary>
    private static RadioButton ThemeChoice(string label, ThemePreference preference, ThemePreference current, Action<ThemePreference>? selected)
    {
        var radio = new RadioButton { Content = label, GroupName = GroupName };
        radio.IsChecked = current == preference;
        radio.IsCheckedChanged += (_, _) =>
        {
            if (radio.IsChecked == true) selected?.Invoke(preference);
        };
        return radio;
    }

    private static TextBlock SectionHeader(string text) =>
        new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold };
}
