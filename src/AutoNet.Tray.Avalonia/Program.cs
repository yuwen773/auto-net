namespace AutoNet.Tray.Avalonia;

/// <summary>
/// Avalonia 兼容原型入口。验证托盘、隐藏启动、关闭隐藏、单实例、独立提权入口与单文件发布契约；
/// 不读取或修改任何配置，也不控制后台服务。候选版将在本工程上继续完成全部界面迁移。
/// </summary>
internal static class Program
{
    /// <summary>普通托盘单实例互斥名。原型独立命名以免与本机已安装的 WinForms 托盘互斥冲突。</summary>
    internal const string MutexName = @"Local\AutoNet.Tray.Avalonia";

    [STAThread]
    private static int Main(string[] args)
    {
        PrototypeCommandLine request;
        try
        {
            request = PrototypeCommandLine.Parse(args);
        }
        catch (ArgumentException e)
        {
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
                    at = LifecycleReport.Timestamp()
                });
            }

            return AppHost.RunElevated(decision.Operation!.Value, qa);
        }

        if (decision.Kind is StartupActionKind.RequestElevation)
            return RequestElevation(decision.Operation!.Value, qa);

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
                    at = LifecycleReport.Timestamp()
                });
                return 3;
            }

            AppHost.RunNotice("AutoNet 已在系统托盘运行", "请双击托盘图标打开。");
            return 0;
        }

        return AppHost.RunTray(request, qa);
    }

    private static int RequestElevation(ElevatedOperationKind operation, QaEnvironment qa)
    {
        if (qa.Active)
        {
            // QA：记录提权请求而不弹出真实 UAC，供脚本核验“提权入口不被普通实例互斥锁拦截”。
            QaReports.Write(qa.ElevationRecordPath, new
            {
                phase = "elevation-requested",
                operation = operation.CommandLineArgument(),
                processId = Environment.ProcessId,
                processPath = Environment.ProcessPath,
                commandLineArgs = Environment.GetCommandLineArgs().Skip(1),
                at = LifecycleReport.Timestamp()
            });
            return 4;
        }

        Elevation.Launch(operation);
        return 0;
    }
}
