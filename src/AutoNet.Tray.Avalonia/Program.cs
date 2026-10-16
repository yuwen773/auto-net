namespace AutoNet.Tray.Avalonia;

/// <summary>
/// AutoNet 应用入口（Avalonia 壳）。承载托盘、隐藏启动、关闭隐藏、普通单实例、
/// 独立提权入口与状态管道；服务生命周期完全独立于本进程。候选包替换既有 WinForms 托盘前，
/// 启动参数、EXE 身份与安装契约保持不变。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 普通托盘单实例互斥名。迁移期间独立命名，避免与本机已安装的 WinForms 托盘
    /// （Local\AutoNet.Tray）相互阻塞；候选包替换旧实现时（工单 #15）必须改回原名。
    /// </summary>
    internal const string MutexName = @"Local\AutoNet.Tray.Avalonia";

    [STAThread]
    private static int Main(string[] args)
    {
        StartupRequest request;
        try
        {
            request = StartupRequest.Parse(args);
        }
        catch (ArgumentException e)
        {
            if (QaEnvironment.FromProcess() is { Active: true } qaEnv)
            {
                // QA：参数错误不弹窗口（脚本无法关闭），只落证据并按既有契约返回 2。
                QaReports.Write(qaEnv.ParseErrorReportPath, new
                {
                    phase = "parse-error",
                    message = e.Message,
                    arguments = args,
                    at = QaReports.Timestamp()
                });
                return 2;
            }

            AppHost.RunNotice("参数无效", e.Message);
            return 2;
        }

        var qa = QaEnvironment.FromProcess();
        var isAdministrator = Elevation.IsAdministrator;

        // 管理员操作先于单实例检查：已有普通托盘实例时也必须能进入提权路径（故事 8）。
        var decision = StartupRouter.Decide(request, isAdministrator, anotherInstanceRunning: false);
        if (decision.Kind is StartupActionKind.RunElevatedOperation)
        {
            if (qa.Active)
            {
                // QA：记录“已提权、直接执行操作”分支，供脚本核验提权入口未被普通实例互斥锁拦截。
                QaReports.Write(qa.ElevatedInlineReportPath, new
                {
                    phase = "elevated-inline",
                    operation = decision.Operation!.Value.CommandLineArgument(),
                    isAdministrator = true,
                    processId = Environment.ProcessId,
                    at = QaReports.Timestamp()
                });
            }

            return AppHost.RunElevated(request, qa);
        }

        if (decision.Kind is StartupActionKind.RequestElevation)
            return RequestElevation(decision.Operation!.Value, qa, request.ExportTargetPath);

        using var instance = SingleInstance.Acquire(MutexName);
        if (!instance.Acquired)
        {
            decision = StartupRouter.Decide(request, isAdministrator, anotherInstanceRunning: true);
            if (decision.Kind is not StartupActionKind.ReportAlreadyRunning)
                throw new InvalidOperationException("单实例路由错误：" + decision.Kind);
            if (qa.Active)
            {
                QaReports.Write(qa.AlreadyRunningReportPath, new
                {
                    phase = "already-running",
                    reportedBy = Environment.ProcessId,
                    processPath = Environment.ProcessPath,
                    mutex = MutexName,
                    at = QaReports.Timestamp()
                });
                return 3;
            }

            AppHost.RunNotice("AutoNet 已在系统托盘运行", "请双击托盘图标打开。");
            return 0;
        }

        return AppHost.RunTray(request, qa);
    }

    private static int RequestElevation(ElevatedOperationKind operation, QaEnvironment qa, string? exportTargetPath = null)
    {
        if (qa.Active)
        {
            // QA：记录提权请求而不弹出真实 UAC，供脚本核验“提权入口不被普通实例互斥锁拦截”。
            QaReports.Write(qa.ElevationRecordPath, new
            {
                phase = "elevation-requested",
                operation = operation.CommandLineArgument(),
                exportTargetPath,
                processId = Environment.ProcessId,
                processPath = Environment.ProcessPath,
                commandLineArgs = Environment.GetCommandLineArgs().Skip(1),
                at = QaReports.Timestamp()
            });
            return 4;
        }

        // 手工命令行触发的导出（非管理员）：原样把用户所选路径带进提权重启（#12 契约不变）。
        if (operation == ElevatedOperationKind.DiagnosticsExport && !string.IsNullOrWhiteSpace(exportTargetPath))
        {
            Elevation.LaunchDiagnosticsExport(exportTargetPath, Elevation.StartProcess);
            return 0;
        }

        Elevation.Launch(operation);
        return 0;
    }
}
