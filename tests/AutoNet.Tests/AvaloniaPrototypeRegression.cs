using System.Text.Json;
using AutoNet.Tray.Avalonia;

/// <summary>
/// Avalonia 兼容原型的启动路由、单实例与 QA 开关回归。对应迁移工单 #2 的受控证据部分；
/// 真实进程与桌面行为由 scripts/qa-avalonia-prototype.ps1 驱动核验。
/// </summary>
internal static class AvaloniaPrototypeRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("原型验证：命令行路由保持既有启动契约", () => Sync(() =>
        {
            var hidden = StartupRequest.Parse(["--tray"]);
            Require(hidden.StartHidden && hidden.ElevatedOperation is null, "--tray 应隐藏启动且不是管理员操作");
            var shown = StartupRequest.Parse([]);
            Require(!shown.StartHidden && shown.ElevatedOperation is null, "无参数应显示窗口");
            Require(StartupRequest.Parse(["--tray", "--settings"]).ElevatedOperation == ElevatedOperationKind.Settings,
                "--tray 与提权操作并存时按既有优先级进入提权路径");

            // 提权重启必须原样传回同一参数：枚举与命令行逐项往返一致。
            foreach (var operation in Enum.GetValues<ElevatedOperationKind>())
            {
                var argument = operation.CommandLineArgument();
                // #12：导出操作按契约携带用户所选目标路径，单独的参数名不构成完整启动请求。
                if (operation == ElevatedOperationKind.DiagnosticsExport)
                {
                    var withTarget = StartupRequest.Parse([argument, @"C:\pkg\diag.zip"]);
                    Require(withTarget.ElevatedOperation == operation && withTarget.ExportTargetPath == @"C:\pkg\diag.zip",
                        "导出操作应携带目标路径：" + argument);
                    continue;
                }

                Require(StartupRequest.Parse([argument]).ElevatedOperation == operation,
                    "参数往返失败：" + argument);
            }

            Require(ElevatedOperationKind.Settings.CommandLineArgument() == "--settings");
            Require(ElevatedOperationKind.EnableRecovery.CommandLineArgument() == "--enable-recovery");
            Require(ElevatedOperationKind.DisableRecovery.CommandLineArgument() == "--disable-recovery");
            Require(ElevatedOperationKind.UnblockAuthentication.CommandLineArgument() == "--unblock-auth");
            Require(ElevatedOperationKind.DiagnosticsExport.CommandLineArgument() == "--export-diagnostics");

            foreach (var args in new[]
            {
                new[] { "--toggle" },
                new[] { "--enable-recovery", "--disable-recovery" },
                new[] { "--settings", "--unblock-auth" }
            })
            {
                try { StartupRequest.Parse(args); throw new Exception("危险或矛盾命令未被拒绝"); }
                catch (ArgumentException) { }
            }
        })),
        ("原型验证：提权操作先于单实例互斥且不被拦截", () => Sync(() =>
        {
            var elevated = StartupRequest.Parse(["--settings"]);
            // 故事 8 的核心：已有普通实例运行时，提权请求也不得被互斥锁拦截。
            var decision = StartupRouter.Decide(elevated, isAdministrator: false, anotherInstanceRunning: true);
            Require(decision.Kind == StartupActionKind.RequestElevation && decision.Operation == ElevatedOperationKind.Settings,
                "已有普通实例时提权请求被拦截");
            Require(StartupRouter.Decide(elevated, isAdministrator: true, anotherInstanceRunning: true).Kind == StartupActionKind.RunElevatedOperation,
                "管理员进程应直接执行操作");

            var tray = StartupRequest.Parse(["--tray"]);
            Require(StartupRouter.Decide(tray, isAdministrator: false, anotherInstanceRunning: false).Kind == StartupActionKind.RunTray);
            Require(StartupRouter.Decide(tray, isAdministrator: false, anotherInstanceRunning: true).Kind == StartupActionKind.ReportAlreadyRunning,
                "第二个普通实例应报告已在运行");
            Require(AutoNet.Tray.Avalonia.Program.MutexName != @"Local\AutoNet.Tray",
                "原型互斥名必须与本机已安装的 WinForms 托盘区分，避免相互阻塞");
        })),
        ("原型验证：单实例互斥独占且释放后可重新获取", () => Sync(() =>
        {
            var name = @"Local\AutoNet.Tests.SingleInstance." + Guid.NewGuid().ToString("N");
            using (var first = SingleInstance.Acquire(name))
            {
                Require(first.Acquired, "首个实例未取得互斥");
                using var second = SingleInstance.Acquire(name);
                Require(!second.Acquired, "第二个普通实例未被互斥锁拦截");
            }

            using var third = SingleInstance.Acquire(name);
            Require(third.Acquired, "释放后无法重新获取互斥名");
        })),
        ("原型验证：关闭窗口隐藏、托盘退出才结束会话", () => Sync(() =>
        {
            var session = new TraySession();
            Require(session.ShouldHideOnClose && !session.IsShuttingDown, "默认关闭应隐藏而不是结束");
            session.RequestShutdown();
            Require(!session.ShouldHideOnClose && session.IsShuttingDown, "托盘退出后必须允许真正关闭");
        })),
        ("原型验证：QA 开关默认关闭且按模式解析", () => Sync(() =>
        {
            var previousDirectory = Environment.GetEnvironmentVariable("AUTONET_QA_DIR");
            var previousMode = Environment.GetEnvironmentVariable("AUTONET_QA_MODE");
            try
            {
                Environment.SetEnvironmentVariable("AUTONET_QA_DIR", null);
                Environment.SetEnvironmentVariable("AUTONET_QA_MODE", null);
                var inactive = QaEnvironment.FromProcess();
                Require(!inactive.Active && inactive.Mode == QaMode.None, "未设置 QA 目录时不得激活 QA 场景");

                var directory = Path.Combine(Path.GetTempPath(), "autonet-qa-env-" + Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("AUTONET_QA_DIR", directory);
                Environment.SetEnvironmentVariable("AUTONET_QA_MODE", "lifecycle");
                var active = QaEnvironment.FromProcess();
                Require(active.Active && active.Mode == QaMode.Lifecycle);
                Require(active.Directory == Path.GetFullPath(directory));
                Require(active.LifecycleReportPath.StartsWith(active.Directory, StringComparison.OrdinalIgnoreCase));
                Require(active.ThemeLightPath != active.ThemeDarkPath, "明暗主题证据不得写入同一文件");

                Environment.SetEnvironmentVariable("AUTONET_QA_MODE", "shell");
                var shell = QaEnvironment.FromProcess();
                Require(shell.Active && shell.Mode == QaMode.Shell);
                Require(shell.ShellReportPath.StartsWith(shell.Directory, StringComparison.OrdinalIgnoreCase));
                Require(shell.ShellShotPath("x").StartsWith(shell.Directory, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                Environment.SetEnvironmentVariable("AUTONET_QA_DIR", previousDirectory);
                Environment.SetEnvironmentVariable("AUTONET_QA_MODE", previousMode);
            }
        })),
        ("原型验证：QA 证据报告为合法 JSON 且包含阶段字段", () => Sync(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-qa-report-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var qa = new QaEnvironment(QaMode.Hold, directory);
                QaReports.Write(qa.LifecycleReportPath, new LifecycleReport(
                    "started", "Hold", true, "C:\\虚构\\AutoNet.Tray.exe", 1, false, AutoNet.Tray.Avalonia.Program.MutexName, "Default",
                    [new LifecycleStep("started", false, true, QaReports.Timestamp())]));
                QaReports.Write(qa.ElevationRecordPath, new { phase = "elevation-requested", operation = "--settings" });
                QaReports.Write(qa.AlreadyRunningReportPath, new { phase = "already-running" });

                using var lifecycle = JsonDocument.Parse(File.ReadAllText(qa.LifecycleReportPath));
                Require(lifecycle.RootElement.GetProperty("Phase").GetString() == "started");
                Require(lifecycle.RootElement.GetProperty("StartHidden").GetBoolean());
                Require(lifecycle.RootElement.GetProperty("Steps").GetArrayLength() == 1);
                using var elevation = JsonDocument.Parse(File.ReadAllText(qa.ElevationRecordPath));
                Require(elevation.RootElement.GetProperty("phase").GetString() == "elevation-requested");
                using var blocked = JsonDocument.Parse(File.ReadAllText(qa.AlreadyRunningReportPath));
                Require(blocked.RootElement.GetProperty("phase").GetString() == "already-running");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }))
    ];

    private static Task Sync(Action action) { action(); return Task.CompletedTask; }

    private static void Require(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
}
