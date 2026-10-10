using System.Net;
using AutoNet.Core;
using AutoNet.Service;

internal static class MonitorRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("监测：关联、DHCP、不可用网卡和用户切网不触发重连", async () =>
        {
            foreach (var link in new WifiLink[] {
                new(false, null, null, 7, [], Connecting: true), new(true, null, null, 7, []),
                new(false, null, null, 0, []), new(true, null, IPAddress.Parse("10.0.0.1"), 7, [], OtherProfile: true) })
            {
                var fixture = new MonitorFixture { Link = link };
                using var worker = new MonitorWorker(fixture, fixture.Clock);
                await worker.RunOnceAsync(default); fixture.Clock.Advance(TimeSpan.FromSeconds(20)); await worker.RunOnceAsync(default);
                Require(fixture.Connects == 0 && fixture.Http.Requests.Count == 0);
            }
        }),
        ("监测：认证未知或已认证上游故障不提交密码", async () =>
        {
            foreach (var state in new[] { "-1", "1" })
            {
                var fixture = new MonitorFixture(); fixture.Http.PortalResult = state;
                using var worker = new MonitorWorker(fixture, fixture.Clock);
                var status = await worker.RunOnceAsync(default);
                Require(!status.CredentialRequestStarted && fixture.Connects == 0 && fixture.Http.Requests.All(u => u.AbsolutePath != "/drcom/login"));
            }
        }),
        ("监测：日志和状态写入失败后主恢复继续", async () =>
        {
            var fixture = new MonitorFixture { Link = new(false, null, null, 7, []), FailStatus = true, FailLog = true };
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            var failed = await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(2)); await worker.RunOnceAsync(default);
            Require(fixture.Connects == 2 && failed.State != "检测异常" && !failed.LastError.Contains("secret"));
            fixture.FailStatus = fixture.FailLog = false;
            fixture.Clock.Advance(TimeSpan.FromSeconds(2)); var resumed = await worker.RunOnceAsync(default);
            Require(fixture.Connects == 3 && resumed.State != "检测异常");
        }),
        ("监测：辅助取消后重新检测", async () =>
        {
            var fixture = new MonitorFixture { Auxiliary = (n, _) => n == 1
                ? Task.FromCanceled<(string, string)>(new CancellationToken(true)) : Task.FromResult(("恢复后的系统路径", "会话待实测")) };
            fixture.Http.Online = true;
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default); var canceled = await worker.RunOnceAsync(default);
            Require(canceled.Tunnel.Contains("未知"));
            fixture.Clock.Advance(TimeSpan.FromSeconds(20)); await worker.RunOnceAsync(default);
            Require((await worker.RunOnceAsync(default)).Tunnel == "恢复后的系统路径");
        }),
        ("监测：辅助阻塞不阻塞恢复也不积累重叠检测", async () =>
        {
            var pending = new TaskCompletionSource<(string, string)>();
            var fixture = new MonitorFixture { Link = new(false, null, null, 7, []),
                Auxiliary = (n, _) => n == 1 ? pending.Task : Task.FromResult(("重新检测成功", "会话待实测")) };
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(4)); var timeout = await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(30)); await worker.RunOnceAsync(default);
            Require(timeout.Tunnel.Contains("超时") && fixture.Connects >= 2 && fixture.AuxiliaryCalls == 1);
            pending.SetResult(("过时结果", "过时结果"));
            var discarded = await worker.RunOnceAsync(default);
            Require(discarded.Tunnel != "过时结果");
            Require((await worker.RunOnceAsync(default)).Tunnel == "重新检测成功");
        }),
        ("监测：系统时间跳变不改变重试和恢复耗时", async () =>
        {
            var fixture = new MonitorFixture { Link = new(false, null, null, 7, []) };
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1), TimeSpan.FromHours(-2)); await worker.RunOnceAsync(default);
            Require(fixture.Connects == 1);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1), TimeSpan.FromHours(3)); await worker.RunOnceAsync(default);
            Require(fixture.Connects == 2);
            fixture.Link = new(true, null, IPAddress.Parse("10.0.0.1"), 7, []); fixture.Http.Online = true;
            fixture.Clock.Advance(TimeSpan.FromSeconds(3), TimeSpan.FromDays(1));
            Require((await worker.RunOnceAsync(default)).LastRecoverySeconds == 5);
        }),
        ("监测：电源API失败或代理异常不触发校园网修复", async () =>
        {
            var fixture = new MonitorFixture { FailPower = true, Auxiliary = (_, _) => Task.FromResult(("本机代理不可达", "会话未知")) };
            fixture.Http.Online = true;
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default); var status = await worker.RunOnceAsync(default);
            Require(status.Direct == "外网可达" && status.Tunnel == "本机代理不可达" && fixture.Connects == 0 && !status.CredentialRequestStarted);
            Require(status.LastError.Contains("电源"));
        }),
        ("Wi-Fi 恢复后首次认证不继承旧重连退避", () =>
        {
            var policy = new RecoveryPolicy();
            var now = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
            policy.Decide(new(false, false, false), now, true, true);
            policy.Attempted(now.AddMinutes(1));
            Require(policy.Decide(new(true, false, true), now.AddMinutes(1).AddSeconds(1), true, true) == ActionKind.Authenticate);
            return Task.CompletedTask;
        }),
        ("认证首页失败必须指出阶段且不发送凭据", async () =>
        {
            var handler = new MonitorHttp { FailurePath = "/", FailureStatus = HttpStatusCode.ServiceUnavailable };
            using var http = new HttpClient(handler);
            var result = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), default);
            Require(result.Stage == PortalStage.HomePage && result.Failure == PortalFailure.HttpStatus && result.HttpStatus == 503);
            Require(!result.CredentialRequestStarted && handler.Requests.All(r => r.AbsolutePath != "/drcom/login"));
        }),
        ("辅助检测异常后可重试且主恢复继续", async () =>
        {
            var fixture = new MonitorFixture { Link = new(false, null, null, 7, []) };
            fixture.Auxiliary = (_, _) => Task.FromException<(string, string)>(new IOException("fixture-secret-url"));
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            var failed = await worker.RunOnceAsync(default);
            Require(failed.Tunnel.Contains("未知") && !failed.LastError.Contains("secret"));
            fixture.Auxiliary = (_, _) => Task.FromResult(("系统路径恢复", "UU 会话待实测"));
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            await worker.RunOnceAsync(default);
            var recovered = await worker.RunOnceAsync(default);
            Require(recovered.Tunnel == "系统路径恢复" && fixture.Connects >= 2 && fixture.AuxiliaryCalls == 2);
        })
    ];

    private static void Require(bool condition) { if (!condition) throw new Exception("监测行为不符合预期"); }
}

internal sealed class MonitorFixture : IMonitorEnvironment
{
    public readonly ManualClock Clock = new();
    public Settings Settings { get; set; } = new() { InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "fixture" };
    public WifiLink Link { get; set; } = new(true, null, IPAddress.Parse("10.0.0.1"), 7, [IPAddress.Parse("10.0.0.2")]);
    public readonly MonitorHttp Http = new();
    public Func<int, CancellationToken, Task<(string, string)>> Auxiliary { get; set; } = (_, _) => Task.FromResult(("系统路径样本未知", "UU 会话待实测"));
    public int AuxiliaryCalls { get; private set; }
    public int Connects { get; private set; }
    public bool FailStatus { get; set; }
    public bool FailLog { get; set; }
    public bool FailPower { get; set; }
    public bool Blocked { get; set; }
    public bool FailBlock { get; set; }
    public Settings? LoadSettings() => Settings;
    public Credentials? LoadCredentials() => new("test", "never-real");
    public string CredentialFingerprint() => "fixture-fingerprint";
    public bool AuthenticationBlocked(string fingerprint) => Blocked;
    public void BlockAuthentication(string fingerprint)
    {
        if (FailBlock) throw new UnauthorizedAccessException("fixture-secret-url");
        Blocked = true;
    }
    public WifiLink ReadWifi(Settings settings) => Link;
    public void ConnectWifi(Settings settings) => Connects++;
    public HttpClient CreateDirectClient(WifiLink link) => new(Http, disposeHandler: false);
    public Task<(string SystemPath, string Uu)> CheckAuxiliaryAsync(Settings settings, WifiLink link, CancellationToken ct) => Auxiliary(++AuxiliaryCalls, ct);
    public void UpdatePower(bool enabled) { if (FailPower) throw new System.ComponentModel.Win32Exception(5); }
    public void WriteStatus(StatusSnapshot status) { if (FailStatus) throw new UnauthorizedAccessException("fixture-secret-url"); }
    public void WriteLog(string action, string detail, double? seconds, StatusSnapshot? snapshot) { if (FailLog) throw new IOException("fixture-secret-url"); }
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset utc = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private long timestamp;
    public override DateTimeOffset GetUtcNow() => utc;
    public override long GetTimestamp() => timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan elapsed, TimeSpan? wallChange = null) { timestamp += elapsed.Ticks; utc += wallChange ?? elapsed; }
}

internal sealed class MonitorHttp : HttpMessageHandler
{
    public bool Online { get; set; }
    public string PortalResult { get; set; } = "0";
    public string LoginResult { get; set; } = "{\"result\":1}";
    public string HomePage { get; set; } = "<script>v4ip='10.0.0.1';</script>";
    public string ConfigJson { get; set; } = "{\"data\":{\"en_md5\":0,\"password_cut\":0,\"enable_r3\":0}}";
    public string? FailurePath { get; set; }
    public HttpStatusCode FailureStatus { get; set; }
    public Exception? FailureException { get; set; }
    public List<Uri> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri);
        if (uri.AbsolutePath == FailurePath)
        {
            if (FailureException is not null) return Task.FromException<HttpResponseMessage>(FailureException);
            var failure = new HttpResponseMessage(FailureStatus) { Content = new StringContent("fixture-secret-response") };
            failure.Headers.Location = new Uri("https://untrusted.example/redirect");
            return Task.FromResult(failure);
        }
        var body = uri.AbsolutePath switch
        {
            "/robots.txt" => Online ? "User-agent: *" : "offline",
            "/drcom/chkstatus" => "{\"result\":" + PortalResult + "}",
            "/" => HomePage,
            "/eportal/portal/page/loadConfig" => ConfigJson,
            "/drcom/login" => LoginResult,
            _ => throw new InvalidOperationException("未知夹具请求")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
