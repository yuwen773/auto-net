using Avalonia;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 脚本化生命周期 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=lifecycle|hold 激活）。
/// 用真实进程、真实窗口与托盘驱动“隐藏启动 → 显示 → 关闭隐藏 → 保持运行 → 托盘退出”，
/// 逐步写入证据，供 scripts/qa-avalonia-prototype.ps1 核验。
/// </summary>
internal static class QaLifecycle
{
    public static void Run(PrototypeCommandLine request, TrayHost tray, MainWindow window, QaEnvironment qa)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var steps = new List<LifecycleStep>();
            LifecycleReport Build(string phase, string? error = null) => new(
                phase,
                qa.Mode.ToString(),
                request.StartHidden,
                Environment.ProcessPath ?? "(unknown)",
                Environment.ProcessId,
                Elevation.IsAdministrator,
                Program.MutexName,
                Application.Current!.RequestedThemeVariant?.ToString() ?? "Default",
                steps,
                error);
            try
            {
                // QA 运行期间不打扰桌面：窗口状态照常流转，但保持透明且不进入任务栏。
                window.Opacity = 0;
                window.ShowInTaskbar = false;
                await Task.Delay(200);
                steps.Add(new LifecycleStep("started", window.IsVisible, true, QaReports.Timestamp()));
                QaReports.Write(qa.LifecycleReportPath, Build("started"));
                if (qa.Mode == QaMode.Hold) return;

                tray.ShowMainWindow();
                await Task.Delay(250);
                steps.Add(new LifecycleStep("shown", window.IsVisible, true, QaReports.Timestamp()));

                // 与用户点击窗口关闭按钮相同的路径：Window.Close() 会走 OnClosing 的隐藏分支。
                window.Close();
                await Task.Delay(250);
                steps.Add(new LifecycleStep("closed", window.IsVisible, true, QaReports.Timestamp()));

                // 刻意停留：证明关闭窗口后界面进程仍在运行，随后才由托盘“退出”结束。
                await Task.Delay(600);
                steps.Add(new LifecycleStep("alive-after-close", window.IsVisible, true, QaReports.Timestamp()));

                QaReports.Write(qa.LifecycleReportPath, Build("exit-requested"));
                tray.RequestExit();
            }
            catch (Exception e)
            {
                QaReports.Write(qa.LifecycleReportPath, Build("error", e.GetType().Name + ": " + e.Message));
                tray.RequestExit();
            }
        });
    }
}
