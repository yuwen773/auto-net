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
            if (RecoveryTarget(args) is { } enabled)
            {
                if (!IsAdministrator) { Elevate(enabled ? "--enable-recovery" : "--disable-recovery"); return; }
                SetRecoveryEnabled(enabled, Storage.LoadSettings, Storage.SaveSettings); return;
            }
            if (args.Contains("--unblock-auth"))
            {
                if (!IsAdministrator) { Elevate("--unblock-auth"); return; }
                var safety = new SafetyStateStore(Storage.Private);
                var block = safety.LoadAuthenticationBlock();
                if (block?.BlocksSubmissions != true) { MessageBox.Show("当前没有需要人工解除的认证暂停标记。", "AutoNet"); return; }
                if (MessageBox.Show("请先核实诊断、网关及设备绑定配置。解除后，后台将允许再次提交当前账号密码；若再次被明确拒绝，会重新暂停。\n\n确认已核实并解除认证暂停？",
                    "解除认证暂停", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    var settings = Storage.LoadSettings() ?? throw new InvalidOperationException("请先保存校园网设置。");
                    settings.Validate();
                    var link = Wifi.Read(settings);
                    if (link.State != WifiState.Ready) throw new InvalidOperationException("请先连接指定校园 Wi-Fi 并取得 IPv4。");
                    using var http = DirectNetwork.CreateClient(link);
                    var diagnosis = new DrcomPortal(http, new Uri(settings.PortalOrigin)).DiagnoseAsync(default).GetAwaiter().GetResult();
                    if (diagnosis.Failure != PortalFailure.None)
                    {
                        MessageBox.Show("前置复核未通过，保留认证暂停：" + diagnosis.SafeSummary, "AutoNet");
                        return;
                    }
                    // Compare the same revision and durably audit before releasing. No credentials are loaded here.
                    var finalLink = Wifi.Read(settings);
                    if (Storage.LoadSettings() != settings || finalLink.State != WifiState.Ready ||
                        finalLink.Index != link.Index || !Equals(finalLink.Address, link.Address))
                        throw new InvalidOperationException("诊断期间设置或校园 Wi-Fi 已变化，请重新诊断。");
                    safety.ClearAuthenticationBlock(block);
                }
                return;
            }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUTONET_CAPTURE_PATH")))
            {
                using Form preview = args.Contains("--logs") ? new LogForm() : args.Contains("--settings-preview") ? new SettingsForm(true) : new MainForm { AllowExit = true };
                Application.Run(preview);
                return;
            }
            using var mutex = new Mutex(true, @"Local\AutoNet.Tray", out var first);
            if (!first) { MessageBox.Show("AutoNet 已在系统托盘运行，请双击托盘图标打开。", "AutoNet"); return; }
            Application.Run(new TrayContext(args.Contains("--tray")));
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { }
        catch (Exception e) { MessageBox.Show("操作未完成：" + e.GetType().Name + "\n请检查后台服务是否已安装。", "AutoNet", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { Theme.ReleaseFonts(); }
    }

    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    internal static bool? RecoveryTarget(string[] args)
    {
        var enable = args.Contains("--enable-recovery");
        var disable = args.Contains("--disable-recovery");
        if (args.Contains("--toggle") || (enable && disable))
            throw new ArgumentException("请使用明确的开启或关闭自动恢复命令。");
        return enable ? true : disable ? false : null;
    }

    internal static void SetRecoveryEnabled(bool enabled, Func<Settings?> readSettings, Action<Settings> saveSettings)
    {
        var settings = readSettings() ?? throw new InvalidOperationException("请先保存校园网设置。");
        saveSettings(settings with { Enabled = enabled });
    }

    public static void Elevate(string action) => Process.Start(new ProcessStartInfo(Environment.ProcessPath!, action) { UseShellExecute = true, Verb = "runas" });
    public static void OpenFolder(string path) { if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
}
