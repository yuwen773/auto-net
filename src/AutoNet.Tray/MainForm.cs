using System.Diagnostics;
using System.Text.Json;
using AutoNet.Core;

namespace AutoNet.Tray;

internal sealed class MainForm : Form
{
    private readonly Label state = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 20, FontStyle.Bold) };
    private readonly Label detail = new() { AutoSize = true, MaximumSize = new Size(630, 0), ForeColor = Color.FromArgb(65, 75, 88) };
    private readonly Dictionary<string, Label> values = new();
    private readonly Label timing = new() { AutoSize = true };
    private readonly Button toggle = Button("暂停自动恢复");
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool AllowExit { get; set; }

    public MainForm()
    {
        Text = "AutoNet · 校园网连接守护";
        Font = new Font("Microsoft YaHei UI", 10);
        Icon = NetworkIcon.Create();
        ClientSize = new Size(740, 620);
        MinimumSize = new Size(700, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(247, 249, 252);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32, 26, 32, 24), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        var brand = new Label { Text = "AutoNet  /  校园网连接守护", AutoSize = true, ForeColor = Color.FromArgb(18, 72, 130), Font = new Font(Font, FontStyle.Bold) };
        layout.Controls.Add(brand, 0, 0);
        layout.Controls.Add(state, 0, 1);
        layout.Controls.Add(detail, 0, 2);
        var checks = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, BackColor = Color.White, Padding = new Padding(18, 12, 18, 12) };
        checks.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        checks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;
        foreach (var name in new[] { "校园 Wi-Fi", "校园网直连", "代理 / 系统路径", "UU 远程" })
        {
            checks.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            checks.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.FromArgb(65, 75, 88) }, 0, row);
            var value = new Label { Text = "待检测", AutoSize = true, MaximumSize = new Size(430, 0), Anchor = AnchorStyles.Left };
            checks.Controls.Add(value, 1, row++); values[name] = value;
        }
        layout.Controls.Add(checks, 0, 3);
        timing.ForeColor = Color.FromArgb(65, 75, 88); timing.Padding = new Padding(0, 12, 0, 0);
        layout.Controls.Add(timing, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        var settings = Button("设置…"); settings.Click += (_, _) => EditSettings();
        toggle.Click += (_, _) => Toggle();
        var logs = Button("打开日志"); logs.Click += (_, _) => Program.OpenFolder(Storage.Logs);
        var install = Button("安装服务…"); install.Click += (_, _) => InstallService();
        actions.Controls.AddRange([settings, toggle, logs, install]);
        layout.Controls.Add(actions, 0, 5);
        layout.Controls.Add(new Label { Text = "关闭窗口后继续守护；退出托盘不影响后台服务。", AutoSize = true, ForeColor = Color.FromArgb(65, 75, 88), Padding = new Padding(0, 5, 0, 0) }, 0, 6);
        layout.Controls.Add(new Label { Text = "目标 15 秒恢复 · 仅恢复校园网 · 连续失败退避重试", AutoSize = true, ForeColor = Color.FromArgb(65, 75, 88), Font = new Font(Font.FontFamily, 9) }, 0, 7);
        Controls.Add(layout);
        FormClosing += (_, e) => { if (!AllowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        RefreshStatus();
        Shown += (_, _) => CaptureForQa();
    }

    public StatusSnapshot? RefreshStatus()
    {
        StatusSnapshot? status = null;
        try { if (File.Exists(Storage.StatusPath)) status = JsonSerializer.Deserialize<StatusSnapshot>(Storage.ReadShared(Storage.StatusPath), Storage.Json); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        var fresh = status is not null && DateTimeOffset.UtcNow - status.UpdatedAt < TimeSpan.FromSeconds(15);
        state.Text = fresh ? status!.State : "后台服务未就绪";
        state.ForeColor = fresh && status!.Direct == "外网可达" ? Color.FromArgb(20, 108, 69) : Color.FromArgb(18, 72, 130);
        detail.Text = fresh ? status!.Detail : "首次使用请安装服务，再保存校园 Wi-Fi 和账号密码。已有服务时，请检查是否正在运行。";
        values["校园 Wi-Fi"].Text = fresh ? status!.Wifi : "待检测";
        values["校园网直连"].Text = fresh ? status!.Direct : "待检测";
        values["代理 / 系统路径"].Text = fresh ? status!.Tunnel : "待检测";
        values["UU 远程"].Text = fresh ? status!.Uu : "待检测";
        toggle.Text = status?.Enabled == false ? "恢复自动连接" : "暂停自动恢复";
        timing.Text = fresh ? $"本轮尝试 {status!.Attempts} 次  ·  上次观测恢复：{(status.LastRecoverySeconds is { } seconds ? seconds + " 秒" : "尚无记录")}" : "后台未运行时，不会自动恢复网络。";
        return fresh ? status : null;
    }

    public void EditSettings() => RunAction(() => Program.Elevate("--settings"));
    public void Toggle() => RunAction(() => Program.Elevate("--toggle"));
    private void InstallService() => RunAction(() =>
    {
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "install.ps1"));
        if (!File.Exists(script)) { MessageBox.Show("请使用发布包根目录中的 Install.cmd 安装后台服务。", "AutoNet"); return; }
        var start = new ProcessStartInfo("pwsh.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File"); start.ArgumentList.Add(script); start.ArgumentList.Add("-LaunchSettings");
        Process.Start(start);
    });

    private static void RunAction(Action action)
    {
        try { action(); }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { }
        catch (Exception e) { MessageBox.Show("操作未完成：" + e.GetType().Name, "AutoNet", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    internal static Button Button(string text) => new() { Text = text, AutoSize = true, Height = 36, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 0, 8, 0) };

    private void CaptureForQa()
    {
        var path = Environment.GetEnvironmentVariable("AUTONET_CAPTURE_PATH");
        if (string.IsNullOrEmpty(path)) return;
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(path);
        BeginInvoke(() => Application.Exit());
    }
}
