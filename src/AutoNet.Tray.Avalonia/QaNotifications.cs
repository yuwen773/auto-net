using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>一次通知决策证据：场景、受控理由、是否呈现与呈现内容（词表内文案，绝不含状态原文）。</summary>
internal sealed record QaNotificationDecision(
    string Step, string Reason, bool Presented, string? Code, string? Heading, string? Body);

internal sealed record QaNotificationsReport(
    string Mode,
    string Phase,
    string ProcessPath,
    int ProcessId,
    bool Administrator,
    string MutexName,
    string ThemeVariant,
    string ConsumptionFile,
    string ConsumptionContent,
    List<QaNotificationDecision> Decisions,
    string? Error = null);

/// <summary>
/// 关键通知 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=notifications 激活，迁移工单 #10）：
/// 以夹具状态快照手工驱动通知路由（时序确定），逐场景记录「决策 → 是否呈现 → 呈现文案」——
/// 首读不重播历史恢复（56）、运行期恢复提醒一次且同事件不重弹（53、58）、复检只在界面显示（54）、
/// 未知码无动作（60）、改版与暂停提示并存（59）。第二阶段（AUTONET_QA_NOTIFY_PHASE=resume）
/// 在新进程用同一证据目录（同一消费记录）验证：重启后同 payload 不重播（57）且仍暂停时
/// 启动提醒恰好一次（55）。呈现走受控记录器，不弹真实气泡；消费记录经
/// <see cref="NotificationConsumptionStore.Create"/> 落证据目录，绝不触碰本机真实记录。
/// </summary>
internal static class QaNotifications
{
    private static DateTimeOffset FixtureNow => new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    public static void Run(ShellController shell, MainWindow window, NotificationRouter router, List<NotificationAlert> presented, QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var decisions = new List<QaNotificationDecision>();
            var phase = Environment.GetEnvironmentVariable("AUTONET_QA_NOTIFY_PHASE") == "resume" ? "resume" : "select";
            var reportPath = phase == "resume" ? qa.NotificationsResumeReportPath : qa.NotificationsReportPath;

            QaNotificationsReport Build(string? error = null) => new(
                qa.Mode.ToString(),
                phase,
                Environment.ProcessPath ?? "(unknown)",
                Environment.ProcessId,
                Elevation.IsAdministrator,
                Program.MutexName,
                Application.Current?.RequestedThemeVariant?.ToString() ?? "Default",
                NotificationConsumptionStore.FileName,
                ConsumptionContent(qa),
                decisions,
                error);

            try
            {
                TrayHost.ShowWindow(window);
                await Task.Delay(300);

                if (phase == "resume")
                {
                    await ResumeAsync(shell, router, presented, qa, decisions);
                }
                else
                {
                    await SelectAsync(shell, router, presented, qa, decisions);
                }

                QaReports.Write(reportPath, Build());
                shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(reportPath, Build(e.GetType().Name + ": " + e.Message));
                shutdown(1);
            }
        });
    }

    /// <summary>选择阶段场景矩阵：事件生命周期（首读 → 重弹禁止 → 新事件 → 复检/未知码降级 → 并存提示）。</summary>
    private static async Task SelectAsync(ShellController shell, NotificationRouter router, List<NotificationAlert> presented, QaEnvironment qa, List<QaNotificationDecision> decisions)
    {
        var t1 = FixtureNow.AddMinutes(-10);
        var t2 = FixtureNow.AddMinutes(-5);
        var t3 = FixtureNow.AddMinutes(-4);
        var t4 = FixtureNow.AddMinutes(-3);
        var t5 = FixtureNow.AddMinutes(-2);
        var t6 = FixtureNow.AddMinutes(-1);

        // 故事 56：首条可信读取携带的历史恢复只记录不提醒。
        var baseline = Online();
        await DriveAsync("first-reading-no-replay", "first-reading", shell, router, presented, qa, decisions, baseline with
        {
            Notification = new StatusNotification(NotificationCodes.InternetRecovered, t1)
        }, expect: 0);

        // 故事 58：同一事件（同码同刻）在此后的读取中不再弹出。
        await DriveAsync("same-event-not-repeated", "already-consumed", shell, router, presented, qa, decisions, baseline with
        {
            Notification = new StatusNotification(NotificationCodes.InternetRecovered, t1)
        }, expect: 0);

        // 故事 53：运行期新恢复（At 前进）提醒一次，文案受控。
        await DriveAsync("recovery-presented-once", "presented", shell, router, presented, qa, decisions, baseline with
        {
            Notification = new StatusNotification(NotificationCodes.InternetRecovered, t2)
        }, expect: 1);

        // 故事 58：新事件消费后同样不再重弹。
        await DriveAsync("recovery-not-repeated", "already-consumed", shell, router, presented, qa, decisions, baseline with
        {
            Notification = new StatusNotification(NotificationCodes.InternetRecovered, t2)
        }, expect: 0);

        // 故事 54：复检过程绝不在托盘弹出（只在界面显示）。
        await DriveAsync("recheck-interface-only", "interface-only", shell, router, presented, qa, decisions, baseline with
        {
            Notification = new StatusNotification(NotificationCodes.SubmissionRechecking, t3)
        }, expect: 0);

        // 故事 60：未知码无动作、无文案；决策记录不含原文。
        var unknown = await DriveAsync("unknown-code-inert", "unknown-code", shell, router, presented, qa, decisions, baseline with
        {
            Notification = new StatusNotification("BrandNewCode", t4)
        }, expect: 0);
        Require(unknown.Code is null && unknown.Heading is null && unknown.Body is null,
            "未知码的决策证据不得携带原文");

        // 故事 59：改版提示弹出；随后的通用暂停提示独立弹出，不覆盖改版事实。
        // （“同轮”单槽不覆盖由服务端 notifiedThisCycle 保证；托盘侧证据为两条具体文案并存。）
        await DriveAsync("layout-presented", "presented", shell, router, presented, qa, decisions, baseline with
        {
            Notification = new StatusNotification(NotificationCodes.PortalLayoutChanged, t5)
        }, expect: 1);
        await DriveAsync("pause-not-overriding-layout", "presented", shell, router, presented, qa, decisions, baseline with
        {
            CredentialsBlocked = true,
            Notification = new StatusNotification(NotificationCodes.AuthenticationPaused, t6)
        }, expect: 1);
        Require(presented[^1].Heading != presented[^2].Heading,
            "暂停提示与改版提示必须使用不同文案（故事 59）");
    }

    /// <summary>重启阶段：同一证据目录（同一消费记录）下验证不重播与启动暂停提醒。</summary>
    private static async Task ResumeAsync(ShellController shell, NotificationRouter router, List<NotificationAlert> presented, QaEnvironment qa, List<QaNotificationDecision> decisions)
    {
        var t1 = FixtureNow.AddMinutes(-10);
        var t2 = FixtureNow.AddMinutes(-5);

        // 故事 57：托盘重启后，消费记录里的恢复事件不得再次提醒（首读 + 持久记录双保险）。
        var baseline = Online();
        await DriveAsync("resume-no-replay", "first-reading", shell, router, presented, qa, decisions, baseline with
        {
            Notification = new StatusNotification(NotificationCodes.InternetRecovered, t2)
        }, expect: 0);

        // 故事 55：重启时认证仍暂停（payload 为旧暂停事件）→ 启动暂停提醒恰好一次。
        await DriveAsync("resume-pause-reminded-once", "startup-pause", shell, router, presented, qa, decisions, baseline with
        {
            CredentialsBlocked = true,
            Notification = new StatusNotification(NotificationCodes.AuthenticationPaused, t1)
        }, expect: 1);

        // 会话内不再重复启动提醒。
        await DriveAsync("resume-pause-not-repeated", "already-consumed", shell, router, presented, qa, decisions, baseline with
        {
            CredentialsBlocked = true,
            Notification = new StatusNotification(NotificationCodes.AuthenticationPaused, t1)
        }, expect: 0);
    }

    /// <summary>
    /// 驱动一步：写夹具快照（供壳截图与审计一致）→ 手工把同一快照交给路由（QA 模式不订阅管道，
    /// 时序完全确定）→ 记录决策。presented 增量必须等于预期，否则场景失败。
    /// 理由标签由场景定义点直接携带（受控词表，与路由规则一一对应，不出现状态原文）。
    /// </summary>
    private static async Task<QaNotificationDecision> DriveAsync(
        string step, string reason, ShellController shell, NotificationRouter router, List<NotificationAlert> presented,
        QaEnvironment qa, List<QaNotificationDecision> decisions, StatusSnapshot snapshot, int expect)
    {
        var before = presented.Count;
        Storage.AtomicWrite(StatusFixturePath(qa), JsonSerializer.Serialize(snapshot, Storage.Json));
        var reading = Trusted(snapshot);
        router.Observe(reading);
        var shown = presented.Count - before;
        Require(shown == expect, $"{step}：呈现次数 {shown} ≠ 预期 {expect}");

        // 壳侧照常刷新（底部状态栏与概览页跟随夹具），证明通知消费不影响界面降级语义。
        await shell.RefreshStatusAsync();

        var alert = shown > 0 ? presented[^1] : null;
        var decision = new QaNotificationDecision(
            step,
            reason,
            shown > 0,
            alert?.Code,
            alert?.Heading,
            alert?.Body);
        decisions.Add(decision);
        return decision;
    }

    private static string ConsumptionContent(QaEnvironment qa)
    {
        var path = Path.Combine(qa.Directory, NotificationConsumptionStore.FileName);
        return File.Exists(path) ? File.ReadAllText(path) : "(absent)";
    }

    private static string StatusFixturePath(QaEnvironment qa) => Path.Combine(qa.Directory, "status.json");

    private static StatusReading Trusted(StatusSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        return new StatusReading(snapshot, NetworkDisplayMap.Map(snapshot, TimeSpan.Zero), true, true, TimeSpan.Zero, now);
    }

    private static StatusSnapshot Online() => new()
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Enabled = true,
        State = "校园网在线",
        Wifi = "已连接",
        Direct = "外网可达",
        Portal = "未查询（外网检查已通过）",
        Tunnel = "系统路径可达，出口未核验",
        Uu = "未核验"
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
