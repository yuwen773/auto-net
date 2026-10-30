using System.Text.Json;
using AutoNet.Core;
using Avalonia;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 概览页 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=overview 激活）：
/// - 夹具模式：过程性写入状态矩阵，核验“状态/进度/下一步”投影、在线与认证暂停分列、
///   不可信状态不可确认并禁用动作，并驱动恢复开关与解除入口记录明确意图（本模式不弹真实 UAC）；
/// - 实况模式：读取本机真实状态，核验“读取现有服务状态”。
/// 证据全部来自真实窗口捕获；夹具只允许写 QA 证据目录内的文件，绝不触碰本机真实 status.json。
/// </summary>
internal static class QaOverview
{
    private static readonly object launchGate = new();
    private static readonly List<string> launches = [];

    /// <summary>概览 QA 的启动记录适配器：只记录命令意图，不弹出 UAC（生产路径使用 <see cref="Elevation.Launch"/>）。</summary>
    public static void RecordLaunch(ElevatedOperationKind kind)
    {
        lock (launchGate) launches.Add(kind.CommandLineArgument());
    }

    public static void Run(ShellController controller, OverviewActions actions, MainWindow window, QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var steps = new List<QaOverviewStep>();
            var records = new List<QaOverviewAction>();
            var fixturePath = Environment.GetEnvironmentVariable("AUTONET_QA_STATUS_PATH");
            var fixtureMode = fixturePath is { Length: > 0 };
            lock (launchGate) launches.Clear();

            QaOverviewReport Build(string? error = null) => new(
                fixtureMode ? "fixture" : "live",
                fixtureMode,
                Environment.ProcessPath ?? "(unknown)",
                Environment.ProcessId,
                Elevation.IsAdministrator,
                Program.MutexName,
                Application.Current!.RequestedThemeVariant?.ToString() ?? "Default",
                steps,
                records,
                error);

            try
            {
                TrayHost.ShowWindow(window);
                await Task.Delay(300);

                if (fixtureMode)
                {
                    // 安全护栏：夹具只允许写 QA 证据目录内的文件，绝不触碰本机真实的 status.json。
                    if (!Path.GetFullPath(fixturePath!).StartsWith(qa.Directory, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("AUTONET_QA_STATUS_PATH 必须位于 QA 证据目录内。");

                    foreach (var (state, snapshot) in Matrix())
                    {
                        if (snapshot is null) File.Delete(fixturePath!);
                        else WriteStatus(fixturePath!, snapshot);
                        await controller.RefreshStatusAsync();
                        await Task.Delay(200);
                        var shot = qa.OverviewShotPath("overview-" + state);
                        WindowCapture.Save(window, shot);
                        steps.Add(Describe(state, controller, Path.GetFileName(shot)));
                    }

                    // 动作意图（本模式不弹 UAC，只记录命令，逐字核对应发起的参数）：
                    WriteStatus(fixturePath!, Online());
                    await SettleAsync(controller);
                    records.Add(await DriveDisplayedRecoveryAsync("在线且已开启：按显示意图关闭", controller, actions));

                    WriteStatus(fixturePath!, Online() with { Enabled = false });
                    await SettleAsync(controller);
                    records.Add(await DriveDisplayedRecoveryAsync("在线且已关闭：按显示意图开启", controller, actions));

                    WriteStatus(fixturePath!, Online() with
                    {
                        CredentialsBlocked = true,
                        AuthenticationFailure = "CredentialsRejected",
                        AuthenticationPersistentState = "CredentialsRejected"
                    });
                    await SettleAsync(controller);
                    records.Add(await RunUnblockAsync("在线且认证暂停：解除入口", actions));

                    // 动作前重验（故事 41）：过期夹具既不提供显示意图，强制意图也必须被拦下。
                    WriteStatus(fixturePath!, Online() with
                    {
                        CredentialsBlocked = true,
                        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                    });
                    await SettleAsync(controller);
                    records.Add(await DriveDisplayedRecoveryAsync("过期夹具：显示层不提供意图", controller, actions));
                    records.Add(await RunRecoveryAsync("过期夹具：强制意图被重验拦下", actions, RecoveryIntent.Disable));
                    records.Add(await RunUnblockAsync("过期夹具：解除入口被重验拦下", actions));

                    // 缺失状态同样是失败关闭。
                    File.Delete(fixturePath!);
                    await SettleAsync(controller);
                    records.Add(await RunRecoveryAsync("缺失状态：强制意图被重验拦下", actions, RecoveryIntent.Enable));
                }
                else
                {
                    await controller.RefreshStatusAsync();
                    await Task.Delay(250);
                    var shot = qa.OverviewShotPath("overview-live");
                    WindowCapture.Save(window, shot);
                    steps.Add(Describe("live", controller, Path.GetFileName(shot)));
                }

                QaReports.Write(qa.OverviewReportPath, Build());
                shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(qa.OverviewReportPath, Build(e.GetType().Name + ": " + e.Message));
                shutdown(1);
            }
        });
    }

    private static async Task SettleAsync(ShellController controller)
    {
        await controller.RefreshStatusAsync();
        await Task.Delay(120);
    }

    private static QaOverviewStep Describe(string state, ShellController controller, string? file)
    {
        if (controller.Content is not OverviewContent overview)
            throw new InvalidOperationException("概览内容未就绪：" + state);
        return new QaOverviewStep(
            state,
            overview.Trusted,
            overview.StatusTitle,
            overview.Auth.Visible,
            overview.Auth.Title,
            overview.Auth.Note,
            overview.Recovery.Label,
            overview.Recovery.Enabled,
            overview.Unblock.Visible,
            overview.Unblock.Enabled,
            overview.Submission.Value,
            overview.Submission.Note,
            file);
    }

    private static async Task<QaOverviewAction> DriveDisplayedRecoveryAsync(string context, ShellController controller, OverviewActions actions)
    {
        var intent = (controller.Content as OverviewContent)?.Recovery.Intent;
        if (intent is null) return new QaOverviewAction(context, "(显示层未提供意图)", false, null);
        return await RunRecoveryAsync(context, actions, intent.Value);
    }

    private static async Task<QaOverviewAction> RunRecoveryAsync(string context, OverviewActions actions, RecoveryIntent intent)
    {
        var expected = OverviewModel.OperationFor(intent).CommandLineArgument();
        var before = LaunchCount();
        var launched = await actions.RequestRecoveryChangeAsync(intent);
        return Record(context, expected, launched, before);
    }

    private static async Task<QaOverviewAction> RunUnblockAsync(string context, OverviewActions actions)
    {
        var before = LaunchCount();
        var launched = await actions.RequestUnblockAsync();
        return Record(context, ElevatedOperationKind.UnblockAuthentication.CommandLineArgument(), launched, before);
    }

    private static QaOverviewAction Record(string context, string expected, bool launched, int before)
    {
        var after = LaunchCount();
        if (launched && after != before + 1) throw new InvalidOperationException("动作声称已发起但未记录命令：" + context);
        if (!launched && after != before) throw new InvalidOperationException("动作未声称发起却记录了命令：" + context);
        string? command = null;
        if (launched)
        {
            lock (launchGate) command = launches[^1];
            if (command != expected) throw new InvalidOperationException($"命令意图不符：期望 {expected}，实际 {command}（{context}）");
        }

        return new QaOverviewAction(context, expected, launched, command);
    }

    private static int LaunchCount()
    {
        lock (launchGate) return launches.Count;
    }

    private static IEnumerable<(string State, StatusSnapshot? Snapshot)> Matrix()
    {
        yield return ("online", Online());
        yield return ("budget-near-limit", OfflinePending() with
        {
            SubmissionBudgetCode = SubmissionBudgetCodes.NearLimit,
            SubmissionRetryCode = nameof(SubmissionRetryDecision.Waiting),
            SubmissionAttempts = 2, SubmissionAttemptMax = 3, SubmissionBudgetExhausted = false,
            SubmissionNextPossibleAt = DateTimeOffset.UtcNow.AddSeconds(90)
        });
        yield return ("budget-exhausted", OfflinePending() with
        {
            SubmissionBudgetCode = SubmissionBudgetCodes.Exhausted,
            SubmissionRetryCode = nameof(SubmissionRetryDecision.Exhausted),
            SubmissionAttempts = 3, SubmissionAttemptMax = 3, SubmissionBudgetExhausted = true
        });
        // 提交待决但校园 Wi-Fi 未连接：复检被跳过，文案必须说明等待网络条件恢复。
        yield return ("budget-exhausted-wifi-down", OfflinePending() with
        {
            Wifi = "未连接",
            SubmissionBudgetCode = SubmissionBudgetCodes.Exhausted,
            SubmissionRetryCode = nameof(SubmissionRetryDecision.Exhausted),
            SubmissionAttempts = 3, SubmissionAttemptMax = 3, SubmissionBudgetExhausted = true
        });
        // 首次尚未提交凭据且外网在线：InternetOnline 不代表存在提交结果复检链。
        yield return ("budget-online-no-pending", Online() with
        {
            SubmissionBudgetCode = SubmissionBudgetCodes.Unused,
            SubmissionRetryCode = nameof(SubmissionRetryDecision.InternetOnline),
            SubmissionAttempts = 0, SubmissionAttemptMax = 3, SubmissionBudgetExhausted = false
        });
        // 第三次提交同时耗尽额度并收到明确密码错误：认证列必须说明拒绝原因，额度耗尽独立呈现。
        yield return ("budget-exhausted-rejected", OfflinePending() with
        {
            SubmissionBudgetCode = SubmissionBudgetCodes.Exhausted,
            SubmissionRetryCode = nameof(SubmissionRetryDecision.Exhausted),
            SubmissionAttempts = 3, SubmissionAttemptMax = 3, SubmissionBudgetExhausted = true,
            AuthenticationFailure = "CredentialsRejected",
            AuthenticationPersistentState = "CredentialsRejected"
        });
        // 第二次提交被绑定拒绝且额度即将用尽：认证列必须说明绑定拒绝，额度事实独立呈现。
        yield return ("budget-near-limit-binding", OfflinePending() with
        {
            SubmissionBudgetCode = SubmissionBudgetCodes.NearLimit,
            SubmissionRetryCode = nameof(SubmissionRetryDecision.Concluded),
            SubmissionAttempts = 2, SubmissionAttemptMax = 3, SubmissionBudgetExhausted = false,
            AuthenticationFailure = "BindingRejected",
            AuthenticationPersistentState = "BindingRejected"
        });
        yield return ("budget-reset", Online() with
        {
            SubmissionBudgetCode = SubmissionBudgetCodes.Reset,
            SubmissionRetryCode = nameof(SubmissionRetryDecision.Authorized),
            SubmissionAttempts = 0, SubmissionAttemptMax = 3, SubmissionBudgetExhausted = false
        });
        yield return ("budget-waiting", OfflinePending() with
        {
            SubmissionBudgetCode = SubmissionBudgetCodes.Available,
            SubmissionRetryCode = nameof(SubmissionRetryDecision.Waiting),
            SubmissionAttempts = 1, SubmissionAttemptMax = 3, SubmissionBudgetExhausted = false,
            SubmissionNextPossibleAt = DateTimeOffset.UtcNow.AddSeconds(120)
        });
        yield return ("budget-other-wifi", Online() with
        {
            State = "已切换其他 Wi-Fi", Wifi = "其他配置", Direct = "未检查",
            SubmissionBudgetCode = SubmissionBudgetCodes.Exhausted,
            SubmissionAttempts = 3, SubmissionAttemptMax = 3, SubmissionBudgetExhausted = true
        });
        yield return ("budget-authorized-paused", OfflinePending() with
        {
            AuthenticationFailure = "CredentialsRejected", AuthenticationPersistentState = "CredentialsRejected",
            SubmissionBudgetCode = SubmissionBudgetCodes.Unused,
            SubmissionRetryCode = nameof(SubmissionRetryDecision.Authorized),
            SubmissionAttempts = 0, SubmissionAttemptMax = 3, SubmissionBudgetExhausted = false
        });
        yield return ("budget-legacy", Online());
        yield return ("online-disabled", Online() with { Enabled = false });
        yield return ("online-suspended", Online() with
        {
            CredentialsBlocked = true,
            AuthenticationFailure = "CredentialsRejected",
            AuthenticationPersistentState = "CredentialsRejected"
        });
        yield return ("offline-suspended", Online() with
        {
            State = "需要处理认证",
            Direct = "外网不可达或未知",
            CredentialsBlocked = true,
            AuthenticationFailure = "CredentialsRejected",
            AuthenticationPersistentState = "CredentialsRejected"
        });
        yield return ("layout-suspected", Online() with { PortalLayoutSuspected = true });
        yield return ("stale", Online() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        yield return ("invalid", Online() with { Tunnel = null! });
        yield return ("missing", null);
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

    private static StatusSnapshot OfflinePending() => Online() with
    {
        State = "需要处理认证",
        Direct = "外网不可达或未知",
        CredentialsBlocked = true,
        AuthenticationFailure = "None",
        AuthenticationPersistentState = "SubmissionPending"
    };
}
