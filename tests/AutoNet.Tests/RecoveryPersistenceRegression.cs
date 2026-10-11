using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoNet.Core;
using AutoNet.Service;

internal static class RecoveryPersistenceRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("恢复持久安全：完成落盘失败和请求中崩溃跨重启都禁止继续断线", () =>
        {
            foreach (var crash in new[] { false, true })
            AcceptanceRegression.WithStore((store, directory) =>
            {
                var fixture = Stalled();
                if (crash) fixture.BeforeDisconnect = () => throw new InvalidOperationException("synthetic crash");
                var environment = new AcceptanceRegression.StoredEnvironment(fixture, store)
                { FailBudgetWrite = next => !crash && next.State == DisconnectRequestState.Completed && next.Attempts > 0 };
                using (var worker = new MonitorWorker(environment, fixture.Clock))
                {
                    Observe(worker, fixture);
                    fixture.Http.Online = true; worker.RunOnceAsync(default).GetAwaiter().GetResult();
                    fixture.Clock.Advance(TimeSpan.FromSeconds(60)); worker.RunOnceAsync(default).GetAwaiter().GetResult();
                }
                Require(fixture.Disconnects == 1 && store.LoadDisconnectBudget(fixture.Settings).State == DisconnectRequestState.RequestPending);
                fixture.BeforeDisconnect = null; fixture.Http.Online = false;
                using var restarted = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, new(directory)), fixture.Clock);
                Observe(restarted, fixture);
                fixture.Clock.Advance(TimeSpan.FromHours(1)); restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(fixture.Disconnects == 1);
            });
            return Task.CompletedTask;
        }),
        ("恢复持久安全：预算版本及每个必需字段均验证，完整旧结构迁移保留次数", () =>
        {
            AcceptanceRegression.WithStore((store, directory) =>
            {
                var settings = new MonitorFixture().Settings;
                var budget = new DisconnectBudget(2, DateTimeOffset.UtcNow.AddSeconds(120));
                store.SaveDisconnectBudget(settings, budget);
                var path = Directory.GetFiles(directory, "disconnect-*.json").Single();
                foreach (var field in new[] { "Version", "Attempts", "NextAttempt", "State", "CompletedAt", "Revision" })
                {
                    var data = JsonNode.Parse(JsonSerializer.Serialize(budget, Storage.Json))!.AsObject(); data.Remove(field);
                    File.WriteAllText(path, data.ToJsonString());
                    ExpectInvalid(() => store.LoadDisconnectBudget(settings));
                }
                foreach (var invalid in new[] { "{}", "null", "{broken", "{\"Attempts\":0}",
                    JsonSerializer.Serialize(budget with { Version = 99 }, Storage.Json),
                    JsonSerializer.Serialize(budget, Storage.Json).Replace("\"Attempts\": 2", "\"Attempts\": 2, \"Attempts\": 0") })
                {
                    File.WriteAllText(path, invalid); ExpectInvalid(() => store.LoadDisconnectBudget(settings));
                }
                File.WriteAllText(path, JsonSerializer.Serialize(new { budget.Attempts, budget.NextAttempt }));
                Require(store.LoadDisconnectBudget(settings).Attempts == 2);
                using var migrated = JsonDocument.Parse(File.ReadAllText(path));
                Require(migrated.RootElement.GetProperty("Version").GetInt32() == 1);
                var pending = budget with { State = DisconnectRequestState.RequestPending, CompletedAt = default, Revision = Guid.NewGuid() };
                Require(store.TryTransitionDisconnectBudget(settings, store.LoadDisconnectBudget(settings), pending));
                Require(!new SafetyStateStore(directory).TryTransitionDisconnectBudget(settings, budget, pending));
            });
            return Task.CompletedTask;
        }),
        ("恢复持久安全：预留后暂停切网DHCP认证阻断或需认证均取消请求", () =>
        {
            foreach (var mode in new[] { "paused", "disabled-disconnect", "other", "dhcp", "blocked", "needs-login" })
            AcceptanceRegression.WithStore((store, _) =>
            {
                var fixture = Stalled();
                var environment = new AcceptanceRegression.StoredEnvironment(fixture, store)
                {
                    AfterBudgetSave = () =>
                    {
                        switch (mode)
                        {
                            case "paused": fixture.Settings = fixture.Settings with { Enabled = false }; break;
                            case "disabled-disconnect": fixture.Settings = fixture.Settings with { AllowActiveDisconnect = false }; break;
                            case "other": fixture.Link = fixture.Link with { OtherProfile = true }; break;
                            case "dhcp": fixture.Link = fixture.Link with { Address = null }; break;
                            case "blocked": store.BlockAuthentication(new(PortalFailure.BindingRejected)); break;
                            case "needs-login": fixture.Http.PortalResult = "0"; break;
                        }
                    }
                };
                using var worker = new MonitorWorker(environment, fixture.Clock);
                Observe(worker, fixture);
                Require(fixture.Disconnects == 0 && fixture.Logs.Any(e => e.Action == "取消主动断线" && e.Detail.Contains("未发出")));
            });
            return Task.CompletedTask;
        }),
        ("恢复持久安全：最终WLAN读取期间取消仍零次断线", () =>
        {
            AcceptanceRegression.WithStore((store, _) =>
            {
                var fixture = Stalled(); using var cancellation = new CancellationTokenSource();
                var readsAfterReservation = 0;
                var environment = new AcceptanceRegression.StoredEnvironment(fixture, store)
                {
                    AfterBudgetSave = () => fixture.BeforeWifiRead = () => { if (++readsAfterReservation == 2) cancellation.Cancel(); }
                };
                using var worker = new MonitorWorker(environment, fixture.Clock);
                worker.RunOnceAsync(default).GetAwaiter().GetResult();
                fixture.Clock.Advance(TimeSpan.FromSeconds(15)); worker.RunOnceAsync(default).GetAwaiter().GetResult();
                fixture.Clock.Advance(TimeSpan.FromSeconds(15));
                try { worker.RunOnceAsync(cancellation.Token).GetAwaiter().GetResult(); throw new Exception("未传播取消"); }
                catch (OperationCanceledException) { }
                Require(fixture.Disconnects == 0 && store.LoadDisconnectBudget(fixture.Settings).State == DisconnectRequestState.RequestPending);
            });
            return Task.CompletedTask;
        }),
        ("恢复持久安全：原生请求边界区分IssuedSkippedFailed且不抢其他配置", () =>
        {
            var calls = 0;
            var other = new WifiLink(true, null, IPAddress.Parse("10.0.0.1"), 7, [], OtherProfile: true);
            foreach (var disconnect in new[] { false, true })
                Require(Wifi.Request(disconnect, () => other, () => { calls++; return 0; }).Status == WifiRequestStatus.Skipped);
            Require(calls == 0);
            var ready = other with { OtherProfile = false };
            Require(Wifi.Request(true, () => ready, () => 0).Status == WifiRequestStatus.Issued);
            var failed = Wifi.Request(true, () => ready, () => 5);
            Require(failed.Status == WifiRequestStatus.Failed && failed.NativeError == 5);
            var disconnected = ready with { Connected = false };
            Require(Wifi.Request(false, () => disconnected, () => 0).Status == WifiRequestStatus.Issued);
            Require(Wifi.Request(false, () => ready, () => 0).Status == WifiRequestStatus.Skipped);
            return Task.CompletedTask;
        }),
        ("恢复持久安全：独立开关保存后仍监测连接及认证", async () =>
        {
            var fixture = Stalled();
            fixture.Settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(fixture.Settings with { AllowActiveDisconnect = false }, Storage.Json), Storage.Json)!;
            Require(!fixture.Settings.AllowActiveDisconnect && fixture.Settings.Enabled);
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            Observe(worker, fixture);
            Require(fixture.Disconnects == 0);
            fixture.Link = fixture.Link with { Connected = false }; await worker.RunOnceAsync(default);
            Require(fixture.Connects == 1);
            fixture.Link = fixture.Link with { Connected = true }; fixture.Http.PortalResult = "0";
            Require((await worker.RunOnceAsync(default)).CredentialRequestStarted && fixture.Disconnects == 0);
        }),
        ("恢复持久安全：20秒写入延迟后完整两次请求仍至少间隔120秒", () =>
        {
            AcceptanceRegression.WithStore((store, _) =>
            {
                var fixture = Stalled(); var times = new List<DateTimeOffset>();
                fixture.BeforeDisconnect = () => times.Add(fixture.Clock.GetUtcNow());
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store)
                { AfterBudgetSave = () => { if (times.Count == 0) fixture.Clock.Advance(TimeSpan.FromSeconds(20)); } }, fixture.Clock);
                Observe(worker, fixture);
                Require(times.Count == 1 && store.LoadDisconnectBudget(fixture.Settings).CompletedAt == times[0]);
                fixture.Clock.Advance(TimeSpan.FromSeconds(119.999)); worker.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(times.Count == 1);
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(1)); worker.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(times.Count == 2 && times[1] - times[0] >= TimeSpan.FromSeconds(120));
            });
            return Task.CompletedTask;
        }),
        ("恢复持久安全：60秒清零写入失败不丢旧额度，恢复后才清零", () =>
        {
            AcceptanceRegression.WithStore((store, directory) =>
            {
                var fixture = Stalled(); var failReset = true;
                using (var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store)
                    { FailBudgetWrite = next => failReset && next.Attempts == 0 }, fixture.Clock))
                {
                    Observe(worker, fixture);
                    fixture.Http.Online = true; worker.RunOnceAsync(default).GetAwaiter().GetResult();
                    fixture.Clock.Advance(TimeSpan.FromSeconds(60)); worker.RunOnceAsync(default).GetAwaiter().GetResult();
                    Require(store.LoadDisconnectBudget(fixture.Settings).Attempts == 1);
                    failReset = false;
                    worker.RunOnceAsync(default).GetAwaiter().GetResult();
                    fixture.Clock.Advance(TimeSpan.FromSeconds(60)); worker.RunOnceAsync(default).GetAwaiter().GetResult();
                    Require(store.LoadDisconnectBudget(fixture.Settings).Attempts == 0);
                }
                Require(new SafetyStateStore(directory).LoadDisconnectBudget(fixture.Settings).Attempts == 0);
            });
            return Task.CompletedTask;
        })
    ];

    private static MonitorFixture Stalled() { var fixture = new MonitorFixture(); fixture.Http.PortalResult = "-1"; return fixture; }
    private static void Observe(MonitorWorker worker, MonitorFixture fixture)
    {
        worker.RunOnceAsync(default).GetAwaiter().GetResult();
        fixture.Clock.Advance(TimeSpan.FromSeconds(15)); worker.RunOnceAsync(default).GetAwaiter().GetResult();
        fixture.Clock.Advance(TimeSpan.FromSeconds(15)); worker.RunOnceAsync(default).GetAwaiter().GetResult();
    }
    private static void ExpectInvalid(Action run)
    {
        try { run(); } catch (Exception e) when (e is InvalidDataException or JsonException) { return; }
        throw new Exception("无效额度不能恢复为可用额度");
    }
    private static void Require(bool condition) { if (!condition) throw new Exception("恢复持久安全不变量失败"); }
}
