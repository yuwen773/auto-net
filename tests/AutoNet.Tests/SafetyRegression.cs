using System.Net;
using System.Text.Json;
using AutoNet.Core;
using AutoNet.Service;

internal static class SafetyRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("安全门控：30 秒与三轮失败缺一不可，成功立即取消累计", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var offline = new Observation(WifiState.Ready, false, PortalState.Unknown);
            var policy = new RecoveryPolicy();
            Require(policy.Decide(offline, now, true, true) == ActionKind.None);
            Require(policy.Decide(offline, now.AddSeconds(30), true, true) == ActionKind.None);
            Require(policy.Decide(offline with { Portal = PortalState.LoggedIn }, now.AddSeconds(31), true, true) == ActionKind.DisconnectWifi);
            policy.Decide(offline with { DirectOnline = true }, now.AddSeconds(32), true, true);
            Require(policy.Decide(offline, now.AddSeconds(33), true, true) == ActionKind.None);
            Require(policy.Decide(offline, now.AddSeconds(34), true, true) == ActionKind.None);
            Require(policy.Decide(offline, now.AddSeconds(62), true, true) == ActionKind.None);
            Require(policy.Decide(offline, now.AddSeconds(63), true, true) == ActionKind.DisconnectWifi);
            return Task.CompletedTask;
        }),
        ("安全门控：单次故障三次封顶且间隔不少于120秒", async () =>
        {
            var fixture = Stalled();
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await FirstDisconnectAsync(worker, fixture);
            fixture.Clock.Advance(TimeSpan.FromSeconds(119)); await worker.RunOnceAsync(default);
            Require(fixture.Disconnects == 1);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1)); await worker.RunOnceAsync(default);
            Require(fixture.Disconnects == 2);
            fixture.Clock.Advance(TimeSpan.FromSeconds(120)); await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromHours(5)); var exhausted = await worker.RunOnceAsync(default);
            Require(fixture.Disconnects == 3 && exhausted.DisconnectAttempts == 3 && exhausted.Detail.Contains("额度已用尽"));
            Require(fixture.Logs.Where(e => e.Action == "重连校园 Wi-Fi").Select(e => e.DisconnectAttempts).SequenceEqual(new int?[] { 1, 2, 3 }));
            fixture.Http.PortalResult = "0";
            Require((await worker.RunOnceAsync(default)).CredentialRequestStarted);
            Require(fixture.Http.Requests.Count(u => u.Query.Contains("user_password")) == 1);
        }),
        ("安全门控：重启保留额度且重新等待完整冷却，墙钟前跳不绕过", async () =>
        {
            var fixture = Stalled();
            using (var worker = new MonitorWorker(fixture, fixture.Clock)) await FirstDisconnectAsync(worker, fixture);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1), TimeSpan.FromDays(10));
            using (var restarted = new MonitorWorker(fixture, fixture.Clock))
            {
                await restarted.RunOnceAsync(default);
                fixture.Clock.Advance(TimeSpan.FromSeconds(30)); await restarted.RunOnceAsync(default);
                fixture.Clock.Advance(TimeSpan.FromSeconds(89)); await restarted.RunOnceAsync(default);
                Require(fixture.Disconnects == 1);
                fixture.Clock.Advance(TimeSpan.FromSeconds(1)); await restarted.RunOnceAsync(default);
                fixture.Clock.Advance(TimeSpan.FromSeconds(120)); await restarted.RunOnceAsync(default);
                Require(fixture.Disconnects == 3);
            }
            using var exhausted = new MonitorWorker(fixture, fixture.Clock);
            await exhausted.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromMinutes(10)); await exhausted.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromMinutes(10));
            Require((await exhausted.RunOnceAsync(default)).DisconnectAttempts == 3 && fixture.Disconnects == 3);
        }),
        ("安全门控：暂停和用户切网不清额度，目标网络分别保存", async () =>
        {
            var fixture = Stalled();
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await FirstDisconnectAsync(worker, fixture);
            fixture.Settings = fixture.Settings with { Enabled = false };
            fixture.Clock.Advance(TimeSpan.FromSeconds(20)); await worker.RunOnceAsync(default);
            fixture.Link = fixture.Link with { OtherProfile = true };
            fixture.Settings = fixture.Settings with { Enabled = true };
            await worker.RunOnceAsync(default);
            fixture.Link = fixture.Link with { OtherProfile = false };
            await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(30)); var resumed = await worker.RunOnceAsync(default);
            Require(resumed.DisconnectAttempts == 1 && fixture.Disconnects == 1);
            fixture.Settings = fixture.Settings with { WifiProfile = "other-target" };
            Require((await worker.RunOnceAsync(default)).DisconnectAttempts == 0);
            fixture.Settings = fixture.Settings with { WifiProfile = "fixture" };
            Require((await worker.RunOnceAsync(default)).DisconnectAttempts == 1);
        }),
        ("安全门控：短暂在线保留额度，连续60秒在线才持久清零", async () =>
        {
            var fixture = Stalled();
            using (var worker = new MonitorWorker(fixture, fixture.Clock))
            {
                await FirstDisconnectAsync(worker, fixture);
                fixture.Http.Online = true; await worker.RunOnceAsync(default);
                fixture.Clock.Advance(TimeSpan.FromSeconds(59));
                Require((await worker.RunOnceAsync(default)).DisconnectAttempts == 1);
                fixture.Http.Online = false; await worker.RunOnceAsync(default);
                fixture.Http.Online = true; await worker.RunOnceAsync(default);
                fixture.Clock.Advance(TimeSpan.FromSeconds(59));
                Require((await worker.RunOnceAsync(default)).DisconnectAttempts == 1);
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                Require((await worker.RunOnceAsync(default)).DisconnectAttempts == 0);
            }
            fixture.Http.Online = false;
            using var restarted = new MonitorWorker(fixture, fixture.Clock);
            await FirstDisconnectAsync(restarted, fixture);
            Require(fixture.Disconnects == 2);
        }),
        ("安全门控：额度写入失败不执行断线，写入成功后才能重试", async () =>
        {
            var fixture = Stalled(); fixture.FailBudget = true;
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await FirstDisconnectAsync(worker, fixture, expected: 0);
            Require(fixture.Budgets.Count == 0);
            fixture.FailBudget = false;
            fixture.Clock.Advance(TimeSpan.FromSeconds(120)); await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(15)); await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(15)); var retried = await worker.RunOnceAsync(default);
            Require(fixture.Disconnects == 1 && retried.DisconnectAttempts == 2 && fixture.Budgets.Values.Single().Attempts == 2);
        }),
        ("安全门控：三次额度在Windows动作前已保存，请求失败也消耗额度", async () =>
        {
            var fixture = Stalled(); fixture.FailDisconnect = true;
            fixture.BeforeDisconnect = () => Require(fixture.Budgets.Values.Single().Attempts == fixture.Disconnects);
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await FirstDisconnectAsync(worker, fixture);
            fixture.Clock.Advance(TimeSpan.FromSeconds(120)); await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(120)); await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromHours(1)); await worker.RunOnceAsync(default);
            Require(fixture.Disconnects == 3 && fixture.Budgets.Values.Single().Attempts == 3);
        }),
        ("绑定拒绝：明确提示独立分类，不把泛化失败误报为绑定或密码错误", async () =>
        {
            foreach (var message in new[] { "只能绑定指定的VLAN设备", "只能绑定指定的 VLAN 设备", "只能绑定指定的MAC设备", "只能绑定指定的AC设备" })
            {
                var handler = new MonitorHttp { LoginResult = JsonSerializer.Serialize(new { result = 0, Msg = message }) };
                using var http = new HttpClient(handler);
                var result = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), default);
                Require(result.Result == LoginResult.RejectedBinding && result.Failure == PortalFailure.BindingRejected && result.CredentialRequestStarted);
                Require(result.SafeSummary.Contains("绑定被拒绝") && !result.SafeSummary.Contains("服务器明确拒绝凭据"));
            }
            foreach (var message in new[] { "VLAN timeout", "MAC unavailable", "ldap timeout", "未知错误" })
            {
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { result = 0, msg = message }));
                Require(DrcomPortal.Classify(json.RootElement) == LoginResult.Rejected);
            }
        }),
        ("绑定拒绝：只提交一次，重启保存设置及更改凭据不解除，专门解除后再试", async () =>
        {
            var fixture = new MonitorFixture();
            fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"只能绑定指定的VLAN设备\"}";
            using (var worker = new MonitorWorker(fixture, fixture.Clock))
            {
                Require((await worker.RunOnceAsync(default)).CredentialsBlocked);
                fixture.Settings = fixture.Settings with { KeepAwakeOnAc = false, ProxyUrl = "http://127.0.0.1:7897" };
                fixture.Credentials = new("changed-test", "changed-never-real");
                fixture.Clock.Advance(TimeSpan.FromHours(1)); await worker.RunOnceAsync(default);
            }
            using var restarted = new MonitorWorker(fixture, fixture.Clock);
            var blocked = await restarted.RunOnceAsync(default);
            Require(blocked.CredentialsBlocked && blocked.Detail.Contains("绑定被拒绝") && blocked.AuthenticationFailure == "BindingRejected");
            Require(fixture.Http.Requests.Count(u => u.Query.Contains("user_password")) == 1);
            fixture.Http.PortalResult = "-1";
            await FirstDisconnectAsync(restarted, fixture, expected: 0);
            fixture.Link = new(false, null, null, 7, []);
            await restarted.RunOnceAsync(default);
            Require(fixture.Connects == 1 && fixture.Disconnects == 0);
            fixture.Link = new(true, null, IPAddress.Parse("10.0.0.1"), 7, []);
            fixture.Http.PortalResult = "0";
            fixture.Blocked = false; // The fixture's dedicated manual release boundary.
            var again = await restarted.RunOnceAsync(default);
            Require(again.CredentialsBlocked && fixture.Http.Requests.Count(u => u.Query.Contains("user_password")) == 2);
        }),
        ("绑定拒绝：在线及暂停恢复不解除认证阻断，已断开仍可重连", async () =>
        {
            var fixture = new MonitorFixture { Blocked = true, BlockFailure = PortalFailure.BindingRejected };
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            fixture.Http.Online = true;
            await worker.RunOnceAsync(default); fixture.Clock.Advance(TimeSpan.FromSeconds(60));
            Require((await worker.RunOnceAsync(default)).CredentialsBlocked);
            fixture.Settings = fixture.Settings with { Enabled = false }; await worker.RunOnceAsync(default);
            fixture.Settings = fixture.Settings with { Enabled = true }; fixture.Http.Online = false;
            fixture.Link = new(false, null, null, 7, []);
            Require((await worker.RunOnceAsync(default)).CredentialsBlocked && fixture.Connects == 1);
            Require(fixture.Http.Requests.All(u => !u.Query.Contains("user_password")));
        }),
        ("安全存储：状态跨实例保存、网络隔离、旧标记及损坏标记保持暂停", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "AutoNet-safety-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var settings = new MonitorFixture().Settings;
                var store = new SafetyStateStore(directory);
                var budget = new DisconnectBudget(3, DateTimeOffset.UtcNow.AddSeconds(120));
                store.SaveDisconnectBudget(settings, budget);
                store.BlockAuthentication(new(PortalFailure.BindingRejected));
                var restarted = new SafetyStateStore(directory);
                Require(restarted.LoadDisconnectBudget(settings) == budget);
                Require(restarted.LoadDisconnectBudget(settings with { WifiProfile = "other" }).Attempts == 0);
                Require(restarted.LoadAuthenticationBlock()?.Failure == PortalFailure.BindingRejected);
                File.WriteAllText(Path.Combine(directory, "auth-block.txt"), "legacy-fixture-fingerprint");
                Require(restarted.LoadAuthenticationBlock() is not null);
                File.WriteAllText(Path.Combine(directory, "auth-block.txt"), "{broken");
                Require(restarted.LoadAuthenticationBlock() is not null);
                restarted.ClearAuthenticationBlock();
                Require(store.LoadAuthenticationBlock() is null);
                Require(store.LoadDisconnectBudget(settings) == budget);
                File.WriteAllText(Directory.GetFiles(directory, "disconnect-*.json").Single(), "{\"Attempts\":4}");
                try { store.LoadDisconnectBudget(settings); throw new Exception("损坏额度不得自动清零"); }
                catch (InvalidDataException) { }
            }
            finally
            {
                var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
                var target = Path.GetFullPath(directory);
                if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理越界");
                Directory.Delete(target, true);
            }
            return Task.CompletedTask;
        })
    ];

    private static MonitorFixture Stalled()
    {
        var fixture = new MonitorFixture(); fixture.Http.PortalResult = "-1";
        return fixture;
    }

    private static async Task FirstDisconnectAsync(MonitorWorker worker, MonitorFixture fixture, int? expected = null)
    {
        var count = expected ?? fixture.Disconnects + 1;
        await worker.RunOnceAsync(default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(15)); await worker.RunOnceAsync(default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(15)); await worker.RunOnceAsync(default);
        Require(fixture.Disconnects == count);
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("安全约束不符合预期"); }
}
