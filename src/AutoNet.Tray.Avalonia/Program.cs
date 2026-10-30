namespace AutoNet.Tray.Avalonia;

/// <summary>
/// AutoNet 应用入口（Avalonia 壳）。承载托盘、隐藏启动、关闭隐藏、普通单实例、
/// 独立提权入口与状态管道；服务生命周期完全独立于本进程。候选包替换既有 WinForms 托盘前，
/// 启动参数、EXE 身份与安装契约保持不变。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 正式托盘沿用既有安装身份；只有显式 QA 场景使用独立互斥，避免干扰已安装实例。
    /// QA 报告与进程入口按同一规则选择身份。
    /// </summary>
    internal static string MutexName => ResolveMutexName(QaEnvironment.FromProcess());

    /// <summary>
    /// 正式托盘沿用既有安装身份；只有显式 QA 场景使用独立互斥，避免干扰已安装实例。
    /// 同一台机器上可能同时存在旧预览版安装（其正式实例曾占用固定 QA 名）或并行工作树的 QA 进程，
    /// 因此 QA 可用 <c>AUTONET_QA_MUTEX_SUFFIX</c> 追加受控后缀，取得本次运行的独立互斥身份；
    /// 未设置后缀时保持既有固定名，既有 QA 脚本与测试契约不变。
    /// </summary>
    internal static string ResolveMutexName(QaEnvironment qa)
    {
        if (!qa.Active) return @"Local\AutoNet.Tray";
        var suffix = Environment.GetEnvironmentVariable("AUTONET_QA_MUTEX_SUFFIX");
        if (string.IsNullOrEmpty(suffix) || !suffix.All(c => char.IsLetterOrDigit(c) || c == '-'))
            return @"Local\AutoNet.Tray.Avalonia";
        return @"Local\AutoNet.Tray.Avalonia." + suffix;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // #30：凭据交接助手先于普通参数解析、QA 参数记录与单实例；无效交接也不能打开设置窗口。
        if (args.Contains(SettingsHandoffLauncher.Command))
            return SettingsHandoffLauncher.RunReceiverAsync(args).GetAwaiter().GetResult();

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

        var mutexName = ResolveMutexName(qa);
        using var instance = SingleInstance.Acquire(mutexName);
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
                    mutex = mutexName,
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
