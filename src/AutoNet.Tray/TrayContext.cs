using AutoNet.Core;

namespace AutoNet.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly MainForm window;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private bool notified;

    public TrayContext(bool startHidden)
    {
        window = new MainForm();
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 AutoNet", null, (_, _) => Show());
        menu.Items.Add("设置认证信息…", null, (_, _) => window.EditSettings());
        menu.Items.Add("暂停 / 恢复自动连接", null, (_, _) => window.Toggle());
        menu.Items.Add("查看日志", null, (_, _) => window.OpenLogs());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出托盘（后台继续运行）", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Icon = NetworkIcon.Create(), Text = "AutoNet · 校园网连接守护", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Show();
        timer.Tick += (_, _) =>
        {
            var status = window.RefreshStatus();
            tray.Text = ("AutoNet · " + (status?.State ?? "服务未就绪或状态过期"))[..Math.Min(63, ("AutoNet · " + (status?.State ?? "服务未就绪或状态过期")).Length)];
            if (status?.CredentialsBlocked == true && !notified)
            {
                notified = true;
                tray.ShowBalloonTip(8000, "AutoNet 需要检查认证", "自动认证已安全暂停。请先查看诊断和日志，勿仅为解锁而重存密码。", ToolTipIcon.Warning);
            }
            if (status?.CredentialsBlocked == false) notified = false;
        };
        timer.Start();
        if (!startHidden) Show();
    }

    private void Show() { window.Show(); if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal; window.Activate(); }
    protected override void ExitThreadCore() { timer.Dispose(); tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose(); window.AllowExit = true; window.Close(); window.Dispose(); base.ExitThreadCore(); }
}
