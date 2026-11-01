using AutoNet.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 设置页视觉构建（#8 / #41）：普通权限下的可编辑表单——守护配置、凭据说明、外观三个卡片。
/// 非秘密字段由后台状态投影预填；账号与密码不由受限存储回读，密码始终遮挡。
/// 保存按钮把草稿交给注入的 <see cref="SettingsPage"/>（生产 = 独立提权交接），
/// 主题选择经壳注入的偏好提供者读取当前值、经选择委托当场生效。
/// 控件树只构建一次：状态轮询只更新只读事实与结果文本，不打断正在输入的焦点与光标。
/// </summary>
internal static class SettingsView
{
    /// <summary>主题单选钮的组名（设置页与 QA 定位共用）。</summary>
    public const string GroupName = "AutoNetTheme";

    /// <summary>认证通道单选钮的组名（QA 定位共用）。</summary>
    public const string ChannelGroupName = "AutoNetAuthChannel";

    public static Control Build(
        SettingsPage page,
        ThemePreference theme = ThemePreference.System,
        Action<ThemePreference>? themeSelected = null)
    {
        var loading = false;
        var pageStack = new StackPanel { Spacing = 14, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        pageStack.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = ShellPages.Info(ShellPage.Settings).Name, FontSize = 26, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = ShellPages.Info(ShellPage.Settings).Description,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                }
            }
        });

        // 只读事实：来自状态快照，随轮询更新；不参与保存。
        var recoveryValue = new TextBlock { FontSize = 14, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        var networkValue = new TextBlock { FontSize = 14, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var facts = new StackPanel { Spacing = 10 };
        facts.Children.Add(SectionHeader("当前状态"));
        facts.Children.Add(FactRow("自动恢复", recoveryValue));
        facts.Children.Add(FactRow("守护的校园 Wi-Fi", networkValue));
        facts.Children.Add(FactRow("认证地址", new TextBlock
        {
            Text = SettingsModel.PortalLine,
            FontSize = 14,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center
        }));
        pageStack.Children.Add(ShellTheming.Card(facts));

        // 可编辑表单。
        var profileBox = ShellAccessibility.Named(
            new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left }, "校园 Wi-Fi 配置");
        foreach (var profile in page.Profiles) profileBox.Items.Add(profile);

        var usernameBox = ShellAccessibility.Named(new TextBox { PlaceholderText = "校园网账号" }, "校园网账号");
        var passwordBox = ShellAccessibility.Named(new TextBox
        {
            PasswordChar = '●',
            PlaceholderText = "留空则保留已保存的密码"
        }, "校园网密码");
        var proxyBox = ShellAccessibility.Named(new TextBox { PlaceholderText = "可选，例如 http://127.0.0.1:7897" }, "本机代理检测地址");
        var endpointBox = ShellAccessibility.Named(new TextBox { PlaceholderText = "可选，真实 UU 服务检测地址" }, "UU 服务检测地址");
        var clearProxy = ShellAccessibility.Named(new Button { Content = "清空" }, "清空代理检测地址");
        var clearEndpoint = ShellAccessibility.Named(new Button { Content = "清空" }, "清空 UU 服务检测地址");

        // 三态只用于显示“未确定”：没有可信投影且用户未改动时，页面不知道已保存值。
        var enabledBox = new CheckBox { Content = "启用自动恢复校园网", IsThreeState = true, IsChecked = true };
        var disconnectBox = new CheckBox { Content = "允许主动断线恢复（可能中断 UU 远程）", IsThreeState = true, IsChecked = true };
        var awakeBox = new CheckBox { Content = "接通电源时保持唤醒，允许屏幕关闭", IsThreeState = true, IsChecked = true };

        var channelAuto = new RadioButton { GroupName = ChannelGroupName, Content = "自动：门户改版且认证耗尽时自动以内嵌通道修复" };
        var channelManual = new RadioButton { GroupName = ChannelGroupName, Content = "仅手动（默认）：只在诊断页点按钮时发起" };
        var channelOff = new RadioButton { GroupName = ChannelGroupName, Content = "关闭：不提供内嵌认证通道" };
        var channelStack = new StackPanel { Spacing = 4, Children = { channelAuto, channelManual, channelOff } };

        // 枚举失败或没有可用配置时必须可见：否则用户只会看到一个空下拉框，无从判断原因。
        var profilesNote = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 18, FontSize = 12, IsVisible = false };
        ShellTheming.Themed(profilesNote, TextBlock.ForegroundProperty, "ToneWarning");

        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(SectionHeader("守护配置"));
        form.Children.Add(Field("校园 Wi-Fi", profileBox));
        form.Children.Add(profilesNote);
        form.Children.Add(Field("校园网账号", usernameBox));
        form.Children.Add(Field("校园网密码", passwordBox));
        form.Children.Add(Field("本机代理检测", AddressInput(proxyBox, clearProxy)));
        form.Children.Add(Field("UU 服务检测", AddressInput(endpointBox, clearEndpoint)));
        // 地址含未回显的路径/查询时说明保留规则；保存端按草稿的 Keep 标记保留完整原值。
        var redactedNote = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            FontSize = 12,
            IsVisible = false
        };
        ShellTheming.Themed(redactedNote, TextBlock.ForegroundProperty, "ToneWarning");
        form.Children.Add(redactedNote);
        form.Children.Add(enabledBox);
        form.Children.Add(disconnectBox);
        form.Children.Add(awakeBox);
        form.Children.Add(Field("内嵌认证通道", channelStack));
        // 无投影时选项显示为“未确定”；说明与保存语义逐条对应（保留原值 / 首次设置默认值）。
        var optionsNote = new TextBlock
        {
            Text = SettingsModel.OptionsUnknownNote,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            FontSize = 12,
            IsVisible = false
        };
        ShellTheming.Themed(optionsNote, TextBlock.ForegroundProperty, "ToneWarning");
        form.Children.Add(optionsNote);
        form.Children.Add(new TextBlock
        {
            Text = SettingsModel.PasswordNote + "\n账号由后台投影预填；未改动账号时保存端保留已保存的账号，密码仍需输入或留空保留。",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 12,
            Opacity = 0.75
        });
        form.Children.Add(new TextBlock
        {
            Text = SettingsModel.SaveNote,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 12,
            Opacity = 0.75
        });

        var projectionNote = new TextBlock
        {
            Text = SettingsModel.ProjectionUnavailableNote,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 12,
            IsVisible = false
        };
        ShellTheming.Themed(projectionNote, TextBlock.ForegroundProperty, "ToneWarning");
        form.Children.Add(projectionNote);

        var save = ShellAccessibility.Named(
            new Button { Content = ShellAccessibility.SettingsSave(false), MinWidth = 108 },
            ShellAccessibility.SettingsSave(false));
        var discard = ShellAccessibility.Named(new Button { Content = "放弃修改", MinWidth = 108 }, "放弃修改");
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { save, discard }
        };
        form.Children.Add(actions);

        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
        form.Children.Add(result);
        pageStack.Children.Add(ShellTheming.Card(form));

        pageStack.Children.Add(ShellTheming.Card(new TextBlock
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
        pageStack.Children.Add(ShellTheming.Card(appearance));

        // 选项控件与草稿/投影同步：没有可信投影且用户未改动时显示“未确定”，绝不把页面默认值当成已保存值。
        void SyncOptions()
        {
            var projected = page.Content.ConfigProjected;
            enabledBox.IsChecked = SettingsModel.OptionKnown(page.Draft.KeepEnabled, projected) ? page.Draft.Enabled : null;
            disconnectBox.IsChecked = SettingsModel.OptionKnown(page.Draft.KeepAllowActiveDisconnect, projected)
                ? page.Draft.AllowActiveDisconnect : null;
            awakeBox.IsChecked = SettingsModel.OptionKnown(page.Draft.KeepAwakeOnAcKept, projected) ? page.Draft.KeepAwakeOnAc : null;
            var channelKnown = SettingsModel.OptionKnown(page.Draft.KeepAuthChannelMode, projected);
            channelAuto.IsChecked = channelKnown && page.Draft.AuthChannelMode == AuthChannelModes.Automatic;
            channelManual.IsChecked = channelKnown && page.Draft.AuthChannelMode is not (AuthChannelModes.Automatic or AuthChannelModes.Off);
            channelOff.IsChecked = channelKnown && page.Draft.AuthChannelMode == AuthChannelModes.Off;
        }

        void SyncOptionsDeferred()
        {
            loading = true;
            try { SyncOptions(); }
            finally { Dispatcher.UIThread.Post(() => loading = false); }
        }

        void SyncProfiles()
        {
            loading = true;
            try
            {
                profileBox.Items.Clear();
                foreach (var profile in page.Profiles) profileBox.Items.Add(profile);
                profileBox.SelectedItem = page.Draft.Profile is { } selected
                    ? page.Profiles.FirstOrDefault(profile =>
                        profile.InterfaceId == selected.InterfaceId && profile.Name == selected.Name)
                    : null;
                profilesNote.Text = page.ProfileLoadFailed
                    ? "无法读取系统已保存的 Wi-Fi 配置；请稍后重新进入设置页重试。"
                    : page.Profiles.Count == 0 ? "未找到可用的 Wi-Fi 配置；请先在 Windows 中连接并保存校园网。" : "";
                profilesNote.IsVisible = profilesNote.Text.Length > 0;
            }
            finally { Dispatcher.UIThread.Post(() => loading = false); }
        }

        void ApplyDraft()
        {
            loading = true;
            try
            {
                profileBox.SelectedItem = page.Draft.Profile is { } selected
                    ? page.Profiles.FirstOrDefault(profile =>
                        profile.InterfaceId == selected.InterfaceId && profile.Name == selected.Name)
                    : null;
                usernameBox.Text = page.Draft.Username;
                passwordBox.Text = page.Draft.Password;
                proxyBox.Text = page.Draft.ProxyUrl;
                endpointBox.Text = page.Draft.UuEndpoint;
                SyncOptions();
            }
            finally
            {
                // 程序化回填引发的变更事件可能延迟到下一轮调度才送达：先保持忽略，
                // 否则会被当成用户编辑，把草稿误标为已修改并阻止后续状态投影。
                Dispatcher.UIThread.Post(() => loading = false);
            }
        }

        void UpdateState()
        {
            recoveryValue.Text = page.Content.RecoveryState;
            networkValue.Text = page.Content.GuardedNetwork;
            projectionNote.IsVisible = !page.Content.ConfigProjected;
            var redacted = page.Content.ConfigProjected
                ? SettingsModel.RedactedUrlNote(page.Draft.KeepProxyUrl && page.Content.ProxyUrlRedacted,
                    page.Draft.KeepUuEndpoint && page.Content.UuEndpointRedacted) : "";
            redactedNote.Text = redacted;
            redactedNote.IsVisible = redacted.Length > 0;
            SyncOptionsDeferred();
            optionsNote.IsVisible = !page.Content.ConfigProjected && (
                page.Draft.KeepEnabled || page.Draft.KeepAllowActiveDisconnect ||
                page.Draft.KeepAwakeOnAcKept || page.Draft.KeepAuthChannelMode);
            result.Text = page.Message;
            result.IsVisible = page.Message.Length > 0;
            var tone = page.MessageIsError ? "ToneProblem"
                : page.MessageIsSuccess ? "ToneGood" : "ToneNeutral";
            if (Application.Current is { } app && app.TryFindResource(tone, out var brush) && brush is IBrush foreground)
                result.Foreground = foreground;
            save.IsEnabled = !page.Saving;
            discard.IsEnabled = !page.Saving;
            // 保存期间禁止编辑：避免提交后继续输入被误标为已保存或丢失。
            foreach (var control in new Control[]
            {
                profileBox, usernameBox, passwordBox, proxyBox, endpointBox, clearProxy, clearEndpoint,
                enabledBox, disconnectBox, awakeBox, channelAuto, channelManual, channelOff
            })
                control.IsEnabled = !page.Saving;
            // 自动化名称与可见文字同步（#14 / 故事 33）。
            var saveLabel = ShellAccessibility.SettingsSave(page.Saving);
            save.Content = saveLabel;
            AutomationProperties.SetName(save, saveLabel);
        }

        // 所有回写都先与草稿比较：程序化回填造成的迟到事件与用户编辑等价时不得标记为已修改。
        profileBox.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            page.SelectProfile(profileBox.SelectedItem as WifiProfile);
        };
        usernameBox.TextChanged += (_, _) =>
        {
            if (loading) return;
            page.SetUsername(usernameBox.Text ?? "");
        };
        passwordBox.TextChanged += (_, _) =>
        {
            if (loading) return;
            var text = passwordBox.Text ?? "";
            if (text == page.Draft.Password) return;
            page.Draft.Password = text;
            page.MarkDirty();
        };
        // 程序化回填（投影应用）可能迟到于 loading 复位：只有文本真的不同于草稿时才视为用户编辑，
        // 否则会把“未编辑”误判为已编辑并清掉保留原值的标记。
        proxyBox.TextChanged += (_, _) =>
        {
            if (loading) return;
            var text = proxyBox.Text ?? "";
            if (text == page.Draft.ProxyUrl) return;
            page.Draft.ProxyUrl = text;
            page.Draft.KeepProxyUrl = false;
            page.MarkDirty();
        };
        endpointBox.TextChanged += (_, _) =>
        {
            if (loading) return;
            var text = endpointBox.Text ?? "";
            if (text == page.Draft.UuEndpoint) return;
            page.Draft.UuEndpoint = text;
            page.Draft.KeepUuEndpoint = false;
            page.MarkDirty();
        };
        // 三态只用于显示“未知”。用户点击是明确修改：由页面清除“保留原值”标记，
        // 即使选择恰好等于页面默认值也按用户选择保存。
        enabledBox.IsCheckedChanged += (_, _) =>
        {
            if (loading) return;
            if (enabledBox.IsChecked is { } value) { page.SetEnabled(value); return; }
            SyncOptionsDeferred();
        };
        disconnectBox.IsCheckedChanged += (_, _) =>
        {
            if (loading) return;
            if (disconnectBox.IsChecked is { } value) { page.SetAllowActiveDisconnect(value); return; }
            SyncOptionsDeferred();
        };
        awakeBox.IsCheckedChanged += (_, _) =>
        {
            if (loading) return;
            if (awakeBox.IsChecked is { } value) { page.SetKeepAwakeOnAc(value); return; }
            SyncOptionsDeferred();
        };
        channelAuto.IsCheckedChanged += (_, _) => SelectChannel(channelAuto, AuthChannelModes.Automatic);
        channelManual.IsCheckedChanged += (_, _) => SelectChannel(channelManual, AuthChannelModes.ManualOnly);
        channelOff.IsCheckedChanged += (_, _) => SelectChannel(channelOff, AuthChannelModes.Off);

        void SelectChannel(RadioButton radio, string mode)
        {
            if (loading || radio.IsChecked != true) return;
            page.SetAuthChannelMode(mode);
        }

        save.Click += (_, _) => _ = page.SaveAsync();
        discard.Click += (_, _) => page.Discard();
        clearProxy.Click += (_, _) => page.ClearProxyUrl();
        clearEndpoint.Click += (_, _) => page.ClearUuEndpoint();

        page.StateChanged += () => Dispatcher.UIThread.Post(UpdateState);
        page.ProjectionApplied += () => Dispatcher.UIThread.Post(ApplyDraft);
        page.ProfilesChanged += () => Dispatcher.UIThread.Post(SyncProfiles);
        page.PasswordCleared += () => Dispatcher.UIThread.Post(() =>
        {
            loading = true;
            try { passwordBox.Text = ""; }
            finally { Dispatcher.UIThread.Post(() => loading = false); }
        });

        SyncProfiles();
        ApplyDraft();
        UpdateState();
        return pageStack;
    }

    private static Control AddressInput(TextBox input, Button clear)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        clear.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(clear, 1);
        grid.Children.Add(input);
        grid.Children.Add(clear);
        return grid;
    }

    /// <summary>一行事实：标签与值成对呈现，不依赖颜色表达状态。</summary>
    private static Control FactRow(string caption, Control value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(ShellTheming.Themed(
            new TextBlock { Text = caption, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Width = 148 },
            TextBlock.ForegroundProperty, "TextMuted"));
        row.Children.Add(value);
        return row;
    }

    /// <summary>一行表单：标签与控件成对呈现。</summary>
    private static Control Field(string caption, Control control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new TextBlock
        {
            Text = caption,
            Width = 148,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(control);
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
