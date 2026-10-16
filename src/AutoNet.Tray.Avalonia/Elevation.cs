using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 管理员操作入口。非管理员进程通过 UAC 重启自身并原样传递操作参数；
/// 用户拒绝 UAC 时静默返回，不修改任何状态（故事 43）。
/// 启动动作以委托注入，便于受控测试核验“拒绝即无副作用”：注入的启动器抛 1223 时
/// 既不重试也不改写参数，调用方拿到的就是“什么都没发生”。
/// 生产形态与 <see cref="ElevatedRecovery"/> 一致——非泛型动词入口 + 承载依赖的内部重载。
/// </summary>
internal static class Elevation
{
    public static bool IsAdministrator =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>生产入口：与既有 WinForms 托盘同语义——发起提权，拒绝 UAC 时静默返回。</summary>
    public static void Launch(ElevatedOperationKind operation) => Launch(operation, info => Process.Start(info));

    /// <summary>
    /// 受控接缝：返回是否真正启动了提权进程。
    /// 用户在 UAC 提示上选择“否”（Win32 1223）时返回 false——保持既有语义，什么都不做：
    /// 不改设置、不改认证阻断、不碰服务，也不会有隐藏的重试。
    /// </summary>
    internal static bool Launch(ElevatedOperationKind operation, Action<ProcessStartInfo> start)
    {
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!, operation.CommandLineArgument())
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        try
        {
            start(startInfo);
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            // 用户在 UAC 提示上选择了“否”：保持既有语义，什么都不做。
            return false;
        }
    }
}
