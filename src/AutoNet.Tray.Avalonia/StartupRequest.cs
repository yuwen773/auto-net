namespace AutoNet.Tray.Avalonia;

/// <summary>应用支持的管理员操作。参数名与既有 WinForms 托盘逐字一致，候选包替换时启动契约不变。</summary>
internal enum ElevatedOperationKind
{
    Settings,
    EnableRecovery,
    DisableRecovery,
    UnblockAuthentication,
    /// <summary>诊断包导出（#12）：提权进程按既有参数携带的用户所选目标路径执行只读采集。</summary>
    DiagnosticsExport
}

/// <summary>解析后的启动请求。只解释参数语义，不接触 UI、互斥或进程。</summary>
internal sealed record StartupRequest(bool StartHidden, ElevatedOperationKind? ElevatedOperation, string? ExportTargetPath = null)
{
    public static StartupRequest Parse(string[] args)
    {
        if (args.Contains("--toggle")) throw new ArgumentException("请使用明确的开启或关闭自动恢复命令。");
        var enable = args.Contains("--enable-recovery");
        var disable = args.Contains("--disable-recovery");
        if (enable && disable) throw new ArgumentException("请使用明确的开启或关闭自动恢复命令。");
        var operations = new List<ElevatedOperationKind>();
        if (args.Contains("--settings")) operations.Add(ElevatedOperationKind.Settings);
        if (enable) operations.Add(ElevatedOperationKind.EnableRecovery);
        if (disable) operations.Add(ElevatedOperationKind.DisableRecovery);
        if (args.Contains("--unblock-auth")) operations.Add(ElevatedOperationKind.UnblockAuthentication);
        // #12：--export-diagnostics <保存路径>——提权采集目标在普通界面选定后逐字传递，
        // 提权身份不得替换用户已选位置（UI-MIGRATION-SPEC 决策 12）。
        string? exportTarget = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != "--export-diagnostics") continue;
            if (exportTarget is not null) throw new ArgumentException("--export-diagnostics 只能出现一次。");
            if (i + 1 >= args.Length) throw new ArgumentException("使用 --export-diagnostics 时必须提供保存路径。");
            exportTarget = args[++i];
        }

        if (exportTarget is not null) operations.Add(ElevatedOperationKind.DiagnosticsExport);
        if (operations.Count > 1) throw new ArgumentException("同一次启动只允许一个管理员操作。");
        return new StartupRequest(args.Contains("--tray"), operations.Count == 1 ? operations[0] : null, exportTarget);
    }
}

internal static class ElevatedOperations
{
    /// <summary>传回自身进程的命令行参数。提权重启必须原样传递，不得改写。</summary>
    public static string CommandLineArgument(this ElevatedOperationKind kind) => kind switch
    {
        ElevatedOperationKind.Settings => "--settings",
        ElevatedOperationKind.EnableRecovery => "--enable-recovery",
        ElevatedOperationKind.DisableRecovery => "--disable-recovery",
        ElevatedOperationKind.UnblockAuthentication => "--unblock-auth",
        ElevatedOperationKind.DiagnosticsExport => "--export-diagnostics",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string DisplayName(this ElevatedOperationKind kind) => kind switch
    {
        ElevatedOperationKind.Settings => "设置认证信息",
        ElevatedOperationKind.EnableRecovery => "开启自动恢复",
        ElevatedOperationKind.DisableRecovery => "关闭自动恢复",
        ElevatedOperationKind.UnblockAuthentication => "解除认证暂停",
        ElevatedOperationKind.DiagnosticsExport => "导出诊断包",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
