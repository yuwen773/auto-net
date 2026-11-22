using AutoNet.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 设置页视觉构建（#8 / #41）：普通权限下的可编辑表单——当前状态、守护配置、凭据说明与外观。
/// 非秘密字段由后台状态投影预填；账号与密码不由受限存储回读，密码始终遮挡。
/// 保存按钮把草稿交给注入的 <see cref="SettingsPage"/>（生产 = 独立提权交接），
/// 主题选择经壳注入的偏好提供者读取当前值、经选择委托当场生效。
/// 控件树只构建一次：状态轮询只更新只读事实与结果文本，不打断正在输入的焦点与光标。
/// 窄窗口下标签—控件行折为上下两段，输入框保持可读宽度。
/// </summary>
internal static class SettingsView
{
    /// <summary>主题单选钮的组名（设置页与 QA 定位共用）。</summary>
    public const string GroupName = "AutoNetTheme";

    /// <summary>认证通道单选钮的组名（QA 定位共用）。</summary>
    public const string ChannelGroupName = "AutoNetAuthChannel";

    private const double FieldWidth = 360;

    public static Control Build(
        SettingsPage page,
        ThemePreference theme = ThemePreference.System,
        Action<ThemePreference>? themeSelected = null)
    {
        var loading = false;
        var pageStack = new StackPanel
        {
            Spacing = ShellTheming.CardGap,
            MaxWidth = ShellTheming.PageMaxWidth,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        pageStack.Children.Add(ShellTheming.PageHeader(
            ShellPages.Info(ShellPage.Settings).Name,
            ShellPages.Info(ShellPage.Settings).Description));

        // 只读事实：来自状态快照，随轮询更新；不参与保存。
        var recoveryValue = new TextBlock { FontSize = 13.5, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        var networkValue = new TextBlock { FontSize = 13.5, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var facts = new StackPanel { Spacing = 12 };
        facts.Children.Add(ShellTheming.SectionHeader("当前状态"));
        facts.Children.Add(FactRow("自动恢复", recoveryValue));
        facts.Children.Add(ShellTheming.Hairline());
        facts.Children.Add(FactRow("守护的校园 Wi-Fi", networkValue));
        facts.Children.Add(ShellTheming.Hairline());
        facts.Children.Add(FactRow("认证地址", new TextBlock
        {
            Text = SettingsModel.PortalLine,
            FontSize = 13.5,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        }));
        pageStack.Children.Add(ShellTheming.Card(facts));

        // 可编辑表单。
        var profileBox = ShellAccessibility.Named(
            new ComboBox { MinWidth = 320, Width = FieldWidth, HorizontalAlignment = HorizontalAlignment.Left }, "校园 Wi-Fi 配置");
        foreach (var profile in page.Profiles) profileBox.Items.Add(profile);

        var usernameBox = Input("校园网账号");
        var passwordBox = Input("留空则保留已保存的密码");
        passwordBox.PasswordChar = '●';
        var proxyBox = Input("可选，例如 http://127.0.0.1:7897");
        var endpointBox = Input("可选，真实 UU 服务检测地址");
        var clearProxy = ShellAccessibility.Named(ShellTheming.SecondaryButton("清空"), "清空代理检测地址");
        var clearEndpoint = ShellAccessibility.Named(ShellTheming.SecondaryButton("清空"), "清空 UU 服务检测地址");
        clearProxy.MinWidth = 0;
        clearEndpoint.MinWidth = 0;

        // 三态只用于显示“未确定”：没有可信投影且用户未改动时，页面不知道已保存值。
        var enabledBox = new CheckBox { Content = "启用自动恢复校园网", IsThreeState = true, IsChecked = true };
        var disconnectBox = new CheckBox { Content = "允许主动断线恢复（可能中断 UU 远程）", IsThreeState = true, IsChecked = true };
        var awakeBox = new CheckBox { Content = "接通电源时保持唤醒，允许屏幕关闭", IsThreeState = true, IsChecked = true };

        var channelAuto = new RadioButton { GroupName = ChannelGroupName, Content = "自动：门户改版且认证耗尽时自动以内嵌通道修复" };
        var channelManual = new RadioButton { GroupName = ChannelGroupName, Content = "仅手动（默认）：只在诊断页点按钮时发起" };
        var channelOff = new RadioButton { GroupName = ChannelGroupName, Content = "关闭：不提供内嵌认证通道" };
        var channelStack = new StackPanel { Spacing = 8, Children = { channelAuto, channelManual, channelOff } };

        // 枚举失败或没有可用配置时必须可见：否则用户只会看到一个空下拉框，无从判断原因。
        var profilesNote = NoteBlock();
        var redactedNote = NoteBlock();
        var optionsNote = NoteBlock();
        var projectionNote = NoteBlock();

        var optionsPanel = new StackPanel { Spacing = 12, Children = { enabledBox, disconnectBox, awakeBox } };
        optionsPanel.Children.Add(ShellTheming.Hairline(4, 4));
        optionsPanel.Children.Add(ShellTheming.Label("内嵌认证通道"));
        optionsPanel.Children.Add(channelStack);
        optionsPanel.Children.Add(optionsNote);
        var optionsSurface = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14),
            Child = optionsPanel
        };
        ShellTheming.Themed(optionsSurface, Border.BackgroundProperty, "SurfaceSunken");

        var form = new StackPanel { Spacing = ShellTheming.SectionGap };
        form.Children.Add(ShellTheming.SectionHeader("守护配置"));
        form.Children.Add(Field("校园 Wi-Fi", profileBox));
        form.Children.Add(profilesNote);
        form.Children.Add(Field("校园网账号", usernameBox));
        form.Children.Add(Field("校园网密码", passwordBox));
        form.Children.Add(Field("本机代理检测", AddressInput(proxyBox, clearProxy)));
        form.Children.Add(Field("UU 服务检测", AddressInput(endpointBox, clearEndpoint)));
        // 地址含未回显的路径/查询时说明保留规则；保存端按草稿的 Keep 标记保留完整原值。
        form.Children.Add(redactedNote);
        form.Children.Add(optionsSurface);
        form.Children.Add(ShellTheming.Hairline(2, 10));
        form.Children.Add(ShellTheming.Muted(
            SettingsModel.PasswordNote + "\n账号由后台投影预填；未改动账号时保存端保留已保存的账号，密码仍需输入或留空保留。", 11.5, 19));
        form.Children.Add(ShellTheming.Muted(SettingsModel.SaveNote, 11.5, 19));
        form.Children.Add(projectionNote);

        var save = ShellAccessibility.Named(
            ShellTheming.PrimaryButton(ShellAccessibility.SettingsSave(false)),
            ShellAccessibility.SettingsSave(false));
        var discard = ShellAccessibility.Named(ShellTheming.SecondaryButton("放弃修改"), "放弃修改");
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { save, discard }
        };
        form.Children.Add(actions);

        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
        form.Children.Add(result);
        pageStack.Children.Add(ShellTheming.Card(form));

        pageStack.Children.Add(ShellTheming.Card(
            ShellTheming.NoticeStrip(SettingsModel.CredentialNote)));

        // 外观（#9 / 故事 27–29）：跟随系统、浅色、深色三选一；选择即应用并持久化。
        var appearance = new StackPanel { Spacing = 12 };
        appearance.Children.Add(ShellTheming.SectionHeader("外观"));
        appearance.Children.Add(ShellTheming.Muted(SettingsModel.AppearanceNote, 12.5, 21));
        var choices = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 26,
            Margin = new Thickness(0, 2, 0, 0)
        };
        choices.Children.Add(ThemeChoice(ThemeLabel(ThemePreference.System), ThemePreference.System, theme, themeSelected));
        choices.Children.Add(ThemeChoice(ThemeLabel(ThemePreference.Light), ThemePreference.Light, theme, themeSelected));
        choices.Children.Add(ThemeChoice(ThemeLabel(ThemePreference.Dark), ThemePreference.Dark, theme, themeSelected));
        var choicesSurface = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 13),
            Child = choices,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        ShellTheming.Themed(choicesSurface, Border.BackgroundProperty, "SurfaceSunken");
        appearance.Children.Add(choicesSurface);
        pageStack.Children.Add(ShellTheming.Card(appearance));

        // 选项控件与草稿/投影同步：没有可信投影且用户未改动时显示“未确定”，绝不把页面默认值当成已保存值。
        void SyncOptions()
        {
            var projected = page.Content.ConfigProjected;
            enabledBox.IsChecked = page.Draft.EnabledForDisplay(projected);
            disconnectBox.IsChecked = page.Draft.AllowActiveDisconnectForDisplay(projected);
            awakeBox.IsChecked = page.Draft.KeepAwakeOnAcForDisplay(projected);
            var channelKnown = page.Draft.ChannelModeKnown(projected);
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
            optionsNote.IsVisible = page.Draft.AnyOptionUnknown(page.Content.ConfigProjected);
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

    private static TextBox Input(string placeholder)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            Width = FieldWidth,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        return box;
    }

    private static Control AddressInput(TextBox input, Button clear)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        clear.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(clear, 1);
        grid.Children.Add(input);
        grid.Children.Add(clear);
        return grid;
    }

    /// <summary>一行事实：标签与值成对呈现，窄窗口折为上下两段，不依赖颜色表达状态。</summary>
    private static Control FactRow(string caption, Control value) => new AdaptiveField
    {
        Breakpoint = 520,
        LabelWidth = ShellTheming.LabelWidth,
        Gap = 12,
        Children = { ShellTheming.Label(caption), value }
    };

    /// <summary>一行表单：标签与控件成对呈现，窄窗口折为上下两段。</summary>
    private static Control Field(string caption, Control control) => new AdaptiveField
    {
        Breakpoint = 560,
        LabelWidth = ShellTheming.LabelWidth,
        Gap = 12,
        Children = { ShellTheming.Label(caption), control }
    };

    /// <summary>可显示/隐藏的警示说明：默认隐藏，语义色前景，行高与正文一致。</summary>
    private static TextBlock NoteBlock()
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            FontSize = 11.5,
            IsVisible = false
        };
        return ShellTheming.Themed(block, TextBlock.ForegroundProperty, "ToneWarning");
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
}
