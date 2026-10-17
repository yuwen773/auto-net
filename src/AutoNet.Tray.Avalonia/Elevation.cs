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
    public static void Launch(ElevatedOperationKind operation) => Launch(operation, StartProcess);

    /// <summary>生产启动委托：UseShellExecute + verb runas 已在 startInfo 上设置，直接交给系统。</summary>
    internal static Process? StartProcess(ProcessStartInfo info) => Process.Start(info);

    /// <summary>
    /// 受控接缝：返回是否真正启动了提权进程。
    /// 用户在 UAC 提示上选择“否”（Win32 1223）时返回 false——保持既有语义，什么都不做：
    /// 不改设置、不改认证阻断、不碰服务，也不会有隐藏的重试。
    /// </summary>
    internal static bool Launch(ElevatedOperationKind operation, Func<ProcessStartInfo, Process?> start)
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

    /// <summary>
    /// 诊断包导出（#12 / 故事 72–74）：普通界面已选定目标路径后，以独立提权操作把
    /// 「--export-diagnostics &lt;用户所选路径&gt;」逐字传回自身进程；提权身份只承载采集权限，
    /// 绝不替换用户已选位置。返回提权进程句柄供界面异步等待（界面保持普通权限且可操作，
    /// 故事 81）；用户拒绝 UAC 时返回 null（取消导出，不生成任何文件）。
    /// </summary>
    internal static Process? LaunchDiagnosticsExport(string targetPath, Func<ProcessStartInfo, Process?> start)
    {
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add(ElevatedOperationKind.DiagnosticsExport.CommandLineArgument());
        startInfo.ArgumentList.Add(targetPath);
        try
        {
            return start(startInfo);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            // 用户在 UAC 提示上选择了“否”：取消导出，不生成任何文件（故事 74）。
            return null;
        }
    }

    /// <summary>
    /// 内嵌认证通道（工单 #24 / ADR-0009）：UAC 逐字传参启动辅助进程。辅助进程以提权
    /// 令牌读取机器级凭据（LocalMachine DPAPI），凭据不经托盘进程、不经命令行。
    /// 辅助 exe 不存在（发布布局缺项）时返回 null——调用方呈现受控不可用而非崩溃。
    /// </summary>
    internal static Process? LaunchAuthChannel(string resultPath, Func<ProcessStartInfo, Process?> start)
    {
        var exe = ResolveHelperExe();
        if (exe is null) return null;
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add("--from-settings");
        startInfo.ArgumentList.Add("--result");
        startInfo.ArgumentList.Add(resultPath);
        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add("150");
        startInfo.ArgumentList.Add("--human-wait");
        startInfo.ArgumentList.Add("600");
        try
        {
            return start(startInfo);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return null;
        }
    }

    internal static string? ResolveHelperExe()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            Path.Combine(baseDir, "AutoNet.AuthChannel.exe"),
            Path.Combine(baseDir, "..", "AutoNet.AuthChannel.exe"),
            Path.Combine(baseDir, "..", "authchannel", "AutoNet.AuthChannel.exe"),
        })
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
