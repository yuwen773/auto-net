using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using AutoNet.Core;

namespace AutoNet.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            if (args.Contains("--settings"))
            {
                if (!IsAdministrator) { Elevate("--settings"); return; }
                if (!Directory.Exists(Storage.Private)) { MessageBox.Show("请先安装后台服务，然后设置认证信息。", "AutoNet"); return; }
                Application.Run(new SettingsForm()); return;
            }
            if (args.Contains("--toggle"))
            {
                if (!IsAdministrator) { Elevate("--toggle"); return; }
                var settings = Storage.LoadSettings() ?? throw new InvalidOperationException("请先保存校园网设置。");
                Storage.SaveSettings(settings with { Enabled = !settings.Enabled }); return;
            }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUTONET_CAPTURE_PATH")))
            {
                using Form preview = args.Contains("--logs") ? new LogForm() : new MainForm { AllowExit = true };
                Application.Run(preview);
                return;
            }
            using var mutex = new Mutex(true, @"Local\AutoNet.Tray", out var first);
            if (!first) { MessageBox.Show("AutoNet 已在系统托盘运行，请双击托盘图标打开。", "AutoNet"); return; }
            Application.Run(new TrayContext(args.Contains("--tray")));
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { }
        catch (Exception e) { MessageBox.Show("操作未完成：" + e.GetType().Name + "\n请检查后台服务是否已安装。", "AutoNet", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static void Elevate(string action) => Process.Start(new ProcessStartInfo(Environment.ProcessPath!, action) { UseShellExecute = true, Verb = "runas" });
    public static void OpenFolder(string path) { if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
}
