using System.Text.Json;
using AutoNet.Core;
using Avalonia;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 壳级 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=shell 激活）：
/// 驱动真实窗口完成“四页导航截图 + 状态栏降级截图”，证据全部来自真实窗口捕获。
/// 两种运行方式：
/// - 夹具模式（脚本设置 AUTONET_QA_STATUS_PATH 指向 QA 目录内的文件）：过程性写入
///   有效/过期/未来/损坏/缺失状态，核验状态栏安全降级；绝不触碰本机真实 status.json。
/// - 实况模式（未设置该环境变量）：读取本机真实状态文件，核验“读取现有服务状态”。
/// </summary>
internal static class QaShell
{
    public static void Run(ShellController controller, MainWindow window, QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var steps = new List<ShellStep>();
            var fixturePath = Environment.GetEnvironmentVariable("AUTONET_QA_STATUS_PATH");
            var fixtureMode = fixturePath is { Length: > 0 };

            ShellReport Build(string? error = null) => new(
                fixtureMode ? "fixture" : "live",
                fixtureMode,
                Environment.ProcessPath ?? "(unknown)",
                Environment.ProcessId,
                Elevation.IsAdministrator,
                Program.MutexName,
                Application.Current!.RequestedThemeVariant?.ToString() ?? "Default",
                steps,
                error);

            void Step(string step, ShellPage page, string? file) => steps.Add(new ShellStep(
                step,
                page.ToString(),
                ShellPages.Info(page).Name,
                controller.Status?.StripText ?? "(尚无状态)",
                file,
                QaReports.Timestamp()));

            try
            {
                TrayHost.ShowWindow(window);
                await Task.Delay(300);

                if (fixtureMode)
                {
                    // 安全护栏：夹具只允许写 QA 证据目录内的文件，绝不触碰本机真实的 status.json。
                    if (!Path.GetFullPath(fixturePath!).StartsWith(qa.Directory, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("AUTONET_QA_STATUS_PATH 必须位于 QA 证据目录内。");
                    WriteStatus(fixturePath!, Online());
                    await controller.RefreshStatusAsync();
                    await Task.Delay(200);
                }

                foreach (var info in ShellPages.All)
                {
                    await controller.SelectAsync(info.Page);
                    await Task.Delay(250);
                    var shot = qa.ShellShotPath("shell-nav-" + info.Page.ToString().ToLowerInvariant());
                    WindowCapture.Save(window, shot);
                    Step("nav", info.Page, Path.GetFileName(shot));
                }

                if (fixtureMode)
                {
                    foreach (var (name, snapshot) in new (string, StatusSnapshot?)[]
                    {
                        ("stale", Online() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }),
                        ("future", Online() with { UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(30) }),
                        ("invalid", Online() with { Tunnel = null! }),
                        ("missing", null)
                    })
                    {
                        if (snapshot is null) File.Delete(fixturePath!);
                        else WriteStatus(fixturePath!, snapshot);
                        await controller.RefreshStatusAsync();
                        await Task.Delay(150);
                        var shot = qa.ShellShotPath("shell-strip-" + name);
                        WindowCapture.Save(window, shot);
                        Step("state-" + name, controller.SelectedPage, Path.GetFileName(shot));
                    }
                }

                QaReports.Write(qa.ShellReportPath, Build());
                shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(qa.ShellReportPath, Build(e.GetType().Name + ": " + e.Message));
                shutdown(1);
            }
        });
    }

    private static void WriteStatus(string path, StatusSnapshot snapshot) =>
        Storage.AtomicWrite(path, JsonSerializer.Serialize(snapshot, Storage.Json));

    private static StatusSnapshot Online() => new()
    {
        Enabled = true,
        State = "校园网在线",
        Direct = "外网可达",
        Wifi = "已连接",
        Portal = "未查询（外网检查已通过）",
        Tunnel = "系统路径可达，出口未核验",
        Uu = "未核验"
    };
}
