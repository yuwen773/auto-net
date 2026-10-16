using AutoNet.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 管理员设置编辑窗口（迁移工单 #8 / T07 / 故事 35–39）：在提权进程内运行，承接既有 WinForms
/// 设置表单的全部字段与保存规则。普通壳界面永远不打开此窗口——它只经 UAC 确认后出现（故事 34）。
/// 取消按钮与标题栏关闭都不写入任何配置（故事 38）；保存按既有规则执行（故事 36、37），
/// 绝不触碰认证暂停标记（故事 39）。校验失败在窗口内显示受控文案并保持打开；
/// 保存成功后关闭窗口，提权进程随即结束。
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly ElevatedSettings editor;
    private readonly ComboBox profiles = new() { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox username = new() { PlaceholderText = "校园网账号" };
    private readonly TextBox password = new() { PasswordChar = '●', PlaceholderText = "留空则保留已保存的密码" };
    private readonly TextBox proxy = new() { PlaceholderText = "可选，例如 http://127.0.0.1:7897" };
    private readonly TextBox endpoint = new() { PlaceholderText = "可选，真实 UU 服务检测地址" };
    private readonly CheckBox awake = new() { Content = "接通电源时保持唤醒，允许屏幕关闭", IsChecked = true };
    private readonly CheckBox enabled = new() { Content = "启用自动恢复校园网", IsChecked = true };
    private readonly CheckBox disconnect = new() { Content = "允许主动断线恢复（可能中断 UU 远程）", IsChecked = true };
    private readonly TextBlock result = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly Button save = new() { Content = "保存设置", MinWidth = 108 };
    private readonly Button cancel = new() { Content = "取消", MinWidth = 96, IsCancel = true };
    private IReadOnlyList<WifiProfile> available = [];

    internal SettingsWindow(ElevatedSettings editor)
    {
        this.editor = editor;
        Title = "AutoNet · 设置";
        Icon = AppIcons.Load();
        Width = 700;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildLayout();
        Load();
    }

    /// <summary>生产入口：private 目录缺失时返回 null（调用方显示安装提示，同既有语义）。</summary>
    public static SettingsWindow? Create(ElevatedSettings editor)
    {
        if (!System.IO.Directory.Exists(Storage.Private)) return null;
        return new SettingsWindow(editor);
    }

    private Control BuildLayout()
    {
        var layout = new StackPanel { Margin = new Thickness(26), Spacing = 14 };

        layout.Children.Add(new TextBlock
        {
            Text = "设置一次，开机后自动守护。认证信息加密保存在本机，后台无需打开浏览器。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        });

        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(Field("校园 Wi-Fi", profiles));
        form.Children.Add(Field("认证地址", new TextBlock { Text = SettingsModel.PortalLine.Trim(), VerticalAlignment = VerticalAlignment.Center }));
        form.Children.Add(Field("校园网账号", username));
        form.Children.Add(Field("校园网密码", password));
        form.Children.Add(Field("本机代理检测", proxy));
        form.Children.Add(Field("UU 服务检测", endpoint));
        form.Children.Add(enabled);
        form.Children.Add(disconnect);
        form.Children.Add(awake);
        layout.Children.Add(form);

        layout.Children.Add(new TextBlock
        {
            Text = "仅列出可供开机使用的所有用户 Wi-Fi 配置。更改账号需重新输入密码。\n认证站点固定为长沙理工大学；协议变化时暂停认证，需人工检查。\n代理端口仅用于检测，不修改 Clash。UU 服务运行不等同远程会话成功。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 19
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => Save();
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        layout.Children.Add(actions);
        layout.Children.Add(result);
        return layout;
    }

    private static Control Field(string caption, Control control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new TextBlock { Text = caption, Width = 118, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        row.Children.Add(control);
        return row;
    }

    /// <summary>填充当前配置。查看不产生任何写入（故事 38）；凭据密码从不进入任何控件。</summary>
    private void Load()
    {
        var state = editor.Load();
        available = state.Profiles;
        foreach (var profile in available) profiles.Items.Add(profile);
        var existing = state.Settings;
        var selected = available.FirstOrDefault(profile => existing is null
            ? profile.Connected
            : profile.InterfaceId == existing.InterfaceId && profile.Name == existing.WifiProfile);
        if (selected is not null) profiles.SelectedItem = selected;
        else if (available.Count > 0) profiles.SelectedIndex = 0;
        username.Text = state.Credentials?.Username ?? "";
        proxy.Text = existing?.ProxyUrl ?? "";
        endpoint.Text = existing?.UuEndpoint ?? "";
        awake.IsChecked = existing?.KeepAwakeOnAc ?? true;
        enabled.IsChecked = existing?.Enabled ?? true;
        disconnect.IsChecked = existing?.AllowActiveDisconnect ?? true;
    }

    /// <summary>保存：收集草稿交给编辑器规则；成功即关闭（进程随后结束），失败在窗口内说明并保持打开。</summary>
    private void Save()
    {
        var draft = new SettingsDraft(
            profiles.SelectedItem as WifiProfile,
            username.Text ?? "",
            password.Text ?? "",
            proxy.Text ?? "",
            endpoint.Text ?? "",
            enabled.IsChecked == true,
            disconnect.IsChecked == true,
            awake.IsChecked == true);
        ApplySave(draft);
    }

    /// <summary>
    /// QA 接缝（仅 AUTONET_QA_MODE=settings 激活路径调用）：以给定草稿驱动与用户保存
    /// 完全相同的交互路径，让窗口内的真实结果文本进入视觉证据；生产交互从不经过这里。
    /// </summary>
    internal void RunSaveForQa(SettingsDraft draft) => ApplySave(draft);

    private void ApplySave(SettingsDraft draft)
    {
        var outcome = editor.Save(draft);
        if (outcome.Saved)
        {
            // 与既有表单同语义：保存成功给出确认反馈（WinForms 为 MessageBox），密码不留存于
            // 控件内存；用户关闭窗口（取消按钮或标题栏）后宿主结束提权进程。
            password.Clear();
            save.IsEnabled = false;
            cancel.Content = "关闭";
            result.Text = outcome.Message;
            return;
        }

        // 校验失败保持窗口打开：用户可以直接修正；文案是受控校验消息或只含异常类型（安全边界）。
        result.Text = outcome.Message;
    }
}
