using AutoNet.Core;
using AutoNet.Service;
using System.Diagnostics;

internal static class SubmissionRetryRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("共享额度：托盘重启后活动辅助进程仍独占通道启动", () => WithStore((_, store, directory) =>
        {
            using var first = store.TryAcquireChannelLaunch();
            Require(first is not null, "首次通道启动未取得锁");
            using var second = new SafetyStateStore(directory).TryAcquireChannelLaunch();
            Require(second is null, "独立进程可重复启动活动通道");
        })),
        ("共享额度：启动锁占用时可探测且释放后可重新启动", () => WithStore((_, store, directory) =>
        {
            Require(!store.IsChannelLaunchActive(), "无锁时被误判为忙");
            using (var first = store.TryAcquireChannelLaunch())
            {
                Require(first is not null, "首次启动未取得锁");
                Require(store.IsChannelLaunchActive(), "持有启动锁时未探测到忙");
                Require(new SafetyStateStore(directory).IsChannelLaunchActive(), "独立进程未探测到忙");
            }
            Require(!store.IsChannelLaunchActive(), "释放启动锁后仍报告忙");
            using var again = store.TryAcquireChannelLaunch();
            Require(again is not null, "释放后无法重新取得启动锁");
        })),
        ("共享额度：浏览器退出但辅助进程仍在时不能接续", () => WithStore((fixture, store, _) =>
        {
            using var session = new ChannelJobRegression.Fixture();
            using var browser = Process.GetProcessById(session.BrowserId);
            var block = new AuthenticationBlock(PortalFailure.InteractionRequired)
                { Attempts = 3, Revision = Guid.NewGuid(), ChannelFallbackCause = PortalFailure.MissingGatewayParameters };
            store.BlockAuthentication(block);
            using (var lease = store.TryBeginChannelSubmission(block, null, PortalFailure.MissingGatewayParameters,
                new SubmissionRetryPolicy(), fixture.Clock.GetUtcNow(), TimeSpan.Zero))
                Require(lease is not null && store.TryRegisterChannelSession(lease, browser.Id, session.OwnerId, session.Name), "未登记会话");
            browser.Kill();
            browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            using var execution = store.TryAcquireSubmissionExecution();
            Require(execution is null, "浏览器退出后仍运行的辅助进程被忽略");
        })),
        ("共享额度：会话登记后设置变化不得提交", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.GatewayLocation = "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254";
            store.BlockAuthentication(new AuthenticationBlock(PortalFailure.InteractionRequired)
                { Attempts = 3, Revision = Guid.NewGuid(), ChannelFallbackCause = PortalFailure.MissingGatewayParameters });
            using var browser = new FixtureChannelBrowser();
            var environment = new AcceptanceRegression.StoredEnvironment(fixture, store)
                { AfterChannelSessionRegister = () => fixture.Settings = fixture.Settings with { Enabled = false } };
            var workflow = new AuthChannelWorkflow(environment, browser, fixture.Clock);
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Blocked && browser.Submissions == 0,
                "登记会话后设置变化仍交付凭据");
            Require(store.LoadSubmissionBudget()?.Attempts == 1, "拒绝时不得退还预留");
        })),
        ("共享额度：通道预留后设置变化则拒绝交付并保留额度", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.GatewayLocation = "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254";
            store.BlockAuthentication(new AuthenticationBlock(PortalFailure.InteractionRequired)
                { Attempts = 3, Revision = Guid.NewGuid(), ChannelFallbackCause = PortalFailure.MissingGatewayParameters });
            using var browser = new FixtureChannelBrowser();
            var environment = new AcceptanceRegression.StoredEnvironment(fixture, store)
                { AfterSubmissionReserve = () => fixture.Settings = fixture.Settings with { Enabled = false } };
            var workflow = new AuthChannelWorkflow(environment, browser, fixture.Clock);
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Blocked && browser.Submissions == 0,
                "设置变更后仍交付凭据");
            Require(store.LoadSubmissionBudget() is { Attempts: 1, State: SubmissionBudgetState.PossiblySent }, "拒绝时不应退还预留");
        })),
        ("共享额度：真实服务与通道工作流并发只发送一次", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.LoginResult = "unparseable";
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            var channelFixture = new MonitorFixture();
            channelFixture.Http.GatewayLocation = "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254";
            using var browser = new FixtureChannelBrowser();
            var workflow = new AuthChannelWorkflow(new AcceptanceRegression.StoredEnvironment(channelFixture, store), browser, fixture.Clock);
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Waiting, "通道未进入冷却");
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            using var start = new Barrier(2);
            var protocol = Task.Run(() => { start.SignalAndWait(); return worker.RunOnceAsync(default).GetAwaiter().GetResult(); });
            var channel = Task.Run(() => { start.SignalAndWait(); return workflow.RunOnceAsync(default).GetAwaiter().GetResult(); });
            Task.WaitAll(protocol, channel);
            Require(store.LoadSubmissionBudget()?.Attempts == 2, "竞争后没有恰好预留一次");
            Require(Count(fixture) - 1 + browser.Submissions == 1, "两条实际路径没有恰好一次交付凭据");
        })),
        ("共享额度：取消等待不把仍活动的浏览器当成已结束", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.GatewayLocation = "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254";
            store.BlockAuthentication(new AuthenticationBlock(PortalFailure.InteractionRequired)
                { Attempts = 3, Revision = Guid.NewGuid(), ChannelFallbackCause = PortalFailure.MissingGatewayParameters });
            using var cancellation = new CancellationTokenSource();
            using var browser = new FixtureChannelBrowser { BeforeSubmit = cancellation.Cancel };
            var workflow = new AuthChannelWorkflow(new AcceptanceRegression.StoredEnvironment(fixture, store), browser, fixture.Clock);
            try { workflow.RunOnceAsync(cancellation.Token).GetAwaiter().GetResult(); throw new Exception("取消未传播"); }
            catch (OperationCanceledException) { }
            using var execution = store.TryAcquireSubmissionExecution();
            Require(execution is null && store.LoadSubmissionBudget()?.Attempts == 1, "取消后活动会话未阻断或额度被退回");
        })),
        ("共享额度：损坏的浏览器会话记录保守阻断", () => WithStore((_, store, directory) =>
        {
            foreach (var content in new[] { "{broken", "{}", "{\"Version\":99,\"ProcessId\":1,\"StartedUtcTicks\":1}" })
            {
                File.WriteAllText(Path.Combine(directory, "submission-channel-session.json"), content);
                using var execution = store.TryAcquireSubmissionExecution();
                Require(execution is null, "损坏或未知版本的浏览器会话记录未阻断");
                Require(File.ReadAllText(Path.Combine(directory, "submission-channel-session.json")) == content, "损坏证据被删除");
            }
        })),
        ("共享额度：浏览器代理与当前校园网卡不一致时拒绝交付凭据", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.GatewayLocation = "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254";
            store.BlockAuthentication(new AuthenticationBlock(PortalFailure.InteractionRequired)
                { Attempts = 3, Revision = Guid.NewGuid(), ChannelFallbackCause = PortalFailure.MissingGatewayParameters });
            using var browser = new FixtureChannelBrowser { BoundToCurrentWifi = false };
            var workflow = new AuthChannelWorkflow(new AcceptanceRegression.StoredEnvironment(fixture, store), browser, fixture.Clock);
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Blocked && browser.Submissions == 0,
                "代理未绑定当前校园网卡仍交付凭据");
            Require(store.LoadSubmissionBudget() is null, "网卡不一致却占用额度");
        })),
        ("共享额度：通道返回后浏览器仍活动时服务不能接续", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.GatewayLocation = "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254";
            store.BlockAuthentication(new AuthenticationBlock(PortalFailure.InteractionRequired)
                { Attempts = 3, Revision = Guid.NewGuid(), ChannelFallbackCause = PortalFailure.MissingGatewayParameters });
            using var browser = new FixtureChannelBrowser();
            var workflow = new AuthChannelWorkflow(new AcceptanceRegression.StoredEnvironment(fixture, store), browser, fixture.Clock);
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Submitted, "未执行夹具提交");
            using var execution = store.TryAcquireSubmissionExecution();
            Require(execution is null, "活动浏览器未记录，服务可过早接续");
        })),
        ("共享额度：活动会话全部结束后重新持久启动完整等待", () => WithStore((fixture, store, directory) =>
        {
            using var session = new ChannelJobRegression.Fixture();
            using var browser = Process.GetProcessById(session.BrowserId);
            var block = new AuthenticationBlock(PortalFailure.InteractionRequired)
                { Attempts = 3, Revision = Guid.NewGuid(), ChannelFallbackCause = PortalFailure.MissingGatewayParameters };
            store.BlockAuthentication(block);
            using (var lease = store.TryBeginChannelSubmission(block, null, PortalFailure.MissingGatewayParameters,
                new SubmissionRetryPolicy(), DateTimeOffset.UtcNow, TimeSpan.Zero))
                Require(lease is not null && store.TryRegisterChannelSession(lease, browser.Id, session.OwnerId, session.Name), "浏览器身份未持久登记");
            browser.Kill();
            browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Require(new SafetyStateStore(directory).TryAcquireSubmissionExecution() is null, "辅助进程仍活动却释放共享执行权限");
            var beforeExit = store.LoadSubmissionBudget()!;
            var policy = new SubmissionRetryPolicy();
            var elapsed = TimeSpan.FromSeconds(500);
            var sent = beforeExit with { State = SubmissionBudgetState.PossiblySent };
            policy.Evaluate(sent, beforeExit.ReservedAt, TimeSpan.Zero);
            Require(policy.Evaluate(sent, beforeExit.ReservedAt.Add(elapsed), elapsed) == SubmissionRetryDecision.Authorized,
                "夹具未模拟活动会话超过等待间隔");
            session.Dispose();
            var confirmedAt = DateTimeOffset.UtcNow;
            IDisposable? acquired = null;
            Require(SpinWait.SpinUntil(() => (acquired = new SafetyStateStore(directory).TryAcquireSubmissionExecution()) is not null,
                TimeSpan.FromSeconds(10)), "会话结束后未能接续");
            using var afterExit = acquired;
            Require(afterExit is not null, "会话结束后未能接续");
            var after = store.LoadSubmissionBudget();
            Require(after is { Attempts: 1, State: SubmissionBudgetState.PossiblySent } && after.Revision != beforeExit.Revision &&
                after.ReservedAt >= confirmedAt.AddSeconds(-2) && after.NextPossibleAt >= confirmedAt.AddSeconds(118),
                "会话结束后未重新持久启动完整等待");
        })),
        ("共享额度：未知结果后结构性预检失败可再次授予通道启动意图", () => WithStore((fixture, store, _) =>
        {
            fixture.Settings = fixture.Settings with { AuthChannelMode = AuthChannelModes.Automatic };
            fixture.Http.LoginResult = "unparseable";
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Http.GatewayLocation = "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254";
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            var status = worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(status.AuthChannelRequested && status.AuthenticationPersistentState == "SubmissionPending", "未知结果未产生新的通道启动意图");
            Require(store.LoadSubmissionBudget()?.Attempts == 1 && Count(fixture) == 1, "预检错误增加了提交额度");
        })),
        ("共享额度：浏览器网络边界未确认时拒绝交付凭据", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.GatewayLocation = "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254";
            store.BlockAuthentication(new AuthenticationBlock(PortalFailure.InteractionRequired)
                { Attempts = 3, Revision = Guid.NewGuid(), ChannelFallbackCause = PortalFailure.MissingGatewayParameters });
            using var browser = new FixtureChannelBrowser { HasTrustedNetworkBoundary = false };
            var workflow = new AuthChannelWorkflow(new AcceptanceRegression.StoredEnvironment(fixture, store), browser, fixture.Clock);
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Blocked && browser.Submissions == 0,
                "浏览器未钉住校园网络仍交付凭据");
            Require(store.LoadSubmissionBudget() is null, "网络边界未确认却占用额度");
        })),
        ("共享额度：协议两次后通道等待完整间隔并用尽最后一次", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.LoginResult = "unparseable";
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Http.GatewayLocation = "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254";
            using var browser = new FixtureChannelBrowser();
            var workflow = new AuthChannelWorkflow(new AcceptanceRegression.StoredEnvironment(fixture, store), browser, fixture.Clock);
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Waiting, "启动未重新等待");
            fixture.Clock.Advance(TimeSpan.FromSeconds(119));
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Waiting && browser.Submissions == 0, "119秒提前提交");
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Submitted && browser.Submissions == 1, "120秒未使用最后额度");
            Require(store.LoadSubmissionBudget() is { Attempts: 3, State: SubmissionBudgetState.PossiblySent }, "跨载体计数不符");
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Exhausted && browser.Submissions == 1, "耗尽后仍提交");
        })),
        ("共享额度：损坏额度记录受控拒绝且不交付凭据", () => WithStore((fixture, store, directory) =>
        {
            File.WriteAllText(Path.Combine(directory, "submission-budget.json"), "{broken");
            using var browser = new FixtureChannelBrowser();
            var workflow = new AuthChannelWorkflow(new AcceptanceRegression.StoredEnvironment(fixture, store), browser, fixture.Clock);
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Blocked && browser.Submissions == 0,
                "损坏额度未受控拒绝");
        })),
        ("共享额度：通道明确需要人工时停止后续自动提交", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.GatewayLocation = "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254";
            store.BlockAuthentication(new AuthenticationBlock(PortalFailure.InteractionRequired)
                { Attempts = 3, Revision = Guid.NewGuid(), ChannelFallbackCause = PortalFailure.MissingGatewayParameters });
            using var browser = new FixtureChannelBrowser { Result = AuthChannelConclusion.NeedsHuman };
            var workflow = new AuthChannelWorkflow(new AcceptanceRegression.StoredEnvironment(fixture, store), browser, fixture.Clock);
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Submitted, "未执行授权的通道");
            Require(store.LoadAuthenticationBlock() is { State: AuthenticationState.InteractionRequired, ChannelFallbackCause: PortalFailure.None }, "人工终态未保留暂停");
            Require(store.LoadSubmissionBudget() is { Attempts: 1, State: SubmissionBudgetState.Concluded }, "人工终态未结束额度");
        })),
        ("共享额度：通道仅在预留和最终复核后向可信宿主交付凭据", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.GatewayLocation = "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254";
            store.BlockAuthentication(new AuthenticationBlock(PortalFailure.InteractionRequired)
                { Attempts = 3, Revision = Guid.NewGuid(), ChannelFallbackCause = PortalFailure.MissingGatewayParameters });
            using var browser = new FixtureChannelBrowser { BeforeSubmit = () =>
            {
                Require(store.LoadSubmissionBudget() is { Attempts: 1, State: SubmissionBudgetState.Reserved }, "填表前未持久预留");
                Require(store.LoadAuthenticationBlock()?.State == AuthenticationState.SubmissionPending, "填表前未保存待确认状态");
                Require(store.TryAcquireSubmissionExecution() is null, "填表时未持有共享执行锁");
            } };
            var workflow = new AuthChannelWorkflow(new AcceptanceRegression.StoredEnvironment(fixture, store), browser, fixture.Clock);
            Require(workflow.RunOnceAsync(default).GetAwaiter().GetResult() == ChannelWorkflowOutcome.Submitted, "符合条件未提交");
            Require(browser.Submissions == 1 && store.LoadSubmissionBudget() is { Attempts: 1, State: SubmissionBudgetState.PossiblySent }, "未知结果没有保留额度");
        })),
        ("共享额度：额度耗尽时通道不会向页面填入凭据", () => WithStore((fixture, store, _) =>
        {
            var clock = fixture.Clock;
            SubmissionBudget? budget = null;
            for (var i = 0; i < 3; i++)
            {
                var reserved = store.TryReserveSubmissionBudget(budget, clock.GetUtcNow()).Budget!;
                Require(store.TryTransitionSubmissionBudget(reserved, SubmissionBudgetState.PossiblySent), "准备额度失败");
                budget = store.LoadSubmissionBudget();
                clock.Advance(TimeSpan.FromSeconds(120));
            }
            store.BlockAuthentication(new AuthenticationBlock { State = AuthenticationState.SubmissionPending, Attempts = 3, Revision = Guid.NewGuid() });
            using var browser = new FixtureChannelBrowser();
            var workflow = new AuthChannelWorkflow(new AcceptanceRegression.StoredEnvironment(fixture, store), browser, clock);
            var outcome = workflow.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(outcome == ChannelWorkflowOutcome.Exhausted && browser.Submissions == 0, "额度耗尽后仍向页面交付凭据");
            Require(store.LoadSubmissionBudget()?.Attempts == 3, "拒绝后额度被修改");
        })),
        ("共享额度：两条载体并发时只允许一次持久预留", () => WithStore((fixture, store, directory) =>
        {
            var block = new AuthenticationBlock(PortalFailure.InteractionRequired)
                { Attempts = 3, Revision = Guid.NewGuid(), ChannelFallbackCause = PortalFailure.MissingGatewayParameters };
            store.BlockAuthentication(block);
            using var start = new Barrier(2);
            using var release = new ManualResetEventSlim();
            var channel = Task.Run(() =>
            {
                start.SignalAndWait();
                using var lease = new SafetyStateStore(directory).TryBeginChannelSubmission(block, null,
                    PortalFailure.MissingGatewayParameters, new SubmissionRetryPolicy(), fixture.Clock.GetUtcNow(), TimeSpan.Zero);
                if (lease is not null) release.Wait(TimeSpan.FromSeconds(5));
                return lease is not null;
            });
            var protocol = Task.Run(() =>
            {
                start.SignalAndWait();
                using var execution = new SafetyStateStore(directory).TryAcquireSubmissionExecution();
                if (execution is null) { release.Set(); return false; }
                var result = store.TryReserveSubmissionBudget(null, fixture.Clock.GetUtcNow());
                release.Set();
                return result.Outcome == SubmissionReservationOutcome.Reserved;
            });
            Task.WaitAll(channel, protocol);
            Require((channel.Result ? 1 : 0) + (protocol.Result ? 1 : 0) == 1, "并发载体未恰好预留一次");
            Require(store.LoadSubmissionBudget()?.Attempts == 1, "并发增加了多次额度");
        })),
        ("共享额度：协议已用两次后通道持锁预留最后一次", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.LoginResult = "unparseable";
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            var budget = store.LoadSubmissionBudget();
            var block = store.LoadAuthenticationBlock();
            Require(budget?.Attempts == 2 && block?.State == AuthenticationState.SubmissionPending, "协议未形成两次未知提交");
            var policy = new SubmissionRetryPolicy();
            var start = fixture.Clock.GetTimestamp();
            policy.Evaluate(budget, fixture.Clock.GetUtcNow(), fixture.Clock.GetElapsedTime(start));
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            using var lease = store.TryBeginChannelSubmission(block, budget, PortalFailure.MissingGatewayParameters,
                policy, fixture.Clock.GetUtcNow(), fixture.Clock.GetElapsedTime(start));
            Require(lease is not null && store.LoadSubmissionBudget() is { Attempts: 3, State: SubmissionBudgetState.Reserved }, "通道未共用协议额度");
            Require(store.TryAcquireSubmissionExecution() is null, "通道未持有协议执行锁");
        })),
        ("有限重试：预检上限不吞掉明确临时拒绝后的提交额度", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.GatewayLocation = null;
            fixture.Http.StatusClientIpv4 = null;
            fixture.Http.HomePage = "no address";
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Http.GatewayLocation = MonitorHttp.GatewayRedirect;
            fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"ldap timeout\"}";
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            for (var i = 0; i < 2; i++)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(120));
                worker.RunOnceAsync(default).GetAwaiter().GetResult();
            }
            Require(Count(fixture) == 3 && store.LoadSubmissionBudget()?.Attempts == 3, "预检计数提前终止临时拒绝后的提交");
        })),
        ("有限重试：状态结构损坏不是端点失联不得借未知重发", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.LoginResult = "unparseable";
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Http.StatusJson = "<html>unrecognizable</html>";
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 1 && store.LoadSubmissionBudget()?.Attempts == 1, "结构损坏被当作端点失联放行");
        })),
        ("有限重试：真实进程持锁预留被终止后保守接续", () => WithStore((fixture, store, directory) =>
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(SubmissionRetryRegression).Assembly.Location);
            start.ArgumentList.Add("--submission-retry-test-peer");
            start.ArgumentList.Add(directory);
            using var peer = Process.Start(start)!;
            try
            {
                Require(peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() == "RESERVED", "子进程未持久预留");
                Require(store.TryAcquireSubmissionExecution() is null, "活动子进程未独占执行锁");
                using (var blocked = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock))
                {
                    blocked.RunOnceAsync(default).GetAwaiter().GetResult();
                    fixture.Clock.Advance(TimeSpan.FromSeconds(120));
                    blocked.RunOnceAsync(default).GetAwaiter().GetResult();
                    Require(Count(fixture) == 0 && store.LoadSubmissionBudget()?.State == SubmissionBudgetState.Reserved, "活动预留被误恢复");
                }
                peer.Kill();
                peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                // Process exit notification can precede release of its OS resources.
                // Synchronize on the actual execution handle without advancing fake time.
                Require(SpinWait.SpinUntil(() =>
                {
                    using var released = store.TryAcquireSubmissionExecution();
                    return released is not null;
                }, TimeSpan.FromSeconds(10)), "进程退出后执行句柄未释放");
                fixture.Http.LoginResult = "unparseable";
                using var restarted = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(store.LoadSubmissionBudget()?.State == SubmissionBudgetState.PossiblySent, "接续等待尚未开始");
                fixture.Clock.Advance(TimeSpan.FromSeconds(119));
                restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(Count(fixture) == 0, "杀进程后立即免费重发");
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(Count(fixture) == 1 && store.LoadSubmissionBudget()?.Attempts == 2, "进程退出丢失已用次数");
            }
            finally { if (!peer.HasExited) { peer.Kill(); peer.WaitForExit(); } }
        })),
        ("有限重试：人工确认重置后保留未知暂停并受控开始新额度", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.LoginResult = "unparseable";
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            var pending = store.LoadAuthenticationBlock();
            var used = store.LoadSubmissionBudget();
            Require(pending?.State == AuthenticationState.SubmissionPending && used?.Attempts == 1, "未准备未知提交");
            Require(store.TryRestartAuthentication(pending, used), "人工重置失败");
            Require(store.LoadAuthenticationBlock() == pending, "人工重置解除安全暂停");
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 1, "重置后绕过等待立即提交");
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 2 && store.LoadSubmissionBudget()?.Attempts == 1, "人工重置后未受控开始新额度");
        })),
        ("有限重试：持执行锁最终复核拒绝失效的人工确认", () => WithStore((fixture, store, directory) =>
        {
            fixture.Http.LoginResult = "unparseable";
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(store.TryRestartAuthentication(store.LoadAuthenticationBlock(), store.LoadSubmissionBudget()), "准备人工确认失败");
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            var reads = 0;
            fixture.BeforeWifiRead = () =>
            {
                if (++reads == 2) File.WriteAllText(Path.Combine(directory, "submission-restart.json"), "{}");
            };
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(reads >= 2 && Count(fixture) == 1 && store.LoadSubmissionBudget()?.Attempts == 0, "确认失效后仍发送或占用额度");
        })),
        ("有限重试：外网在线不消解不重置而门户明确成功审计后消解", () => WithStore((fixture, store, directory) =>
        {
            fixture.Http.LoginResult = "unparseable";
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Http.Online = true;
            fixture.Http.PortalResult = "-1";
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            var online = worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 1 && online.AuthenticationAcceptedAt is null && store.LoadAuthenticationBlock()?.State == AuthenticationState.SubmissionPending,
                "外网在线被当作认证成功");
            Require(store.LoadSubmissionBudget()?.Attempts == 1, "外网恢复重置额度");
            fixture.Http.PortalResult = "1";
            fixture.Clock.Advance(TimeSpan.FromSeconds(60));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(store.LoadAuthenticationBlock()?.State == AuthenticationState.Ready && File.Exists(Path.Combine(directory, "auth-release-audit.jsonl")), "门户成功未审计消解");
            Require(store.LoadSubmissionBudget() is { Attempts: 0, State: SubmissionBudgetState.Concluded }, "成功复检没有审计并重置额度");
        })),
        ("有限重试：预检失败不占额度也不吃掉未知后的两次重试", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.GatewayLocation = null;
            fixture.Http.StatusClientIpv4 = null;
            fixture.Http.HomePage = "no address";
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(store.LoadSubmissionBudget() is null && Count(fixture) == 0, "预检计入提交额度");
            fixture.Http.GatewayLocation = MonitorHttp.GatewayRedirect;
            fixture.Http.LoginResult = "unparseable";
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Http.PortalResult = "-1";
            for (var i = 0; i < 2; i++)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(120));
                worker.RunOnceAsync(default).GetAwaiter().GetResult();
            }
            Require(Count(fixture) == 3 && store.LoadSubmissionBudget()?.Attempts == 3, "预检上限吃掉实际重试额度");
        })),
        ("有限重试：慢预留与慢发送之后仍完整等待120秒", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.LoginResult = "unparseable";
            var sends = new List<DateTimeOffset>();
            fixture.Http.BeforeLogin = () => { sends.Add(fixture.Clock.GetUtcNow()); fixture.Clock.Advance(TimeSpan.FromSeconds(10)); };
            var environment = new AcceptanceRegression.StoredEnvironment(fixture, store)
                { AfterSubmissionReserve = () => fixture.Clock.Advance(TimeSpan.FromSeconds(90)) };
            using var worker = new MonitorWorker(environment, fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Clock.Advance(TimeSpan.FromSeconds(119));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 1, "预留前的时间被算作发送后等待");
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(sends.Count == 2 && sends[1] - sends[0] >= TimeSpan.FromSeconds(120), "实际可能发送间隔不足");
        })),
        ("有限重试：损坏记录及旧Pending缺额度保守停发", () => WithStore((_, _, directory) =>
        {
            foreach (var content in new[] { "{}", "{broken", "{\"Version\":99}" })
            {
                var child = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(child);
                File.WriteAllText(Path.Combine(child, "submission-budget.json"), content);
                var fixture = new MonitorFixture();
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, new(child)), fixture.Clock);
                worker.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(Count(fixture) == 0, "损坏额度允许发送");
            }
            var legacy = new MonitorFixture();
            var state = new SafetyStateStore(directory);
            state.BlockAuthentication(new() { State = AuthenticationState.SubmissionPending, Attempts = 1 });
            using var paused = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(legacy, state), legacy.Clock);
            paused.RunOnceAsync(default).GetAwaiter().GetResult();
            legacy.Clock.Advance(TimeSpan.FromHours(1));
            paused.RunOnceAsync(default).GetAwaiter().GetResult();
            legacy.Clock.Advance(TimeSpan.FromSeconds(2));
            paused.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(legacy) == 0 && state.LoadSubmissionBudget() is null, "旧Pending凭空初始化额度");
        })),
        ("有限重试：状态端点失联时仍在可信网关配置复核后有限重试", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.LoginResult = "unparseable";
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Http.FailurePath = "/drcom/chkstatus";
            fixture.Http.FailureException = new HttpRequestException("synthetic status unavailable");
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 2 && store.LoadSubmissionBudget()?.Attempts == 2, "状态端点失联退化为永久停发");
        })),
        ("有限重试：直接成功记录提交响应来源并审计重置", () => WithStore((fixture, store, directory) =>
        {
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(store.LoadAuthenticationBlock()?.State == AuthenticationState.Ready && store.LoadSubmissionBudget()?.Attempts == 0,
                "直接成功未消解并重置额度");
            var lines = File.ReadAllLines(Path.Combine(directory, "auth-release-audit.jsonl"));
            using var audit = System.Text.Json.JsonDocument.Parse(lines[0]);
            Require(audit.RootElement.GetProperty("Action").GetString() == "门户提交成功消解", "直接成功误记为只读复检");
        })),
        ("有限重试：明确成功但消解写入失败时持久暂停避免重发", () => WithStore((fixture, store, _) =>
        {
            using (var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store)
                { FailResolve = true }, fixture.Clock))
                worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 1 && store.LoadAuthenticationBlock()?.State == AuthenticationState.SafetyBlocked, "明确成功后写失败没有持久暂停");
            using var restarted = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            restarted.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Clock.Advance(TimeSpan.FromMinutes(10));
            restarted.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 1, "明确成功后因终态写入失败再次发送");
        })),
        ("有限重试：明确密码绑定拒绝立即停止即使额度终态写失败", () => WithStore((_, _, directory) =>
        {
            foreach (var message in new[] { "密码错误", "只能绑定指定的VLAN设备", "captcha required" })
            {
                var child = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(child);
                var store = new SafetyStateStore(child);
                var fixture = new MonitorFixture();
                fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"" + message + "\"}";
                using (var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store)
                    { FailSubmissionTransition = state => state == SubmissionBudgetState.Concluded }, fixture.Clock))
                    worker.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(store.LoadAuthenticationBlock()?.State is AuthenticationState.CredentialsRejected or AuthenticationState.BindingRejected or AuthenticationState.InteractionRequired,
                    "明确拒绝未保存认证终态：" + message);
                using var restarted = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                fixture.Clock.Advance(TimeSpan.FromHours(1));
                restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(Count(fixture) == 1, "明确拒绝后仍提交：" + message);
            }
        })),
        ("有限重试：外网在预检中恢复也须暂停提交且继续只读定性", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.Delay = uri =>
            {
                if (uri.AbsolutePath == "/eportal/portal/page/loadConfig") fixture.Http.Online = true;
                return TimeSpan.Zero;
            };
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 0 && store.LoadSubmissionBudget() is null, "外网恢复后仍占额度或提交");
        })),
        ("有限重试：预留失败及最终认证额度修订变化均零发送", () => WithStore((fixture, store, directory) =>
        {
            foreach (var failure in new[] { "lock", "write", "auth", "budget", "clock" })
            {
                var child = Path.Combine(directory, failure);
                Directory.CreateDirectory(child);
                var state = new SafetyStateStore(child);
                var current = new MonitorFixture();
                IDisposable? fileLock = null;
                var environment = new AcceptanceRegression.StoredEnvironment(current, state)
                {
                    BeforeSubmissionReserve = () =>
                    {
                        if (failure == "lock") fileLock = new FileStream(Path.Combine(child, "auth-state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        if (failure == "write") Directory.CreateDirectory(Path.Combine(child, "submission-budget.json"));
                    },
                    AfterSubmissionReserve = () =>
                    {
                        fileLock?.Dispose();
                        if (failure == "auth") state.BlockAuthentication(new(PortalFailure.CredentialsRejected) { Revision = Guid.NewGuid() });
                        if (failure == "budget") state.TryTransitionSubmissionBudget(state.LoadSubmissionBudget()!, SubmissionBudgetState.Concluded);
                        if (failure == "clock") current.Clock.Advance(TimeSpan.Zero, TimeSpan.FromDays(-1));
                    }
                };
                using var worker = new MonitorWorker(environment, current.Clock);
                try { worker.RunOnceAsync(default).GetAwaiter().GetResult(); }
                finally { fileLock?.Dispose(); }
                Require(Count(current) == 0, failure + " 时仍发送");
                Require(current.Logs.Any(log => log.EventCode == "SubmissionNotAuthorized"), failure + " 缺少拒发原因");
            }
        })),
        ("有限重试：预留后发送前进程退出按可能发送计且重启重新等待", () => WithStore((fixture, store, directory) =>
        {
            var reserved = store.TryReserveSubmissionBudget(null, fixture.Clock.GetUtcNow()).Budget!;
            store.BlockAuthentication(new() { State = AuthenticationState.SubmissionPending, Attempts = 1, Revision = Guid.NewGuid() });
            fixture.Http.PortalResult = "-1";
            fixture.Http.LoginResult = "unparseable";
            fixture.Clock.Advance(TimeSpan.FromDays(1));
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, new(directory)), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 0 && store.LoadSubmissionBudget()?.Attempts == 1, "重启立即发送或退还次数");
            fixture.Clock.Advance(TimeSpan.FromSeconds(119));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 0, "重启等待不足120秒");
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 1 && store.LoadSubmissionBudget()?.Attempts == 2, "中断预留未计入下一次额度");
            Require(reserved.Attempts == 1, "预留无效");
        })),
        ("有限重试：未知持续时120秒再发且合计三次封顶继续复检", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.LoginResult = "unparseable";
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            fixture.Http.PortalResult = "-1";
            foreach (var seconds in new[] { 2, 4, 113 })
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
                worker.RunOnceAsync(default).GetAwaiter().GetResult();
            }
            Require(Count(fixture) == 1, "119秒前重发");
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 2 && store.LoadSubmissionBudget()?.Attempts == 2, "120秒没有重试");
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 3 && store.LoadSubmissionBudget()?.Attempts == 3, "未允许第二次额外提交");
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            var status = worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 3 && fixture.Logs.Any(log => log.Diagnostics?.Reason == "SubmissionBudgetExhausted"), "耗尽仍发送或无明确原因");
            Require(status.Detail.Contains("已停止自动提交"), "未显示停止原因");
            var rechecks = fixture.Logs.Count(log => log.EventCode == "SubmissionRechecking");
            fixture.Clock.Advance(TimeSpan.FromSeconds(60));
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(fixture.Logs.Count(log => log.EventCode == "SubmissionRechecking") > rechecks, "耗尽停止只读复检");
        })),
        ("有限重试：服务在发出前持久预留并在结果未知后保持计数", () => WithStore((fixture, store, _) =>
        {
            fixture.Http.LoginResult = "unparseable";
            fixture.Http.BeforeLogin = () => Require(store.LoadSubmissionBudget() is { State: SubmissionBudgetState.Reserved, Attempts: 1 }, "发送前没有持久预留");
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            var status = worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(Count(fixture) == 1, "未发出首次凭据");
            Require(store.LoadSubmissionBudget() is { State: SubmissionBudgetState.PossiblySent, Attempts: 1 }, "未保留可能发送计数");
            Require(status.AuthenticationPersistentState == "SubmissionPending", "响应无法解析未保持未知");
        })),
        ("有限重试：单调等待119秒拒绝120秒放行", () =>
        {
            var now = new ManualClock().GetUtcNow();
            var budget = new SubmissionBudget { State = SubmissionBudgetState.PossiblySent, Attempts = 1,
                ReservedAt = now, NextPossibleAt = now.AddSeconds(120), Revision = Guid.NewGuid() };
            var policy = new SubmissionRetryPolicy();
            Require(policy.Evaluate(budget, now, TimeSpan.Zero) == SubmissionRetryDecision.Waiting, "未等待");
            Require(policy.Evaluate(budget, now.AddSeconds(119), TimeSpan.FromSeconds(119)) == SubmissionRetryDecision.Waiting, "119秒被放行");
            Require(policy.Evaluate(budget, now.AddSeconds(120), TimeSpan.FromSeconds(120)) == SubmissionRetryDecision.Authorized, "120秒未放行");
            return Task.CompletedTask;
        }),
        ("有限重试：墙钟跳变回拨与重启均不能缩短等待", () =>
        {
            var now = new ManualClock().GetUtcNow();
            var budget = new SubmissionBudget { State = SubmissionBudgetState.PossiblySent, Attempts = 1,
                ReservedAt = now, NextPossibleAt = now.AddSeconds(120), Revision = Guid.NewGuid() };
            var policy = new SubmissionRetryPolicy();
            policy.Evaluate(budget, now, TimeSpan.Zero);
            Require(policy.Evaluate(budget, now.AddDays(1), TimeSpan.FromSeconds(119)) == SubmissionRetryDecision.WaitClock, "前跳被视为可信");
            Require(policy.Evaluate(budget, now.AddDays(1).AddSeconds(1), TimeSpan.FromSeconds(120)) == SubmissionRetryDecision.Waiting, "前跳缩短等待");
            Require(policy.Evaluate(budget, now.AddDays(1).AddSeconds(121), TimeSpan.FromSeconds(240)) == SubmissionRetryDecision.Authorized, "前跳后未恢复受控等待");
            Require(policy.Evaluate(budget, now.AddSeconds(200), TimeSpan.FromSeconds(241)) == SubmissionRetryDecision.WaitClock, "部分回拨被忽略");
            Require(policy.Evaluate(budget, now.AddSeconds(319), TimeSpan.FromSeconds(360)) == SubmissionRetryDecision.Waiting, "回拨缩短等待");
            Require(policy.Evaluate(budget, now.AddSeconds(320), TimeSpan.FromSeconds(361)) == SubmissionRetryDecision.Authorized, "回拨后等待未完成");
            Require(new SubmissionRetryPolicy().Evaluate(budget, now.AddDays(2), TimeSpan.Zero) == SubmissionRetryDecision.Waiting, "重启凭墙钟立即重发");
            Require(policy.Evaluate(budget, default, TimeSpan.FromSeconds(362)) == SubmissionRetryDecision.InvalidTime, "无效时间放行");
            Require(policy.Evaluate(budget, now.AddSeconds(322), TimeSpan.FromSeconds(363)) == SubmissionRetryDecision.Waiting, "时间失效后恢复未重新等待");
            return Task.CompletedTask;
        }),
        ("有限重试：额度耗尽结束及活动状态拒绝外网在线暂停", () =>
        {
            var now = new ManualClock().GetUtcNow();
            var budget = new SubmissionBudget { State = SubmissionBudgetState.PossiblySent, Attempts = 1,
                ReservedAt = now, NextPossibleAt = now.AddSeconds(120), Revision = Guid.NewGuid() };
            var policy = new SubmissionRetryPolicy();
            policy.Evaluate(budget, now, TimeSpan.Zero);
            Require(policy.Evaluate(budget, now.AddSeconds(120), TimeSpan.FromSeconds(120), true) == SubmissionRetryDecision.InternetOnline, "外网在线仍提交");
            Require(policy.Evaluate(budget with { Attempts = 3 }, now.AddHours(1), TimeSpan.FromHours(1)) == SubmissionRetryDecision.Exhausted, "第四次未拒绝");
            Require(policy.Evaluate(budget with { State = SubmissionBudgetState.Concluded }, now.AddHours(2), TimeSpan.FromHours(2)) == SubmissionRetryDecision.Concluded, "明确结束仍提交");
            Require(policy.Evaluate(budget with { State = SubmissionBudgetState.Reserved }, now.AddHours(3), TimeSpan.FromHours(3)) == SubmissionRetryDecision.InFlight, "活动预留仍提交");
            return Task.CompletedTask;
        })
    ];

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class FixtureChannelBrowser : IAuthChannelBrowser, IDisposable
    {
        private ChannelJobRegression.Fixture? session;
        private ChannelJobRegression.Fixture Session => session ??= new ChannelJobRegression.Fixture();
        public int Submissions { get; private set; }
        public bool HasTrustedNetworkBoundary { get; init; } = true;
        public bool BoundToCurrentWifi { get; init; } = true;
        public bool IsBoundTo(Settings settings, WifiLink link) => BoundToCurrentWifi;
        public int BrowserProcessId => Session.BrowserId;
        public int HelperProcessId => Session.OwnerId;
        public string ProcessJobName => Session.Name;
        public Action? BeforeSubmit { get; init; }
        public AuthChannelConclusion Result { get; init; } = AuthChannelConclusion.Unknown;
        public Task<AuthChannelConclusion> SubmitAsync(Credentials credentials, CancellationToken ct)
        {
            BeforeSubmit?.Invoke();
            ct.ThrowIfCancellationRequested();
            Submissions++;
            return Task.FromResult(Result);
        }
        public void Dispose() => session?.Dispose();
    }
    private static int Count(MonitorFixture fixture) => fixture.Http.Requests.Count(uri => uri.AbsolutePath == "/eportal/portal/login");
    private static Task WithStore(Action<MonitorFixture, SafetyStateStore, string> test)
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var directory = Path.GetFullPath(Path.Combine(root, "AutoNet-issue32-" + Guid.NewGuid().ToString("N")));
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理越界");
        Directory.CreateDirectory(directory);
        try { test(new MonitorFixture(), new SafetyStateStore(directory), directory); }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    public static int RunPeer(string[] args)
    {
        var store = new SafetyStateStore(args[1]);
        using var execution = store.TryAcquireSubmissionExecution();
        if (execution is null) return 2;
        var reservation = store.TryReserveSubmissionBudget(null, new ManualClock().GetUtcNow());
        if (reservation.Outcome != SubmissionReservationOutcome.Reserved) return 3;
        store.BlockAuthentication(new() { State = AuthenticationState.SubmissionPending, Attempts = 1, Revision = Guid.NewGuid() });
        Console.WriteLine("RESERVED");
        Console.Out.Flush();
        Console.ReadLine();
        return 0;
    }

}
