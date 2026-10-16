using AutoNet.Core;
using Avalonia;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 日志页 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=logs 激活）：
/// - 夹具模式：读取 QA 证据目录内的日志夹具（由脚本预先写入），核验日期入口默认最新、
///   受限范围显式标注、旧版记录兼容渲染、损坏记录跳过并说明缺口、快速切换最新获胜、
///   重复刷新稳定、筛选收窄、脱敏复制文本可用，以及读取失败按固定文案降级（不回显异常消息）；
///   夹具目录为空时只记录空态证据（空目录场景）；
/// - 实况模式：只读本机真实日志目录，仅记录计数与说明，绝不把真实日志内容带出本机。
/// 证据全部来自真实窗口捕获；本模式不弹 UAC、不写状态、不触碰剪贴板与资源管理器。
/// </summary>
internal static class QaLogs
{
    /// <summary>夹具日期契约（与 scripts/qa-avalonia-logs.ps1 一致）：新版、旧版、含损坏行、读取失败（被脚本锁定）。</summary>
    internal const string FixtureNewest = "autonet-2026-10-16.jsonl";
    internal const string FixtureLegacy = "autonet-2026-10-15.jsonl";
    internal const string FixtureCorrupt = "autonet-2026-10-14.jsonl";
    internal const string FixtureLocked = "autonet-2026-10-13.jsonl";

    public static void Run(ShellController controller, LogsPage logsPage, MainWindow window, QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var steps = new List<QaLogsStep>();
            var fixturePath = Environment.GetEnvironmentVariable("AUTONET_QA_LOG_DIRECTORY");
            var fixtureMode = fixturePath is { Length: > 0 };

            QaLogsReport Build(string? error = null) => new(
                fixtureMode ? "fixture" : "live",
                fixtureMode,
                Environment.ProcessPath ?? "(unknown)",
                Environment.ProcessId,
                Elevation.IsAdministrator,
                Program.MutexName,
                Application.Current!.RequestedThemeVariant?.ToString() ?? "Default",
                "单日最多 " + LogTailReader.DefaultMaxRecords + " 条",
                steps,
                error);

            try
            {
                if (fixtureMode)
                {
                    // 安全护栏：夹具目录必须位于 QA 证据目录内（补尾分隔符防兄弟目录前缀绕过）。
                    var root = Path.TrimEndingDirectorySeparator(qa.Directory) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(fixturePath!).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("AUTONET_QA_LOG_DIRECTORY 必须位于 QA 证据目录内。");
                }

                TrayHost.ShowWindow(window);
                await Task.Delay(300);

                // 导航到日志页：经壳加载接缝读取当前（最新）日期。
                await controller.SelectAsync(ShellPage.Logs);
                await Task.Delay(200);
                var initial = Content(controller);

                if (fixtureMode && initial.Dates.Count == 0)
                {
                    // 空目录场景：只记录固定空态证据。
                    Record(steps, qa, "empty", initial, includeCopyText: true);
                    Shot(qa, "logs-empty", window);
                    QaReports.Write(qa.LogsReportPath, Build());
                    shutdown(0);
                    return;
                }

                if (fixtureMode)
                {
                    Record(steps, qa, "dates", initial, includeCopyText: true);
                    Shot(qa, "logs-dates", window);

                    await logsPage.SelectDateAsync(FixtureLegacy);
                    await Task.Delay(150);
                    Record(steps, qa, "legacy", Content(controller), includeCopyText: true);
                    Shot(qa, "logs-legacy", window);

                    await logsPage.SelectDateAsync(FixtureCorrupt);
                    await Task.Delay(150);
                    Record(steps, qa, "corrupt", Content(controller), includeCopyText: true);
                    Shot(qa, "logs-corrupt", window);

                    // 快速切换（故事 50）：旧选择不等待，立即被新选择取代；最终内容必须是最新选择。
                    var superseded = logsPage.SelectDateAsync(FixtureLegacy);
                    var latest = logsPage.SelectDateAsync(FixtureCorrupt);
                    await latest;
                    await Task.Delay(150);
                    Record(steps, qa, "switch", Content(controller), includeCopyText: true);
                    Shot(qa, "logs-switch", window);
                    try { await superseded; } catch (Exception) { /* 已被取代的读取按约定收尾 */ }

                    await logsPage.RefreshAsync();
                    await Task.Delay(150);
                    Record(steps, qa, "refresh", Content(controller), includeCopyText: true);
                    Shot(qa, "logs-refresh", window);

                    // 筛选证据在含认证事件的最新日期上进行（损坏日期只有状态类记录，无法证明收窄）。
                    await logsPage.SelectDateAsync(FixtureNewest);
                    await Task.Delay(150);
                    logsPage.SetFilter(LogsFilter.Authentication);
                    await Task.Delay(150);
                    Record(steps, qa, "filter", Content(controller), includeCopyText: true);
                    Shot(qa, "logs-filter", window);
                    logsPage.SetFilter(LogsFilter.All);

                    // 读取失败（故事 49、51）：脚本以独占方式锁定 FixtureLocked，读取必须降级而非崩溃。
                    await logsPage.SelectDateAsync(FixtureLocked);
                    await Task.Delay(300);
                    Record(steps, qa, "read-failure", Content(controller), includeCopyText: true);
                    Shot(qa, "logs-read-failure", window);
                }
                else
                {
                    Record(steps, qa, "live", initial, includeCopyText: false);
                    Shot(qa, "logs-live", window);
                }

                QaReports.Write(qa.LogsReportPath, Build());
                shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(qa.LogsReportPath, Build(e.GetType().Name + ": " + e.Message));
                shutdown(1);
            }
        });
    }

    private static LogsContent Content(ShellController controller)
    {
        if (controller.Content is not LogsContent logs) throw new InvalidOperationException("日志页内容未就绪");
        return logs;
    }

    /// <summary>
    /// 一步证据：范围/缺口/失败说明、首行事件与详情首行（受控文本）。
    /// 夹具模式可附脱敏复制文本供脚本隐私扫描；实况模式绝不记录详情或复制文本。
    /// </summary>
    private static void Record(List<QaLogsStep> steps, QaEnvironment qa, string scenario, LogsContent logs, bool includeCopyText)
    {
        steps.Add(new QaLogsStep(
            scenario,
            logs.Dates.Count,
            logs.SelectedDate,
            logs.Rows.Count,
            logs.ScopeNote,
            logs.GapNote,
            logs.FailureNote,
            logs.Rows.Count > 0 ? logs.Rows[0].Action : "",
            logs.DetailText.Length > 0 ? logs.DetailText.Split('\n')[0].TrimEnd('\r') : "",
            logs.CopyText.Length > 0,
            includeCopyText ? logs.CopyText : null,
            Path.GetFileName(qa.LogsShotPath("logs-" + scenario))));
    }

    private static void Shot(QaEnvironment qa, string name, MainWindow window) =>
        WindowCapture.Save(window, qa.LogsShotPath(name));
}
