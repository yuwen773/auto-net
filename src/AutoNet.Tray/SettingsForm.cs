using AutoNet.Core;

namespace AutoNet.Tray;

internal sealed class SettingsForm : Form
{
    private readonly ComboBox profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox username = new() { Dock = DockStyle.Fill };
    private readonly TextBox password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "留空则保留已保存的密码" };
    private readonly TextBox proxy = new() { Dock = DockStyle.Fill, PlaceholderText = "可选，例如 http://127.0.0.1:7897" };
    private readonly TextBox endpoint = new() { Dock = DockStyle.Fill, PlaceholderText = "可选，真实 UU 服务检测地址" };
    private readonly CheckBox awake = new() { Text = "接通电源时保持唤醒，允许屏幕关闭", AutoSize = true, Checked = true };
    private readonly CheckBox enabled = new() { Text = "启用自动恢复校园网", AutoSize = true, Checked = true };
    private readonly CheckBox disconnect = new() { Text = "允许主动断线恢复（可能中断 UU 远程）", AutoSize = true, Checked = true };
    private readonly Settings? existing;
    private readonly Credentials? credentials;
    private readonly bool previewForQa;

    public SettingsForm(bool previewForQa = false)
    {
        this.previewForQa = previewForQa;
        Text = previewForQa ? "AutoNet · 设置（虚构预览）" : "AutoNet · 设置";
        Font = new Font("Microsoft YaHei UI", 10); Icon = NetworkIcon.Create();
        ClientSize = new Size(700, 660); MinimumSize = new Size(700, 690);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26), ColumnCount = 2, RowCount = 12 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 10; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 0 ? 72 : 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        var intro = new Label { Text = "设置一次，开机后自动守护\n认证信息加密保存在本机，后台无需打开浏览器。", AutoSize = true, ForeColor = Color.FromArgb(18, 72, 130) };
        layout.Controls.Add(intro, 0, 0); layout.SetColumnSpan(intro, 2);
        AddRow(layout, 1, "校园 Wi-Fi", profiles);
        AddRow(layout, 2, "认证地址", new Label { Text = "https://login.csust.edu.cn  ·  Dr.COM", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        AddRow(layout, 3, "校园网账号", username); AddRow(layout, 4, "校园网密码", password);
        AddRow(layout, 5, "本机代理检测", proxy); AddRow(layout, 6, "UU 服务检测", endpoint);
        layout.Controls.Add(enabled, 1, 7); layout.Controls.Add(disconnect, 1, 8); layout.Controls.Add(awake, 1, 9);
        var note = new Label { Text = "仅列出可供开机使用的所有用户 Wi-Fi 配置。更改账号需重新输入密码。\n认证站点固定为长沙理工大学；协议变化时暂停认证，需人工检查。\n代理端口仅用于检测，不修改 Clash。UU 服务运行不等同远程会话成功。", AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = Color.FromArgb(65, 75, 88) };
        layout.Controls.Add(note, 0, 10); layout.SetColumnSpan(note, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = MainForm.Button("保存设置"); save.Click += (_, _) => Save();
        save.Enabled = !previewForQa;
        var cancel = MainForm.Button("取消"); cancel.Click += (_, _) => Close();
        actions.Controls.AddRange([save, cancel]); layout.Controls.Add(actions, 0, 11); layout.SetColumnSpan(actions, 2);
        AcceptButton = save; CancelButton = cancel; Controls.Add(layout);
        existing = previewForQa ? new Settings { InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "虚构校园 Wi-Fi" } : Storage.LoadSettings();
        credentials = previewForQa ? null : Storage.LoadCredentials();
        var available = previewForQa ? new List<WifiProfile> { new(existing!.InterfaceId, "测试网卡", existing.WifiProfile, true, true) } : Wifi.Profiles();
        foreach (var profile in available.Where(p => p.AllUsers)) profiles.Items.Add(profile);
        var selected = profiles.Items.Cast<WifiProfile>().FirstOrDefault(p => existing is null ? p.Connected : p.InterfaceId == existing.InterfaceId && p.Name == existing.WifiProfile);
        if (selected is not null) profiles.SelectedItem = selected;
        else if (profiles.Items.Count > 0) profiles.SelectedIndex = 0;
        username.Text = credentials?.Username ?? "";
        proxy.Text = existing?.ProxyUrl ?? ""; endpoint.Text = existing?.UuEndpoint ?? "";
        awake.Checked = existing?.KeepAwakeOnAc ?? true; enabled.Checked = existing?.Enabled ?? true;
        disconnect.Checked = existing?.AllowActiveDisconnect ?? true;
        if (previewForQa) Shown += (_, _) => CaptureForQa();
    }

    private static void AddRow(TableLayoutPanel layout, int row, string title, Control control)
    {
        layout.Controls.Add(new Label { Text = title, AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private void Save()
    {
        if (previewForQa) return;
        try
        {
            if (profiles.SelectedItem is not WifiProfile profile) throw new ArgumentException("请选择校园 Wi-Fi 配置。");
            if (password.Text == "" && (credentials is null || credentials.Username != username.Text.Trim()))
                throw new ArgumentException("首次设置或更改账号时，请输入校园网密码。");
            var settings = new Settings { InterfaceId = profile.InterfaceId, WifiProfile = profile.Name,
                Enabled = enabled.Checked, AllowActiveDisconnect = disconnect.Checked, KeepAwakeOnAc = awake.Checked, ProxyUrl = proxy.Text.Trim(), UuEndpoint = endpoint.Text.Trim() };
            settings.Validate();
            Storage.SaveCredentials(new(username.Text.Trim(), password.Text == "" ? credentials!.Password : password.Text));
            Storage.SaveSettings(settings);
            password.Clear();
            MessageBox.Show("已保存。后台服务将在下一轮检测中使用新设置。", "AutoNet", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (ArgumentException e) { MessageBox.Show(e.Message, "检查设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        catch (Exception e) { MessageBox.Show("保存未完成：" + e.GetType().Name, "AutoNet", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void CaptureForQa()
    {
        var path = Environment.GetEnvironmentVariable("AUTONET_CAPTURE_PATH");
        if (string.IsNullOrEmpty(path)) return;
        if (float.TryParse(Environment.GetEnvironmentVariable("AUTONET_QA_SCALE"), System.Globalization.CultureInfo.InvariantCulture, out var scale) && scale is >= 1 and <= 2)
            Scale(new SizeF(scale, scale));
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(path);
        BeginInvoke(() => Application.Exit());
    }
}
