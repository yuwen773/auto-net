using Avalonia;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>一次界面侧导出驱动的证据：场景、记录到的提权目标路径与最终状态。</summary>
internal sealed record QaExportStep(string Scenario, string Stage, string? Target, string Phase, string Message, bool Running);

internal sealed record QaExportReport(
    string Mode,
    string Phase,
    string ProcessPath,
    int ProcessId,
    bool Administrator,
    string MutexName,
    string ThemeVariant,
    List<QaExportStep> Steps,
    List<string> LaunchTargets,
    string? Error = null);

/// <summary>
/// 诊断包导出 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=export-diagnostics 激活，迁移工单 #12）：
/// 界面侧以受控夹具替换保存对话框与提权启动（不弹真实对话框、不弹 UAC、不写真实文件），
/// 逐场景驱动导出状态机并记录「选择 → 提权 → 最终状态」——取消保存不发起提权（74）、
/// 目标路径逐字传递（72→73 交接）、拒绝 UAC 即取消（74）、成功与缺口明细呈现（80）、
/// 失败呈现旧包不变的受控文案（79）、选择阶段异常不逃逸。提权侧由脚本以独立进程运行
/// <see cref="QaExport.RunElevated"/>（只记录意图与受控结果，不做真实采集）。
/// </summary>
internal static class QaExport
{
    private static readonly object Gate = new();
    private static readonly List<string> LaunchTargets = [];
    private static readonly List<QaExportStep> Steps = [];

    /// <summary>当前场景：cancel-pick | deny-uac | ok | notes | fail | throw-pick | throw-launch。</summary>
    private static string scenario = "ok";

    /// <summary>界面侧夹具：QA 模式下替换真实保存对话框（App 构造导出器时注入）。</summary>
    public static Task<string?> PickAsync()
    {
        lock (Gate)
        {
            if (scenario == "throw-pick") throw new IOException("synthetic pick failure");
            return Task.FromResult<string?>(scenario == "cancel-pick" ? null : Path.Combine(QaDirectory!, "chosen-diagnostic-package.zip"));
        }
    }

    /// <summary>界面侧夹具：QA 模式下替换真实提权启动（App 构造导出器时注入）。</summary>
    public static Task<DiagnosticsExportLaunch?> LaunchAsync(string target)
    {
        lock (Gate)
        {
            LaunchTargets.Add(target);
            return Task.FromResult<DiagnosticsExportLaunch?>(scenario switch
            {
                "deny-uac" => null,
                "ok" => new DiagnosticsExportLaunch(0, null, []),
                "notes" => new DiagnosticsExportLaunch(0, null,
                    ["近 3 天没有日志文件。", "status.json 不存在；后台服务可能尚未发布状态。"]),
                "fail" => new DiagnosticsExportLaunch(3, "StubbedExportException", []),
                _ => throw new IOException("synthetic launch failure")
            });
        }
    }

    private static string? QaDirectory;

    public static void Run(ShellController shell, MainWindow window, DiagnosticsExporter exporter, QaEnvironment qa, Action<int> shutdown)
    {
        QaDirectory = qa.Directory;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                TrayHost.ShowWindow(window);
                await shell.SelectAsync(ShellPage.Diagnostics);
                await Task.Delay(300);

                await DriveAsync("cancel-pick-discards-everything", "cancel-pick", exporter, shell, qa);
                Require(LaunchTargets.Count == 0, "取消保存不应发起提权");
                await DriveAsync("deny-uac-cancels-export", "deny-uac", exporter, shell, qa);
                Require(LaunchTargets.Count == 1 && LaunchTargets[0].EndsWith("chosen-diagnostic-package.zip", StringComparison.Ordinal),
                    "提权启动应逐字携带用户所选路径");
                await DriveAsync("success-presents-target", "ok", exporter, shell, qa);
                await DriveAsync("notes-listed-in-result", "notes", exporter, shell, qa);
                await DriveAsync("failure-presents-controlled-kind", "fail", exporter, shell, qa);
                await DriveAsync("pick-exception-controlled", "throw-pick", exporter, shell, qa);
                await DriveAsync("launch-exception-controlled", "throw-launch", exporter, shell, qa);

                // 诊断页导出卡的视觉证据（最终失败态之后恢复 ok 场景再截一张成功态）。
                scenario = "ok";
                await exporter.RunAsync();
                await Task.Delay(200);
                WindowCapture.Save(window, qa.DiagnosticsShotPath("diagnostics-export-card"));

                QaReports.Write(qa.ExportReportPath, new QaExportReport(
                    qa.Mode.ToString(),
                    "ui",
                    Environment.ProcessPath ?? "(unknown)",
                    Environment.ProcessId,
                    Elevation.IsAdministrator,
                    Program.MutexName,
                    Application.Current?.RequestedThemeVariant?.ToString() ?? "Default",
                    Steps,
                    LaunchTargets,
                    null));
                shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(qa.ExportReportPath, new QaExportReport(
                    qa.Mode.ToString(),
                    "ui",
                    Environment.ProcessPath ?? "(unknown)",
                    Environment.ProcessId,
                    Elevation.IsAdministrator,
                    Program.MutexName,
                    Application.Current?.RequestedThemeVariant?.ToString() ?? "Default",
                    Steps,
                    LaunchTargets,
                    e.GetType().Name + ": " + e.Message));
                shutdown(1);
            }
        });
    }

    /// <summary>驱动一个场景：重置状态机上下文 → 发起导出 → 等待终态 → 记录证据。</summary>
    private static async Task DriveAsync(string name, string withScenario, DiagnosticsExporter exporter, ShellController shell, QaEnvironment qa)
    {
        lock (Gate) scenario = withScenario;
        var task = exporter.RunAsync();
        // 界面可在导出期间继续操作（故事 81）：等待期间照常刷新状态投影。
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Require(finished == task, name + "：导出流程未在超时内收束");
        var state = exporter.State;
        Require(!state.Running, name + "：终态不应处于运行中");
        Steps.Add(new QaExportStep(name, withScenario, LaunchTargets.Count > 0 ? LaunchTargets[^1] : null,
            state.Phase.ToString(), state.Message, state.Running));
        await shell.RefreshStatusAsync();
    }

    /// <summary>
    /// 提权侧 QA（AUTONET_QA_MODE=export-diagnostics 且带 --export-diagnostics 启动）：
    /// 只记录「已提权 + 收到的目标路径」并按 AUTONET_QA_EXPORT_OUTCOME 写受控结果文件
    /// （落证据目录，绝不触碰本机真实位置、不做真实采集），退出码与场景一致。
    /// </summary>
    public static void RunElevated(ElevatedMode elevated, QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var target = elevated.ExportTargetPath ?? "";
                var outcome = Environment.GetEnvironmentVariable("AUTONET_QA_EXPORT_OUTCOME") switch
                {
                    "failure" => new DiagnosticsExportOutcome(false, target, "StubbedExportException", [], QaReports.Timestamp()),
                    _ => new DiagnosticsExportOutcome(true, target, null,
                        ["近 3 天没有日志文件。"], QaReports.Timestamp())
                };
                outcome.TryWrite(qa.ExportResultPath);
                QaReports.Write(qa.ExportElevatedReportPath, new
                {
                    phase = "elevated-export",
                    operation = elevated.Operation.CommandLineArgument(),
                    targetPath = target,
                    resultPath = qa.ExportResultPath,
                    outcomeOk = outcome.Ok,
                    processId = Environment.ProcessId,
                    at = QaReports.Timestamp()
                });
                shutdown(outcome.Ok ? 0 : 3);
            }
            catch (Exception e)
            {
                QaReports.Write(qa.ExportElevatedReportPath, new
                {
                    phase = "elevated-export-error",
                    message = e.GetType().Name,
                    at = QaReports.Timestamp()
                });
                shutdown(3);
            }
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
