using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 管理员操作入口。非管理员进程通过 UAC 重启自身并原样传递操作参数；
/// 用户拒绝 UAC 时静默返回，不修改任何状态（故事 43 的前置兼容验证）。
/// </summary>
internal static class Elevation
{
    public static bool IsAdministrator =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static void Launch(ElevatedOperationKind operation)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, operation.CommandLineArgument())
            {
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            // 用户在 UAC 提示上选择了“否”：保持既有语义，什么都不做。
        }
    }
}
