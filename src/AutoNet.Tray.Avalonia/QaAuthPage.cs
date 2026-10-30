using System.Text.Json;
using AutoNet.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// QA 专用假浏览器宿主（工单 #37 / scripts/qa-avalonia-auth.ps1）：
/// 真实窗口内放一个虚构门户页面，记录附着、导航与可见性次数；不访问网络、不填表、不提交。
/// 只在 <see cref="QaMode.AuthPage"/> 下由应用装配，普通用户机器上永远不会使用。
/// </summary>
internal sealed class QaAuthPageHost : IAuthPageBrowserHost
{
    private readonly StackPanel surface;
    private readonly TextBox input;
    private AuthPageHostSnapshot snapshot = AuthPageHostSnapshot.NotAttached;

    public QaAuthPageHost()
    {
        input = new TextBox
        {
            PlaceholderText = "QA 假宿主输入（不会提交）",
            MinHeight = 38,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        surface = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "假浏览器宿主（QA 夹具）", FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = AuthPageModel.PortalAddress,
                    FontSize = 12,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "此区域仅用于验证页面生命周期；不连接真实门户，不发起认证，不提交凭据。",
                    FontSize = 12,
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap
                },
                input
            }
        };
    }

    public object? Surface => surface;

    public AuthPageHostSnapshot Snapshot => snapshot;

    public int AttachCount { get; private set; }

    public int NavigationCount { get; private set; }

    public int VisibleTrueCount { get; private set; }

    public int VisibleFalseCount { get; private set; }

    public Task<AuthPageHostSnapshot> AttachAsync(Uri portal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AttachCount++;
        NavigationCount = AttachCount;
        snapshot = new(AuthPageHostStatus.Attached, AuthPageModel.PortalAddress, true,
            "QA 假宿主已附着固定门户地址；不填表、不提交。");
        return Task.FromResult(snapshot);
    }

    public void SetVisible(bool visible)
    {
        if (visible) VisibleTrueCount++;
        else VisibleFalseCount++;
    }

    public void Dispose() { }

    /// <summary>QA 驱动输入后核验刷新未覆盖已填内容。</summary>
    public void SetInputForQa(string text) => input.Text = text;

    public string ReadInputForQa() => input.Text ?? "";

    /// <summary>只改写宿主状态用于截图；不创建浏览器、不联网。</summary>
    public void SimulateForQa(AuthPageHostStatus status) => snapshot = status switch
    {
        AuthPageHostStatus.Attached => new(status, AuthPageModel.PortalAddress, true,
            "QA 假宿主已附着固定门户地址；不填表、不提交。"),
        AuthPageHostStatus.RuntimeMissing => new(status, AuthPageModel.PortalAddress, false,
            "QA 夹具：浏览器运行时缺失；认证页受控降级。"),
        AuthPageHostStatus.NetworkBoundaryUnavailable => new(status, AuthPageModel.PortalAddress, false,
            "QA 夹具：受控网络边界未接入；不会连接门户。"),
        AuthPageHostStatus.Failed => new(status, AuthPageModel.PortalAddress, false,
            "QA 夹具：宿主加载失败；可手动重试。"),
        _ => AuthPageHostSnapshot.NotAttached
    };
}

/// <summary>认证页一步证据：用户实际看到的状态、地址、宿主状态与截图。</summary>
internal sealed record QaAuthPageStep(
    string Scenario,
    string Phase,
    string Title,
    string Explanation,
    string NextStep,
    string Background,
    string PortalAddress,
    string HostStatus,
    string HostTitle,
    bool RetryVisible,
    bool SurfacePresent,
    string? File,
    string At);

internal sealed record QaAuthPageReport(
    string Mode,
    bool Fixture,
    string ProcessPath,
    int ProcessId,
    bool Administrator,
    string MutexName,
    string ThemeVariant,
    List<QaAuthPageStep> Steps,
    int AttachCount,
    int NavigationCount,
    int VisibleTrueCount,
    int VisibleFalseCount,
    string InputBefore,
    string InputAfter,
    bool InputPreserved,
    string? Error = null);

/// <summary>
/// 认证页 QA（工单 #37）：在真实窗口内用假宿主验证七种状态（含恢复开关关闭）与额度/存储/暂停/
/// 缺少设置与凭据子状态、打开/切页/最小化不增加导航、普通刷新不丢输入，以及运行时缺失、
/// 边界未就绪与加载失败的受控提示。
/// 只有显式设置 AUTONET_QA_DIR / AUTONET_QA_MODE=auth-page 时启动；不触碰真实状态文件。
/// </summary>
internal static class QaAuthPage
{
    public static void Run(ShellController controller, AuthPageCoordinator authPage, MainWindow window, QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var steps = new List<QaAuthPageStep>();
            var fixturePath = Environment.GetEnvironmentVariable("AUTONET_QA_STATUS_PATH");
            var fixtureMode = fixturePath is { Length: > 0 };
            var inputBefore = "QA 已填写但未提交的测试内容";
            var inputAfter = "";

            QaAuthPageReport Build(string? error = null) => new(
                fixtureMode ? "fixture" : "live",
                fixtureMode,
                Environment.ProcessPath ?? "(unknown)",
                Environment.ProcessId,
                Elevation.IsAdministrator,
                Program.MutexName,
                Application.Current!.RequestedThemeVariant?.ToString() ?? "Default",
                steps,
                ((QaAuthPageHost)authPage.Host).AttachCount,
                ((QaAuthPageHost)authPage.Host).NavigationCount,
                ((QaAuthPageHost)authPage.Host).VisibleTrueCount,
                ((QaAuthPageHost)authPage.Host).VisibleFalseCount,
                inputBefore,
                inputAfter,
                inputBefore == inputAfter,
                error);

            void Step(string scenario, string? file)
            {
                var content = controller.Content as AuthPageContent ?? throw new InvalidOperationException("认证页内容未应用：" + scenario);
                steps.Add(new QaAuthPageStep(
                    scenario,
                    content.Phase.ToString(),
                    content.StatusTitle,
                    content.StatusExplanation,
                    content.NextStep,
                    content.BackgroundNote,
                    content.PortalAddress,
                    content.HostStatus.ToString(),
                    content.HostTitle,
                    content.HostRetryVisible,
                    content.HostAvailable && authPage.Surface is not null,
                    file,
                    QaReports.Timestamp()));
            }

            try
            {
                if (!fixtureMode)
                    throw new InvalidOperationException("认证页 QA 必须设置 AUTONET_QA_STATUS_PATH。");
                if (!Path.GetFullPath(fixturePath!).StartsWith(qa.Directory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("AUTONET_QA_STATUS_PATH 必须位于 QA 证据目录内。");

                var host = (QaAuthPageHost)authPage.Host;
                TrayHost.ShowWindow(window);
                await Task.Delay(300);

                // 先写入联网夹具，确保首屏证据是真实投影而不是“尚未读取状态”。
                WriteStatus(fixturePath!, Base());
                await controller.RefreshStatusAsync();
                await controller.SelectAsync(ShellPage.Authentication);
                await Task.Delay(350);
                await Capture("auth-online");
                Step("online", "auth-online.png");

                foreach (var (scenario, snapshot) in new (string, StatusSnapshot?)[]
                {
                    ("authenticated", Base() with { State = "已认证但外网不可达", Direct = "外网不可达或未知", Portal = "已认证" }),
                    ("outcome-unknown", Base() with
                    {
                        State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                        CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                        AuthenticationFailure = "None"
                    }),
                    ("outcome-exhausted", Base() with
                    {
                        State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                        CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                        AuthenticationFailure = "None",
                        Detail = "已停止自动提交：首次提交后的两次额外额度已用尽；继续低频只读复检。"
                    }),
                    ("outcome-online-pending", Base() with
                    {
                        CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                        AuthenticationFailure = "None"
                    }),
                    ("storage-fault", Base() with
                    {
                        State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                        CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                        AuthenticationFailure = "None", SubmissionResolutionStorageFault = true
                    }),
                    // 恢复开关关闭：服务仍观测到“门户需认证”，但不会因等待而自动提交。
                    ("paused-needs-auth", Paused()),
                    ("paused-outcome-unknown", Paused() with
                    {
                        CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                        AuthenticationFailure = "None"
                    }),
                    ("paused-storage-fault", Paused() with
                    {
                        CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                        AuthenticationFailure = "None", SubmissionResolutionStorageFault = true
                    }),
                    ("paused-exhausted", Paused() with
                    {
                        CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                        AuthenticationFailure = "None",
                        Detail = "已停止自动提交：首次提交后的两次额外额度已用尽；继续低频只读复检。"
                    }),
                    // 缺少设置/凭据：服务不会发出认证请求，页面不得让用户等待自动认证。
                    ("settings-missing", Base() with
                    {
                        Enabled = false, State = "等待设置", Direct = "待检测", Portal = "待检测",
                        Detail = "请在托盘窗口中设置校园 Wi-Fi 和认证信息。"
                    }),
                    ("missing-credentials", Base() with
                    {
                        State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                        CredentialsBlocked = false, AuthenticationPersistentState = "Ready",
                        AuthenticationFailure = "None",
                        Detail = "外网检查未通过；未保存认证信息，请在设置中保存账号密码。"
                    }),
                    ("layout-unblocked", Base() with
                    {
                        State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                        CredentialsBlocked = false, AuthenticationPersistentState = "Ready",
                        AuthenticationFailure = "None", PortalLayoutSuspected = true
                    }),
                    ("layout-blocked", Base() with
                    {
                        State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                        CredentialsBlocked = true, AuthenticationPersistentState = "CredentialsRejected",
                        AuthenticationFailure = "CredentialsRejected", PortalLayoutSuspected = true
                    }),
                    ("waiting-retry", Base() with
                    {
                        State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                        CredentialsBlocked = false, AuthenticationPersistentState = "Ready",
                        NextAttempt = DateTimeOffset.UtcNow.AddSeconds(60)
                    }),
                    ("needs-manual", Base() with
                    {
                        State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                        CredentialsBlocked = true, AuthenticationPersistentState = "CredentialsRejected",
                        AuthenticationFailure = "CredentialsRejected"
                    }),
                    ("unconfirmed", null)
                })
                {
                    if (snapshot is null) File.Delete(fixturePath!);
                    else WriteStatus(fixturePath!, snapshot);
                    await controller.RefreshStatusAsync();
                    await Task.Delay(200);
                    await Capture("auth-" + scenario);
                    Step(scenario, "auth-" + scenario + ".png");
                }

                // 普通状态刷新不重建页面、不重新导航、不覆盖已填内容。
                WriteStatus(fixturePath!, Base());
                await controller.RefreshStatusAsync();
                await Task.Delay(150);
                host.SetInputForQa(inputBefore);
                for (var i = 0; i < 3; i++)
                {
                    await controller.RefreshStatusAsync();
                    await Task.Delay(120);
                }
                inputAfter = host.ReadInputForQa();
                await Capture("auth-input-preserved");
                Step("input-preserved", "auth-input-preserved.png");

                // 切页、最小化与恢复：只改变可见性，不增加附着或导航。
                await controller.SelectAsync(ShellPage.Overview);
                await Task.Delay(150);
                await controller.SelectAsync(ShellPage.Authentication);
                await Task.Delay(250);
                controller.SetWindowVisible(false);
                await Task.Delay(150);
                controller.SetWindowVisible(true);
                await Task.Delay(150);
                Step("switched-and-minimized", null);

                // 受控失败状态：运行时缺失、边界未就绪、加载失败。
                foreach (var (scenario, status) in new (string, AuthPageHostStatus)[]
                {
                    ("runtime-missing", AuthPageHostStatus.RuntimeMissing),
                    ("boundary-unavailable", AuthPageHostStatus.NetworkBoundaryUnavailable),
                    ("attach-failed", AuthPageHostStatus.Failed)
                })
                {
                    host.SimulateForQa(status);
                    await controller.RefreshStatusAsync();
                    await Task.Delay(200);
                    await Capture("auth-" + scenario);
                    Step(scenario, "auth-" + scenario + ".png");
                }

                QaReports.Write(qa.AuthPageReportPath, Build());
                shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(qa.AuthPageReportPath, Build(e.GetType().Name + ": " + e.Message));
                shutdown(1);
            }

            async Task Capture(string name)
            {
                var shot = qa.AuthPageShotPath(name);
                WindowCapture.Save(window, shot);
                await Task.Delay(100);
            }
        });
    }

    private static StatusSnapshot Base() => new()
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Enabled = true,
        State = "校园网在线",
        Wifi = "已连接",
        Direct = "外网可达",
        Portal = "未查询（外网检查已通过）",
        Tunnel = "系统路径可达，出口未核验",
        Uu = "未核验",
        AuthChannelMode = AuthChannelModes.ManualOnly
    };

    /// <summary>恢复开关关闭的服务快照：外网不可达、门户需认证，状态为“已暂停恢复”。</summary>
    private static StatusSnapshot Paused() => Base() with
    {
        Enabled = false,
        State = "已暂停恢复",
        Direct = "外网不可达或未知",
        Portal = "需认证",
        Detail = "自动恢复已暂停；仅检测网络，不重连或认证。"
    };

    private static void WriteStatus(string path, StatusSnapshot snapshot) =>
        Storage.AtomicWrite(path, JsonSerializer.Serialize(snapshot, Storage.Json));
}
