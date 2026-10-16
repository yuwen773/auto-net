using System.Text.Json;
using AutoNet.Core;
using Avalonia;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 网络诊断页 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=diagnostics 激活）：
/// - 夹具模式：过程性写入状态矩阵，核验五类诊断分列、认证信号（暂停与结构信号）分列、
///   受控认证分类、不可信状态不可确认，以及只读边界（本模式不弹 UAC、不发起任何网络动作）；
/// - 实况模式：读取本机真实状态，核验页面在真实数据上的呈现与降级。
/// 证据全部来自真实窗口捕获；夹具只允许写 QA 证据目录内的文件，绝不触碰本机真实 status.json。
/// </summary>
internal static class QaDiagnostics
{
    public static void Run(ShellController controller, MainWindow window, QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var steps = new List<QaDiagnosticsStep>();
            var fixturePath = Environment.GetEnvironmentVariable("AUTONET_QA_STATUS_PATH");
            var fixtureMode = fixturePath is { Length: > 0 };

            QaDiagnosticsReport Build(string? error = null) => new(
                fixtureMode ? "fixture" : "live",
                fixtureMode,
                Environment.ProcessPath ?? "(unknown)",
                Environment.ProcessId,
                Elevation.IsAdministrator,
                Program.MutexName,
                Application.Current!.RequestedThemeVariant?.ToString() ?? "Default",
                DiagnosticsModel.ReadOnlyNotice,
                steps,
                error);

            try
            {
                TrayHost.ShowWindow(window);
                await Task.Delay(300);

                // 全程停在诊断页：状态更新由控制器同步重建内容，不做任何其他导航或动作。
                await controller.SelectAsync(ShellPage.Diagnostics);

                if (fixtureMode)
                {
                    // 安全护栏：夹具只允许写 QA 证据目录内的文件，绝不触碰本机真实的 status.json。
                    // 补尾分隔符：防止兄弟目录前缀（如 …diagnostics-evil\…）绕过 StartsWith。
                    var root = Path.TrimEndingDirectorySeparator(qa.Directory) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(fixturePath!).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("AUTONET_QA_STATUS_PATH 必须位于 QA 证据目录内。");

                    foreach (var (state, snapshot) in Matrix())
                    {
                        if (snapshot is null) File.Delete(fixturePath!);
                        else Storage.AtomicWrite(fixturePath!, JsonSerializer.Serialize(snapshot, Storage.Json));
                        await controller.RefreshStatusAsync();
                        await Task.Delay(200);
                        var shot = qa.DiagnosticsShotPath("diagnostics-" + state);
                        WindowCapture.Save(window, shot);
                        steps.Add(Describe(state, controller, Path.GetFileName(shot)));
                    }
                }
                else
                {
                    await controller.RefreshStatusAsync();
                    await Task.Delay(250);
                    var shot = qa.DiagnosticsShotPath("diagnostics-live");
                    WindowCapture.Save(window, shot);
                    steps.Add(Describe("live", controller, Path.GetFileName(shot)));
                }

                QaReports.Write(qa.DiagnosticsReportPath, Build());
                shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(qa.DiagnosticsReportPath, Build(e.GetType().Name + ": " + e.Message));
                shutdown(1);
            }
        });
    }

    private static QaDiagnosticsStep Describe(string state, ShellController controller, string? file)
    {
        if (controller.Content is not DiagnosticsContent diagnostics)
            throw new InvalidOperationException("诊断页内容未就绪：" + state);
        return new QaDiagnosticsStep(
            state,
            diagnostics.Trusted,
            diagnostics.Categories.Select(row => row.Caption).ToList(),
            diagnostics.Categories.Select(row => row.Value).ToList(),
            diagnostics.Signals.Select(row => row.Caption).ToList(),
            diagnostics.Attempts.FirstOrDefault(row => row.Caption == "最近认证尝试")?.Value ?? "",
            diagnostics.Attempts.FirstOrDefault(row => row.Caption == "认证持久状态")?.Value ?? "",
            file);
    }

    private static IEnumerable<(string State, StatusSnapshot? Snapshot)> Matrix()
    {
        yield return ("online", Online());
        yield return ("auth-failed", Online() with
        {
            State = "需要处理认证",
            Direct = "外网不可达或未知",
            Portal = "未知，不自动认证",
            Tunnel = "系统路径不可达（路由样本推断为 Wi-Fi，出口未核验）",
            Uu = "UU 服务未运行",
            CredentialsBlocked = true,
            AuthenticationStage = "Submission",
            AuthenticationFailure = "BindingRejected",
            AuthenticationHttpStatus = 401,
            CredentialRequestStarted = true,
            AuthenticationPersistentState = "BindingRejected"
        });
        yield return ("layout-suspected", Online() with { PortalLayoutSuspected = true });
        yield return ("layout-and-blocked", Online() with
        {
            CredentialsBlocked = true,
            PortalLayoutSuspected = true,
            AuthenticationStage = "Configuration",
            AuthenticationFailure = "MissingFlag",
            AuthenticationPersistentState = "SafetyBlocked"
        });
        yield return ("stale", Online() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        yield return ("invalid", Online() with { Portal = null! });
        yield return ("missing", null);
    }

    private static StatusSnapshot Online() => new()
    {
        Enabled = true,
        State = "校园网在线",
        Direct = "外网可达",
        Wifi = "已连接",
        Portal = "未查询（外网检查已通过）",
        Tunnel = "系统路径可达（路由样本推断为 Wi-Fi，出口未核验）",
        Uu = "UU 服务运行，2 条 TCP 连接；会话待实测"
    };
}
