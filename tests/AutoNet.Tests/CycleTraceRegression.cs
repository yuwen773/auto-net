using System.Text.Json;
using AutoNet.Core;
using AutoNet.Service;

internal static class CycleTraceRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("轮次计时：阶段聚合、排名与摘要只含阶段名和毫秒", () =>
        {
            var clock = new ManualClock();
            var trace = new CycleTrace(clock);
            trace.Measure("WifiRead", () => clock.Advance(TimeSpan.FromMilliseconds(40)));
            trace.Measure("SettingsRead", () => clock.Advance(TimeSpan.FromMilliseconds(700)));
            trace.Measure("WifiRead", () => clock.Advance(TimeSpan.FromMilliseconds(20)));
            var slowest = trace.Slowest;
            Require(slowest is not null && slowest.Value.Name == "SettingsRead" && slowest.Value.Count == 1
                && Math.Abs(slowest.Value.Milliseconds - 700) < 0.01);
            Require(trace.Summary() == "SettingsRead=700ms; WifiRead=60ms×2");
            Require(Math.Abs(trace.TotalMilliseconds - 760) < 0.01);
            return Task.CompletedTask;
        }),
        ("轮次计时：异步阶段同样计入且异常不丢失耗时", async () =>
        {
            var clock = new ManualClock();
            var trace = new CycleTrace(clock);
            await trace.MeasureAsync("PortalInspection", async () =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(300));
                await Task.CompletedTask;
            });
            var threw = false;
            try
            {
                await trace.MeasureAsync("InternetProbe", () =>
                {
                    clock.Advance(TimeSpan.FromMilliseconds(200));
                    throw new InvalidOperationException("fixture-secret");
                });
            }
            catch (InvalidOperationException) { threw = true; }
            Require(threw);
            Require(trace.Summary() == "PortalInspection=300ms; InternetProbe=200ms");
            Require(!trace.Summary().Contains("fixture-secret"));
        }),
        ("轮次计时：慢轮次只写一条 SlowCycle，慢阶段写有界 SlowStep", async () =>
        {
            var slow = new MonitorFixture();
            var slowClock = new ManualClock();
            var slowTrace = new CycleTrace(slowClock);
            slowTrace.Measure("WifiRead", () => slowClock.Advance(TimeSpan.FromMilliseconds(600)));
            slowTrace.Measure("PortalInspection", () => slowClock.Advance(TimeSpan.FromMilliseconds(4500)));
            slowTrace.Flush(new RecoveryJournal(slow, slowClock), slowStep: 500, slowCycle: 5000);
            var slowCycle = slow.Logs.Where(e => e.EventCode == "SlowCycle").ToArray();
            Require(slowCycle.Length == 1 && slow.Logs.All(e => e.EventCode != "SlowStep"));
            Require(slowCycle[0].Diagnostics is { Stage: "Cycle", Outcome: "Slow", ElapsedMilliseconds: >= 5000 });
            Require(slowCycle[0].Detail.Contains("PortalInspection") && slowCycle[0].Detail.Contains("WifiRead"));
            Require(!slowCycle[0].Detail.Contains("fixture-secret"));

            var steps = new MonitorFixture();
            var stepClock = new ManualClock();
            var stepTrace = new CycleTrace(stepClock);
            stepTrace.Measure("WifiRead", () => stepClock.Advance(TimeSpan.FromMilliseconds(600)));
            stepTrace.Measure("StatusWrite", () => stepClock.Advance(TimeSpan.FromMilliseconds(10)));
            stepTrace.Flush(new RecoveryJournal(steps, stepClock), slowStep: 500, slowCycle: 5000);
            var slowSteps = steps.Logs.Where(e => e.EventCode == "SlowStep").ToArray();
            Require(slowSteps.Length == 1 && slowSteps[0].Diagnostics is { Stage: "WifiRead", Outcome: "Slow" });
            Require(steps.Logs.All(e => e.EventCode != "SlowCycle"));

            slowTrace.Flush(new RecoveryJournal(slow, slowClock), slowStep: 500, slowCycle: 5000);
            Require(slow.Logs.Count(e => e.EventCode == "SlowCycle") == 1);
            await Task.CompletedTask;
        }),
        ("轮次计时：日志写入失败时冲刷不抛出，也不掩盖阶段异常", async () =>
        {
            var fixture = new MonitorFixture { FailLog = true };
            var trace = new CycleTrace(fixture.Clock);
            trace.Measure("WifiRead", () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(600)));
            // 冲刷本身不得抛出：它位于轮次的 finally，不能掩盖正在传播的原始异常。
            trace.Flush(new RecoveryJournal(fixture, fixture.Clock), slowStep: 500, slowCycle: 5000);

            var threw = false;
            try
            {
                await trace.MeasureAsync("PortalInspection", () =>
                {
                    fixture.Clock.Advance(TimeSpan.FromMilliseconds(10));
                    throw new InvalidOperationException("fixture-original");
                });
            }
            catch (InvalidOperationException e) { threw = e.Message == "fixture-original"; }
            finally { trace.Flush(new RecoveryJournal(fixture, fixture.Clock)); }
            Require(threw);
        }),
        ("轮次计时：计时字段随状态实际写盘", async () =>
        {
            var fixture = new MonitorFixture();
            fixture.Http.Online = true;
            fixture.BeforeWifiRead = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(600));
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            var returned = await worker.RunOnceAsync(default);
            var written = fixture.LastStatus;
            Require(written is not null, "轮次未写入状态");
            Require(written!.CycleMilliseconds is not null && written.CycleMilliseconds >= 600,
                "写盘状态缺少本轮耗时");
            Require(written.SlowestStage == "WifiRead" && written.SlowestStageMilliseconds >= 600,
                "写盘状态缺少最慢阶段");
            Require(written.CycleMilliseconds == returned.CycleMilliseconds
                && written.SlowestStage == returned.SlowestStage
                && written.SlowestStageMilliseconds == returned.SlowestStageMilliseconds,
                "写盘与返回的计时字段不一致");
        }),
        ("轮次计时：持续慢阶段跨轮次限流且恶化时立即补记", () =>
        {
            var clock = new ManualClock();
            var limiter = new SlowStepLimiter(clock, TimeSpan.FromMinutes(5));
            Require(limiter.ShouldEmit("WifiRead", 600), "首次慢阶段未记录");
            clock.Advance(TimeSpan.FromSeconds(2));
            Require(!limiter.ShouldEmit("WifiRead", 610), "间隔内重复慢阶段未被限流");
            clock.Advance(TimeSpan.FromSeconds(2));
            Require(!limiter.ShouldEmit("WifiRead", 620), "间隔内重复慢阶段未被限流");
            Require(limiter.ShouldEmit("WifiRead", 1300), "显著恶化未立即补记");
            Require(!limiter.ShouldEmit("WifiRead", 1350), "恶化后间隔内仍重复记录");
            clock.Advance(TimeSpan.FromMinutes(5));
            Require(limiter.ShouldEmit("WifiRead", 1350), "到达重复间隔后未再记录");
            Require(limiter.ShouldEmit("PortalInspection", 700), "不同慢阶段被错误抑制");
            return Task.CompletedTask;
        }),
        ("轮次计时：慢阶段交替出现时各自独立限流", () =>
        {
            var clock = new ManualClock();
            var limiter = new SlowStepLimiter(clock, TimeSpan.FromMinutes(5));
            Require(limiter.ShouldEmit("WifiRead", 600), "首次 WifiRead 未记录");
            clock.Advance(TimeSpan.FromSeconds(2));
            Require(limiter.ShouldEmit("InternetProbe", 700), "首次 InternetProbe 未记录");
            clock.Advance(TimeSpan.FromSeconds(2));
            Require(!limiter.ShouldEmit("WifiRead", 610), "交替场景下 WifiRead 未被限流");
            Require(!limiter.ShouldEmit("InternetProbe", 710), "交替场景下 InternetProbe 未被限流");

            clock.Advance(TimeSpan.FromSeconds(2));
            Require(limiter.ShouldEmit("WifiRead", 1300), "交替场景下显著恶化未立即补记");
            Require(!limiter.ShouldEmit("InternetProbe", 710), "补记一个阶段后另一个阶段的窗口被重置");

            // InternetProbe 的重复间隔先到，WifiRead 的间隔从恶化时刻重新起算，二者互不影响。
            clock.Advance(TimeSpan.FromSeconds(297));
            Require(limiter.ShouldEmit("InternetProbe", 720), "到达重复间隔后未再记录");
            Require(!limiter.ShouldEmit("WifiRead", 605), "阶段窗口被其它阶段的记录重置");
            return Task.CompletedTask;
        }),
        ("轮次计时：持续慢阶段不每轮重复写日志", async () =>
        {
            var fixture = new MonitorFixture();
            fixture.Http.Online = true;
            fixture.BeforeWifiRead = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(600));
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            for (var round = 0; round < 5; round++)
            {
                await worker.RunOnceAsync(default);
                fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            }
            Require(fixture.Logs.Count(e => e.EventCode == "SlowStep") == 1,
                "持续慢阶段每轮都写日志：" + fixture.Logs.Count(e => e.EventCode == "SlowStep"));
        }),
        ("轮次计时：监测轮次把最慢阶段投影到状态且不产生虚假慢事件", async () =>
        {
            var fixture = new MonitorFixture();
            fixture.Http.Online = true;
            fixture.BeforeWifiRead = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(600));
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            var status = await worker.RunOnceAsync(default);
            Require(status.SlowestStage == "WifiRead" && status.SlowestStageMilliseconds >= 600);
            Require(status.CycleMilliseconds >= 600);
            Require(fixture.Logs.Any(e => e.EventCode == "SlowStep" && e.Diagnostics?.Stage == "WifiRead"));
            Require(fixture.Logs.All(e => e.EventCode != "SlowCycle"));
            Require(fixture.Logs.All(e => !e.Detail.Contains("fixture-secret")));

            var quiet = new MonitorFixture();
            quiet.Http.Online = true;
            using var quietWorker = new MonitorWorker(quiet, quiet.Clock);
            var quietStatus = await quietWorker.RunOnceAsync(default);
            Require(quiet.Logs.All(e => e.EventCode is not ("SlowCycle" or "SlowStep")));
            Require(quietStatus.CycleMilliseconds is not null);
            await Task.CompletedTask;
        }),
        ("轮次计时：状态附加字段可被旧读取方忽略且旧状态仍可读", () =>
        {
            var current = new StatusSnapshot
            {
                UpdatedAt = DateTimeOffset.UtcNow, State = "正常", Wifi = "已连接", Direct = "外网可达", Portal = "已登录",
                Tunnel = "未知", Uu = "会话待实测", AuthenticationStage = "Idle", AuthenticationFailure = "None",
                AuthenticationPersistentState = "Clear", CycleMilliseconds = 1234.5, SlowestStage = "PortalInspection",
                SlowestStageMilliseconds = 900
            };
            var json = JsonSerializer.Serialize(current, Storage.Json);
            Require(json.Contains("CycleMilliseconds") && json.Contains("SlowestStage"));
            var parsed = StatusSnapshotReader.Parse(json);
            Require(parsed is { CycleMilliseconds: 1234.5, SlowestStage: "PortalInspection", SlowestStageMilliseconds: 900 });

            var legacy = StatusSnapshotReader.Parse("{\"UpdatedAt\":\"2026-10-10T00:00:00+00:00\",\"State\":\"正常\","
                + "\"Wifi\":\"已连接\",\"Direct\":\"外网可达\",\"Portal\":\"已登录\",\"Tunnel\":\"未知\",\"Uu\":\"会话待实测\","
                + "\"AuthenticationStage\":\"Idle\",\"AuthenticationFailure\":\"None\",\"AuthenticationPersistentState\":\"Clear\","
                + "\"Enabled\":true}");
            Require(legacy is { CycleMilliseconds: null, SlowestStage: "", SlowestStageMilliseconds: null });
            return Task.CompletedTask;
        })
    ];

    private static void Require(bool condition) { if (!condition) throw new Exception("轮次计时行为不符合预期"); }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("轮次计时行为不符合预期：" + message);
    }
}
