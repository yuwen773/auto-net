using System.Text.Json;
using AutoNet.Core;
using Avalonia;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>一次浏览器启动记录：场景（含受控失败标记）、逐字 URL 与时刻。URL 只有固定门户根地址。</summary>
internal sealed record QaPortalLaunch(string Step, string Url, string At);

/// <summary>一步评估证据：场景、受控理由、结果枚举与启动增量。</summary>
internal sealed record QaPortalDecision(string Step, string Reason, string Outcome, int LaunchDelta);

internal sealed record QaPortalReport(
    string Mode,
    string Phase,
    string ProcessPath,
    int ProcessId,
    bool Administrator,
    string MutexName,
    string ThemeVariant,
    string StoreFile,
    string StoreContent,
    string NotificationStoreFile,
    bool NotificationStoreSeparate,
    List<QaPortalLaunch> Launches,
    List<QaPortalDecision> Decisions,
    string? Error = null);

/// <summary>
/// 浏览器兜底开页 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=portal-open 激活，迁移工单 #11）：
/// 以夹具快照手工驱动开页控制器（时序确定；本模式不订阅状态管道），逐场景记录
/// 「评估结果 → 是否启动（受控记录器）→ 启动增量」——首次授予立即开页且 URL 逐字为允许根址
/// （61–63）、同一授予会话内与跨重启都只开一次（64）、服务换发授予不绕本地冷却（65）、
/// 手动开页计入消费与冷却（66）、冷却到期不循环重开（67）、请求消失/不可信不开页（68）、
/// 启动失败受控且不在轮询中无限重试（69）。第二阶段（AUTONET_QA_PORTAL_PHASE=resume）在同一
/// 证据目录（同一消费记录）新进程验证跨重启语义；带冷却播种的场景以证据目录里的记录文件 +
/// 新会话（新 <see cref="PortalPageOpener"/>）驱动，等价于「带该持久状态的托盘会话」。
/// 浏览器启动走受控记录器，不弹真实浏览器；消费记录经 <see cref="PortalOpenStore.Create"/> 落
/// 证据目录，绝不触碰本机真实记录。
/// </summary>
internal static class QaPortal
{
    private static readonly object LaunchGate = new();
    private static readonly List<QaPortalLaunch> Launches = [];

    /// <summary>当前场景的浏览器启动行为：success = 打开成功；fail = 返回受控失败类型。</summary>
    private static string launchBehavior = "success";

    private static DateTimeOffset FixtureNow => new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    /// <summary>浏览器启动记录适配器（QA 专用）：记录 URL 并按当前场景返回受控结果。</summary>
    public static string? RecordLaunch(string url)
    {
        lock (LaunchGate)
        {
            Launches.Add(new QaPortalLaunch(launchBehavior, url, QaReports.Timestamp()));
            return launchBehavior == "fail" ? "StubbedLaunchException" : null;
        }
    }

    public static void Run(ShellController shell, MainWindow window, StatusPipe pipe, PortalPageOpener opener, QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var decisions = new List<QaPortalDecision>();
            var phase = Environment.GetEnvironmentVariable("AUTONET_QA_PORTAL_PHASE") == "resume" ? "resume" : "select";
            var reportPath = phase == "resume" ? qa.PortalOpenResumeReportPath : qa.PortalOpenReportPath;

            QaPortalReport Build(string? error = null)
            {
                lock (LaunchGate)
                {
                    return new QaPortalReport(
                        qa.Mode.ToString(),
                        phase,
                        Environment.ProcessPath ?? "(unknown)",
                        Environment.ProcessId,
                        Elevation.IsAdministrator,
                        Program.MutexName,
                        Application.Current?.RequestedThemeVariant?.ToString() ?? "Default",
                        PortalOpenStore.FileName,
                        StoreContent(qa),
                        NotificationConsumptionStore.FileName,
                        !string.Equals(PortalOpenStore.FileName, NotificationConsumptionStore.FileName, StringComparison.Ordinal),
                        [.. Launches],
                        decisions,
                        error);
                }
            }

            try
            {
                TrayHost.ShowWindow(window);
                await Task.Delay(300);
                if (phase == "resume") await ResumeAsync(shell, pipe, opener, qa, decisions);
                else await SelectAsync(shell, window, pipe, opener, qa, decisions);
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

    /// <summary>选择阶段：授予生命周期（首次立即 → 去重 → 换发冷却 → 失效 → 手动 → 启动失败 → 手动重试）。</summary>
    private static async Task SelectAsync(ShellController shell, MainWindow window, StatusPipe pipe, PortalPageOpener opener, QaEnvironment qa, List<QaPortalDecision> decisions)
    {
        var grant1 = FixtureNow.AddMinutes(10); // 第一次授予的 NotBefore（服务戳 = 授予 + 10 分钟）
        var grant2 = FixtureNow.AddMinutes(11); // 服务重启换发的第二个授予
        var grant3 = FixtureNow.AddMinutes(12);

        // 故事 61–63：首次有效授予立即开页；URL 逐字为允许根址（启动委托只收 URL，无表单无凭据）。
        await DriveAsync("first-grant-opens-immediately", "first-grant", shell, opener, qa, decisions,
            Grant(grant1), expectDelta: 1, expect: PortalOpenOutcome.Opened);
        Require(Launches[^1].Url == PortalPageOpener.PortalPageUrl, "开页 URL 不是固定允许的门户根地址");
        Require(!Launches[^1].Url.Contains('?') && !Launches[^1].Url.Contains(';') && !Launches[^1].Url.Contains('@'),
            "URL 携带了查询、userinfo 或其他额外内容");

        // 故事 64（会话内）：同一授予不再自动开页。
        await DriveAsync("same-grant-dedup", "already-consumed", shell, opener, qa, decisions,
            Grant(grant1), expectDelta: 0, expect: PortalOpenOutcome.AlreadyConsumed);

        // 故事 65：服务重启换发的新授予也必须先等本地冷却（首次开页刚发生）。
        await DriveAsync("regrant-within-cooldown-deferred", "cooldown-active", shell, opener, qa, decisions,
            Grant(grant2), expectDelta: 0, expect: PortalOpenOutcome.CooldownActive);

        // 故事 68：请求消失（联网恢复后服务撤旗的表现形态）→ 不开页；恢复授予后仍受冷却约束。
        await DriveAsync("request-gone-no-open", "request-gone", shell, opener, qa, decisions,
            Online(), expectDelta: 0, expect: PortalOpenOutcome.NotEligible);
        await DriveAsync("request-restored-still-cooling", "cooldown-active", shell, opener, qa, decisions,
            Grant(grant2), expectDelta: 0, expect: PortalOpenOutcome.CooldownActive);

        // 故事 68：不可信读取不参与开页决策。
        await DriveUntrustedAsync("untrusted-no-open", "untrusted", shell, opener, qa, decisions, Grant(grant2));

        // 故事 66（前置）：请求消失时手动开页也不可用（无可执行授予）。
        WriteFixture(qa, Online());
        var idleBefore = LaunchCount();
        var manualIdle = await opener.OpenManuallyAsync();
        Require(manualIdle == PortalOpenOutcome.NotEligible, "无授予时手动开页应不可执行");
        Require(LaunchCount() - idleBefore == 0, "无授予的手动开页不应启动浏览器");
        decisions.Add(new QaPortalDecision("manual-open-not-eligible", "request-gone", manualIdle.ToString(), 0));

        // 故事 66：手动开页计入消费与冷却——尽管 grant2 从未自动开过页，手动打开后它即被消费。
        await DriveManualAsync("manual-open-counts", "manual-open", shell, opener, qa, decisions,
            Grant(grant2), expect: PortalOpenOutcome.Opened);
        await DriveAsync("manual-open-blocks-auto-reopen", "already-consumed", shell, opener, qa, decisions,
            Grant(grant2), expectDelta: 0, expect: PortalOpenOutcome.AlreadyConsumed);

        // 故事 69：启动失败只给出受控错误；同一授予的后续评估不再启动浏览器（不无限重试）。
        // 以「冷却已满足的播种会话」驱动：手动开页刚写入的冷却会拦下新授予，先播种绕开。
        launchBehavior = "fail";
        var failing = await DriveSeededAsync("launch-failure-controlled", "launch-failed", shell, pipe, opener, qa, decisions,
            Grant(grant3), seededLastOpenAt: DateTimeOffset.UtcNow.AddMinutes(-11), expectDelta: 1, expect: PortalOpenOutcome.LaunchFailed);
        // 同一会话再评估：授予已消费，轮询不再启动浏览器。
        var beforeRetry = LaunchCount();
        var notRetried = await failing.EvaluateAsync(Trusted(Grant(grant3)));
        Require(notRetried == PortalOpenOutcome.AlreadyConsumed && LaunchCount() - beforeRetry == 0,
            "轮询重试了失败的浏览器启动");
        decisions.Add(new QaPortalDecision("launch-failure-not-retried", "already-consumed", notRetried.ToString(), 0));
        launchBehavior = "success";

        // 故事 69：失败后手动重试入口仍然可用（手动路径不受去重限制，重新计入消费与冷却）。
        await DriveManualAsync("manual-retry-after-failure", "manual-open", shell, failing, qa, decisions,
            Grant(grant3), expect: PortalOpenOutcome.Opened);

        // 概览投影证据：兜底开页卡出现在概览页（fixture 授予 + shell 刷新），真实窗口捕获。
        WriteFixture(qa, Grant(grant3));
        await shell.RefreshStatusAsync();
        await Task.Delay(200);
        WindowCapture.Save(window, qa.PortalOpenShotPath("overview-portal-card"));
    }

    /// <summary>重启阶段：同一证据目录（同一消费记录）内新进程验证跨重启语义。</summary>
    private static async Task ResumeAsync(ShellController shell, StatusPipe pipe, PortalPageOpener opener, QaEnvironment qa, List<QaPortalDecision> decisions)
    {
        var grant3 = FixtureNow.AddMinutes(12); // 选择阶段已被消费的授予（含失败后手动重试）
        var grant4 = FixtureNow.AddMinutes(13);
        var grant5 = FixtureNow.AddMinutes(14);

        // 故事 64（跨重启）：已消费授予重启后不再自动开页，即使本地冷却已满足。
        await DriveAsync("resume-same-grant-dedup", "already-consumed", shell, opener, qa, decisions,
            Grant(grant3), expectDelta: 0, expect: PortalOpenOutcome.AlreadyConsumed);

        // 故事 65（跨重启）：换发授予先被持久冷却拦下（选择阶段最后一次开页距今不足 10 分钟）。
        // 播种相对真实时钟（UtcNow）：冷却「未过期」形态；以证据目录持久记录为准（不覆盖内存态）。
        Require(new PortalOpenStore(StorePath(qa)).TrySave(
            new PortalOpenStore(StorePath(qa)).Load().Grants, DateTimeOffset.UtcNow), "冷却播种失败");
        await DriveAsync("resume-cooldown-cross-grant", "cooldown-active", shell, opener, qa, decisions,
            Grant(grant4), expectDelta: 0, expect: PortalOpenOutcome.CooldownActive);

        // ADR-0008：新的有效授予 + 冷却已满足 → 恰好自动开一次；同一会话再评估不再开。
        var opened = await DriveSeededAsync("resume-expired-cooldown-opens-once", "cooldown-satisfied", shell, pipe, opener, qa, decisions,
            Grant(grant4), seededLastOpenAt: DateTimeOffset.UtcNow.AddMinutes(-11), expectDelta: 1, expect: PortalOpenOutcome.Opened);
        var onceBefore = LaunchCount();
        var onceOnly = await opened.EvaluateAsync(Trusted(Grant(grant4)));
        Require(onceOnly == PortalOpenOutcome.AlreadyConsumed && LaunchCount() - onceBefore == 0, "新授予重复开页");
        decisions.Add(new QaPortalDecision("resume-open-once-only", "already-consumed", onceOnly.ToString(), 0));
        await shell.RefreshStatusAsync();

        // 故事 67：冷却到期本身不循环重开——同一已消费授予在冷却过期后依然不开。
        await DriveSeededAsync("resume-expiry-no-loop", "already-consumed", shell, pipe, opener, qa, decisions,
            Grant(grant4), seededLastOpenAt: DateTimeOffset.UtcNow.AddMinutes(-11), expectDelta: 0, expect: PortalOpenOutcome.AlreadyConsumed);

        // 决策 11（时间异常）：LastOpenAt 在未来（时钟回拨形态）→ 冷却视为未满足，绝不提前重开。
        await DriveSeededAsync("resume-time-anomaly-no-reopen", "cooldown-active", shell, pipe, opener, qa, decisions,
            Grant(grant5), seededLastOpenAt: DateTimeOffset.UtcNow.AddHours(1), expectDelta: 0, expect: PortalOpenOutcome.CooldownActive);
    }

    /// <summary>驱动一步自动评估：写夹具 → 记录启动基线 → 评估 → 断言结果与启动增量。</summary>
    private static async Task DriveAsync(
        string step, string reason, ShellController shell, PortalPageOpener opener, QaEnvironment qa,
        List<QaPortalDecision> decisions, StatusSnapshot snapshot, int expectDelta, PortalOpenOutcome expect)
    {
        WriteFixture(qa, snapshot);
        var before = LaunchCount();
        var outcome = await opener.EvaluateAsync(Trusted(snapshot));
        var delta = LaunchCount() - before;
        Require(outcome == expect, $"{step}：结果 {outcome} ≠ 预期 {expect}");
        Require(delta == expectDelta, $"{step}：启动增量 {delta} ≠ 预期 {expectDelta}");
        decisions.Add(new QaPortalDecision(step, reason, outcome.ToString(), delta));
        await shell.RefreshStatusAsync();
    }

    /// <summary>
    /// 驱动一步带冷却播种的自动评估：先把证据目录记录文件的 LastOpenAt 改写为指定时刻
    /// （模拟时间流逝或时钟异常），再以「读到该持久状态的新会话」评估——等价于托盘在该
    /// 持久状态下的下一次状态读取。
    /// </summary>
    private static async Task<PortalPageOpener> DriveSeededAsync(
        string step, string reason, ShellController shell, StatusPipe pipe, PortalPageOpener opener, QaEnvironment qa,
        List<QaPortalDecision> decisions, StatusSnapshot snapshot, DateTimeOffset seededLastOpenAt, int expectDelta, PortalOpenOutcome expect)
    {
        WriteFixture(qa, snapshot);
        var store = new PortalOpenStore(StorePath(qa));
        // 播种以证据目录的持久记录为准（只改写 LastOpenAt），绝不覆盖前序会话消费的授予键。
        Require(store.TrySave(store.Load().Grants, seededLastOpenAt), "冷却播种失败");
        var seededOpener = new PortalPageOpener(store, pipe.RefreshAsync, RecordLaunch);
        var before = LaunchCount();
        var outcome = await seededOpener.EvaluateAsync(Trusted(snapshot));
        var delta = LaunchCount() - before;
        Require(outcome == expect, $"{step}：结果 {outcome} ≠ 预期 {expect}");
        Require(delta == expectDelta, $"{step}：启动增量 {delta} ≠ 预期 {expectDelta}");
        decisions.Add(new QaPortalDecision(step, reason, outcome.ToString(), delta));
        await shell.RefreshStatusAsync();
        return seededOpener;
    }

    /// <summary>驱动一步不可信评估：快照带两小时前的时间戳（过期形态），读取整体标记不可信。</summary>
    private static async Task DriveUntrustedAsync(
        string step, string reason, ShellController shell, PortalPageOpener opener, QaEnvironment qa,
        List<QaPortalDecision> decisions, StatusSnapshot snapshot)
    {
        WriteFixture(qa, snapshot);
        var before = LaunchCount();
        var untrusted = new StatusReading(snapshot, NetworkDisplayMap.Map(snapshot, TimeSpan.FromHours(2)), true, false, TimeSpan.FromHours(2), DateTimeOffset.UtcNow);
        var outcome = await opener.EvaluateAsync(untrusted);
        var delta = LaunchCount() - before;
        Require(outcome == PortalOpenOutcome.NotEligible && delta == 0, $"{step}：不可信读取不应开页");
        decisions.Add(new QaPortalDecision(step, reason, outcome.ToString(), delta));
        await shell.RefreshStatusAsync();
    }

    /// <summary>驱动一步手动开页：写夹具（重读经状态管道返回同一夹具）→ 手动触发 → 断言结果与增量。</summary>
    private static async Task DriveManualAsync(
        string step, string reason, ShellController shell, PortalPageOpener opener, QaEnvironment qa,
        List<QaPortalDecision> decisions, StatusSnapshot snapshot, PortalOpenOutcome expect)
    {
        WriteFixture(qa, snapshot);
        var before = LaunchCount();
        var outcome = await opener.OpenManuallyAsync();
        var delta = LaunchCount() - before;
        Require(outcome == expect, $"{step}：结果 {outcome} ≠ 预期 {expect}");
        Require(delta == 1, $"{step}：手动开页应恰好启动一次");
        decisions.Add(new QaPortalDecision(step, reason, outcome.ToString(), delta));
        await shell.RefreshStatusAsync();
    }

    private static int LaunchCount() { lock (LaunchGate) return Launches.Count; }

    private static string StorePath(QaEnvironment qa) => Path.Combine(qa.Directory, PortalOpenStore.FileName);

    private static string StoreContent(QaEnvironment qa) => File.Exists(StorePath(qa)) ? File.ReadAllText(StorePath(qa)) : "(absent)";

    private static void WriteFixture(QaEnvironment qa, StatusSnapshot snapshot) =>
        File.WriteAllText(Path.Combine(qa.Directory, "status.json"), JsonSerializer.Serialize(snapshot, Storage.Json));

    private static StatusSnapshot Grant(DateTimeOffset notBefore) => Online() with
    {
        BrowserFallbackRequested = true,
        BrowserFallbackNotBefore = notBefore,
        Notification = new StatusNotification(NotificationCodes.BrowserFallbackRequested, notBefore.AddMinutes(-10))
    };

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

    private static StatusReading Trusted(StatusSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        return new StatusReading(snapshot, NetworkDisplayMap.Map(snapshot, TimeSpan.Zero), true, true, TimeSpan.Zero, now);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
