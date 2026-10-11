using System.Text.Json;
using AutoNet.Core;
using AutoNet.Service;

internal static class AcceptanceRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("最终验收：旧配置默认开启主动断线，30秒边界前不执行", async () =>
        {
            var fixture = Stalled();
            fixture.Settings = JsonSerializer.Deserialize<Settings>(
                "{\"InterfaceId\":\"00000000-0000-0000-0000-000000000001\",\"WifiProfile\":\"fixture\"}", Storage.Json)!;
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(29.999)); await worker.RunOnceAsync(default);
            Require(fixture.Disconnects == 0, "30秒前不得断线");
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(1)); await worker.RunOnceAsync(default);
            Require(fixture.Disconnects == 1, "旧配置应默认开启且恰满30秒允许第三轮请求");
        }),
        ("最终验收：用户能单独关闭主动断线并保留认证恢复", async () =>
        {
            var option = typeof(Settings).GetProperties().FirstOrDefault(p => p.PropertyType == typeof(bool) &&
                (p.Name.Contains("Disconnect", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Stalled", StringComparison.OrdinalIgnoreCase)));
            Require(option?.SetMethod is not null, "缺少独立主动断线配置；Enabled只能关闭全部恢复");
            var fixture = Stalled();
            option!.SetValue(fixture.Settings, false);
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await ObserveStallAsync(worker, fixture);
            Require(fixture.Disconnects == 0, "独立关闭后不应主动断线");
            fixture.Http.PortalResult = "0";
            Require((await worker.RunOnceAsync(default)).CredentialRequestStarted, "关闭主动断线不应关闭认证恢复");
        }),
        ("最终验收：最后失败探测后HTTPS恢复应取消破坏性请求", async () =>
        {
            var fixture = Stalled();
            fixture.Auxiliary = (call, _) =>
            {
                if (call == 2) fixture.Http.Online = true;
                return Task.FromResult(("系统路径样本", "UU会话未知"));
            };
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await ObserveStallAsync(worker, fixture);
            Require(fixture.AuxiliaryCalls == 2 && fixture.Http.Online, "夹具未在最后失败探测与动作间恢复HTTPS");
            Require(fixture.Disconnects == 0, "HTTPS已恢复仍发出断线请求");
        }),
        ("最终验收：认证与主动断线动作切换不绕过120秒冷却", async () =>
        {
            var fixture = Stalled();
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await ObserveStallAsync(worker, fixture);
            fixture.Http.PortalResult = "0";
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            Require((await worker.RunOnceAsync(default)).CredentialRequestStarted, "应优先恢复明确失效的认证");
            fixture.Http.PortalResult = "-1";
            await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(30)); await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(87)); await worker.RunOnceAsync(default);
            Require(fixture.Disconnects == 1, "其他动作不得缩短主动断线冷却");
            fixture.Clock.Advance(TimeSpan.FromSeconds(1)); await worker.RunOnceAsync(default);
            Require(fixture.Disconnects == 2, "满120秒才可再次请求");
        }),
        ("最终验收：存储延迟后两次实际断线请求仍相隔120秒", async () =>
        {
            var fixture = Stalled();
            var times = new List<DateTimeOffset>();
            fixture.BeforeDisconnect = () => times.Add(fixture.Clock.GetUtcNow());
            WithStore((store, _) =>
            {
                // The callback runs after the durable reservation, before the Windows boundary.
                var environment = new StoredEnvironment(fixture, store)
                {
                    AfterBudgetSave = () => { if (times.Count == 0) fixture.Clock.Advance(TimeSpan.FromSeconds(20)); }
                };
                using var worker = new MonitorWorker(environment, fixture.Clock);
                ObserveStallAsync(worker, fixture).GetAwaiter().GetResult();
                fixture.Clock.Advance(TimeSpan.FromSeconds(100)); worker.RunOnceAsync(default).GetAwaiter().GetResult();
            });
            Require(times.Count < 2 || times[1] - times[0] >= TimeSpan.FromSeconds(120),
                "实际请求仅间隔" + (times.Count < 2 ? "不足两次" : (times[1] - times[0]).TotalSeconds + "秒"));
            await Task.CompletedTask;
        }),
        ("最终验收：真实文件存储与Worker重建共同保留三次额度", () =>
        {
            WithStore((store, _) =>
            {
                var fixture = Stalled();
                var environment = new StoredEnvironment(fixture, store);
                using (var worker = new MonitorWorker(environment, fixture.Clock))
                {
                    ObserveStallAsync(worker, fixture).GetAwaiter().GetResult();
                    fixture.Clock.Advance(TimeSpan.FromSeconds(120)); worker.RunOnceAsync(default).GetAwaiter().GetResult();
                    fixture.Clock.Advance(TimeSpan.FromSeconds(120)); worker.RunOnceAsync(default).GetAwaiter().GetResult();
                }
                Require(store.LoadDisconnectBudget(fixture.Settings).Attempts == 3, "三次额度必须落盘");
                using var restarted = new MonitorWorker(new StoredEnvironment(fixture, store), fixture.Clock);
                restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                fixture.Clock.Advance(TimeSpan.FromHours(1)); restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                fixture.Clock.Advance(TimeSpan.FromHours(1)); restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(fixture.Disconnects == 3, "新Worker不得获得额外额度");
            });
            return Task.CompletedTask;
        }),
        ("最终验收：合法JSON但缺失额度字段不得静默恢复为零", () =>
        {
            WithStore((store, directory) =>
            {
                var settings = new MonitorFixture().Settings;
                store.SaveDisconnectBudget(settings, new(3, DateTimeOffset.UtcNow.AddSeconds(120)));
                var path = Directory.GetFiles(directory, "disconnect-*.json").Single();
                File.WriteAllText(path, "{}");
                try { store.LoadDisconnectBudget(settings); }
                catch (Exception e) when (e is InvalidDataException or JsonException) { return; }
                throw new Exception("缺失Attempts/NextAttempt的已有状态被当作零额度");
            });
            return Task.CompletedTask;
        }),
        ("最终验收：VLAN拒绝且持久写失败后重启不得再次提交凭据", async () =>
        {
            var fixture = new MonitorFixture { FailBlock = true };
            fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"只能绑定指定的VLAN设备\"}";
            using (var worker = new MonitorWorker(fixture, fixture.Clock))
            {
                Require((await worker.RunOnceAsync(default)).CredentialsBlocked, "本进程应阻断");
                fixture.Clock.Advance(TimeSpan.FromSeconds(120)); await worker.RunOnceAsync(default);
                Require(LoginRequests(fixture) == 1, "本进程不得重复提交");
            }
            using var restarted = new MonitorWorker(fixture, fixture.Clock);
            await restarted.RunOnceAsync(default);
            Require(LoginRequests(fixture) <= 1, "持久化失败后重启再次提交，累计" + LoginRequests(fixture) + "次");
        }),
        ("最终验收：实际落盘VLAN阻断跨Worker重建及凭据修改保留", () =>
        {
            WithStore((store, _) =>
            {
                var fixture = new MonitorFixture();
                fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"只能绑定指定的VLAN设备\"}";
                using (var worker = new MonitorWorker(new StoredEnvironment(fixture, store), fixture.Clock))
                    Require(worker.RunOnceAsync(default).GetAwaiter().GetResult().CredentialsBlocked, "首次拒绝应阻断");
                fixture.Credentials = new("changed-test", "changed-never-real");
                fixture.Settings = fixture.Settings with { KeepAwakeOnAc = false };
                using var restarted = new MonitorWorker(new StoredEnvironment(fixture, store), fixture.Clock);
                var status = restarted.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(status.CredentialsBlocked && status.AuthenticationFailure == "BindingRejected" && LoginRequests(fixture) == 1,
                    "落盘阻断不得被重启或凭据/设置变化解除");
            });
            return Task.CompletedTask;
        }),
        ("最终验收：临时检测异常不得清除已有认证退避", () =>
        {
            WithStore((store, _) =>
            {
                var fixture = new MonitorFixture(); fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"ldap timeout\"}";
                var environment = new StoredEnvironment(fixture, store);
                using var worker = new MonitorWorker(environment, fixture.Clock);
                worker.RunOnceAsync(default).GetAwaiter().GetResult();
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(500)); environment.FailRead = true;
                worker.RunOnceAsync(default).GetAwaiter().GetResult();
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(500)); environment.FailRead = false;
                worker.RunOnceAsync(default).GetAwaiter().GetResult();
                Require(LoginRequests(fixture) == 1, "尚未到2秒冷却，经异常后已再次提交");
            });
            return Task.CompletedTask;
        })
    ];

    private static MonitorFixture Stalled()
    {
        var fixture = new MonitorFixture(); fixture.Http.PortalResult = "-1";
        return fixture;
    }

    private static async Task ObserveStallAsync(MonitorWorker worker, MonitorFixture fixture)
    {
        await worker.RunOnceAsync(default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(15)); await worker.RunOnceAsync(default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(15)); await worker.RunOnceAsync(default);
    }

    private static int LoginRequests(MonitorFixture fixture) => fixture.Http.Requests.Count(u => u.AbsolutePath == "/eportal/portal/login");
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void WithStore(Action<SafetyStateStore, string> run)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AutoNet-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { run(new SafetyStateStore(directory), directory); }
        finally
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(directory);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理越界");
            Directory.Delete(target, true);
        }
    }

    internal sealed class StoredEnvironment(MonitorFixture fixture, SafetyStateStore store) : IMonitorEnvironment
    {
        public bool FailRead { get; set; }
        public Action? AfterBudgetSave { get; init; }
        public Func<AuthenticationBlock, bool>? FailAuthenticationWrite { get; init; }
        public bool FailAuthenticationRead { get; set; }
        public Func<DisconnectBudget, bool>? FailBudgetWrite { get; init; }
        public Settings? LoadSettings() => fixture.LoadSettings();
        public Credentials? LoadCredentials() => fixture.LoadCredentials();
        public AuthenticationBlock? LoadAuthenticationBlock() => FailAuthenticationRead ? throw new UnauthorizedAccessException("synthetic denial") : store.LoadAuthenticationBlock();
        public AuthenticationRecordKind AuthenticationRecordSource => store.LastAuthenticationRecordKind;
        public void BlockAuthentication(AuthenticationBlock block) => store.BlockAuthentication(block);
        public bool TryTransitionAuthentication(AuthenticationBlock? expected, AuthenticationBlock next) =>
            FailAuthenticationWrite?.Invoke(next) == true ? throw new IOException("synthetic state write failure") : store.TryTransitionAuthentication(expected, next);
        public DisconnectBudget LoadDisconnectBudget(Settings settings) => store.LoadDisconnectBudget(settings);
        public void SaveDisconnectBudget(Settings settings, DisconnectBudget budget)
        {
            store.SaveDisconnectBudget(settings, budget);
            AfterBudgetSave?.Invoke();
        }
        public bool TryTransitionDisconnectBudget(Settings settings, DisconnectBudget expected, DisconnectBudget next)
        {
            if (FailBudgetWrite?.Invoke(next) == true) throw new IOException("synthetic budget write failure");
            var changed = store.TryTransitionDisconnectBudget(settings, expected, next);
            if (changed) AfterBudgetSave?.Invoke();
            return changed;
        }
        public WifiLink ReadWifi(Settings settings) => FailRead ? throw new IOException("synthetic read failure") : fixture.ReadWifi(settings);
        public WifiRequestResult ConnectWifi(Settings settings) => fixture.ConnectWifi(settings);
        public WifiRequestResult DisconnectWifi(Settings settings) => fixture.DisconnectWifi(settings);
        public HttpClient CreateDirectClient(WifiLink link) => fixture.CreateDirectClient(link);
        public Task<(string SystemPath, string Uu)> CheckAuxiliaryAsync(Settings settings, WifiLink link, CancellationToken ct) => fixture.CheckAuxiliaryAsync(settings, link, ct);
        public void UpdatePower(bool enabled) => fixture.UpdatePower(enabled);
        public void WriteStatus(StatusSnapshot status) => fixture.WriteStatus(status);
        public void WriteLog(string action, string detail, double? seconds, StatusSnapshot? snapshot) => fixture.WriteLog(action, detail, seconds, snapshot);
        public void WriteDiagnostic(ActivityLog entry) => fixture.WriteDiagnostic(entry);
        public ActivityLog? ReadLastDiagnostic() => fixture.ReadLastDiagnostic();
        public void WriteDiagnosticFallback(string summary) => fixture.WriteDiagnosticFallback(summary);
    }
}
