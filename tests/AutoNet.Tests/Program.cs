using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using AutoNet.Core;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var tests = new (string Name, Func<Task> Run)[]
{
    ("正常联网不执行修复", () => Sync(() => {
        var p = new RecoveryPolicy(); Equal(ActionKind.None, p.Decide(new(true, true, false), Now(), true, true)); Equal(0, p.Attempts);
    })),
    ("断连请求重连而非认证", () => Sync(() => {
        var p = new RecoveryPolicy(); Equal(ActionKind.ReconnectWifi, p.Decide(new(false, false, false), Now(), true, true));
    })),
    ("认证失效立即认证且首轮无需等待", () => Sync(() => {
        var p = new RecoveryPolicy(); Equal(ActionKind.Authenticate, p.Decide(new(true, false, true), Now(), true, true));
    })),
    ("校园网已认证但上游故障不重复提交密码", () => Sync(() => {
        Equal(ActionKind.None, new RecoveryPolicy().Decide(new(true, false, false), Now(), true, true));
    })),
    ("暂停和凭据缺失时不提交认证", () => Sync(() => {
        Equal(ActionKind.None, new RecoveryPolicy().Decide(new(true, false, true), Now(), false, true));
        Equal(ActionKind.None, new RecoveryPolicy().Decide(new(true, false, true), Now(), true, false));
    })),
    ("快重试、退避和恢复后重置", () => Sync(() => {
        var p = new RecoveryPolicy(); var now = Now(); p.Decide(new(false, false, false), now, true, true);
        p.Attempted(now); Equal(now.AddSeconds(2), p.NextAttempt);
        Equal(ActionKind.None, p.Decide(new(false, false, false), now.AddSeconds(1), true, true));
        for (var i = 0; i < 12; i++) p.Attempted(now.AddSeconds(20 + i * 60));
        Equal(TimeSpan.FromSeconds(60), p.NextAttempt - now.AddSeconds(20 + 11 * 60));
        p.Decide(new(true, true, false), now.AddHours(1), true, true); Equal(0, p.Attempts); True(p.OutageSince is null);
    })),
    ("明确凭据错误后持续检测且不再认证", () => Sync(() => {
        var p = new RecoveryPolicy { CredentialsBlocked = true };
        Equal(ActionKind.None, p.Decide(new(true, false, true), Now(), true, true));
        p.Decide(new(true, true, false), Now(), true, true);
        True(p.CredentialsBlocked);
        Equal(ActionKind.ReconnectWifi, p.Decide(new(false, false, false), Now(), true, true));
    })),
    ("只识别已核实的凭据错误", () => Sync(() => {
        foreach (var message in new[] { "userid error1", "userid error2", "auth error99", "auth error198", "密码错误" })
        { using var j = JsonDocument.Parse(JsonSerializer.Serialize(new { result = 0, msg = message })); Equal(LoginResult.RejectedCredentials, DrcomPortal.Classify(j.RootElement)); }
        using var unknown = JsonDocument.Parse("{\"result\":0,\"Msg\":\"ldap timeout\"}"); Equal(LoginResult.Rejected, DrcomPortal.Classify(unknown.RootElement));
        using var captcha = JsonDocument.Parse("{\"result\":0,\"msg\":\"captcha required\"}"); Equal(LoginResult.NeedsInteraction, DrcomPortal.Classify(captcha.RootElement));
    })),
    ("JSONP 解析不执行页面代码", () => Sync(() => {
        using var j = DrcomPortal.ParseJsonp("dr1001({\"result\":1});"); Equal(1, j.RootElement.GetProperty("result").GetInt32());
        Throws<InvalidDataException>(() => DrcomPortal.ParseJsonp("evil(); dr1001({\"result\":1});"));
    })),
    ("凭据完整编码而非直接拼接查询串", () => Sync(() => {
        var gateway = new DrcomPortal.GatewayContext("10.0.0.1", "192.168.100.254", "TEST-AP-1", "aabb");
        var q = DrcomPortal.BuildLoginQuery(new("测试&account", "p&=#?+ %"), "10.0.0.1", gateway);
        True(q.Contains("user_password=p%26%3D%23%3F%2B%20%25")); True(!q.Contains("captcha="));
        True(q.Contains("user_account=%2C0%2C%E6%B5%8B%E8%AF%95%26account"));
        True(q.Contains("wlan_user_mac=aabb") && q.Contains("wlan_ac_name=TEST-AP-1"));
    })),
    ("真实适配路径：状态、配置、认证", async () => {
        var handler = new PortalFixture(); using var http = new HttpClient(handler);
        var result = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginAsync(new("test", "never-real"), default);
        Equal(LoginResult.Accepted, result); Equal(4, handler.Calls.Count);
        True(handler.Calls[0].AbsolutePath == "/drcom/chkstatus"); True(handler.Calls[1].AbsolutePath == "/connecttest.txt");
        True(handler.Calls[2].Port == 802); True(handler.Calls[3].AbsolutePath == "/eportal/portal/login");
        // Only the gateway probe leaves the portal host, and it is plain HTTP on purpose.
        // Asserted by exclusion rather than by call index so a new stage cannot pass for the wrong reason.
        True(handler.Calls.Count(u => u.Host != "login.csust.edu.cn") == 1 &&
            handler.Calls.Single(u => u.Host != "login.csust.edu.cn").Scheme == "http" &&
            handler.Calls.Where(u => u.Host == "login.csust.edu.cn").All(u => u.Scheme == "https"));
        // The credential-bearing request is the last one, and nothing else carries the password.
        True(handler.Calls.Count(u => u.Query.Contains("user_password")) == 1);
    }),
    ("已经在线时不会发送凭据", async () => {
        var handler = new PortalFixture { AlreadyOnline = true }; using var http = new HttpClient(handler);
        await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginAsync(new("test", "never-real"), default);
        Equal(1, handler.Calls.Count); True(!handler.Calls[0].Query.Contains("user_password"));
    }),
    ("认证模式变化时不猜测登录", async () => {
        var handler = new PortalFixture { ChangedMode = true }; using var http = new HttpClient(handler);
        Equal(LoginResult.Unsupported, await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginAsync(new("test", "never-real"), default));
        True(handler.Calls.All(u => !u.Query.Contains("user_password")));
    }),
    ("认证配置缺失、空值、异常结构均不发送凭据", async () => {
        foreach (var data in new[] {
            "{}", "null", "[]", "0", "\"0\"",
            "{\"en_md5\":0,\"password_cut\":0}",
            "{\"en_md5\":null,\"password_cut\":0,\"enable_r3\":0}",
            "{\"en_md5\":\"\",\"password_cut\":0,\"enable_r3\":0}",
            "{\"en_md5\":false,\"password_cut\":0,\"enable_r3\":0}",
            "{\"en_md5\":[],\"password_cut\":0,\"enable_r3\":0}",
            "{\"en_md5\":0,\"en_md5\":1,\"password_cut\":0,\"enable_r3\":0}" })
        {
            var handler = new PortalFixture { ConfigJson = "dr1001({\"data\":" + data + "});" };
            using var http = new HttpClient(handler);
            Equal(LoginResult.Unsupported, await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginAsync(new("test", "never-real"), default));
            True(handler.Calls.All(u => u.AbsolutePath != "/eportal/portal/login" && !u.Query.Contains("user_password")));
        }
    }),
    ("重定向和伪造成功页不视为外网在线", async () => {
        foreach (var status in new[] { HttpStatusCode.Redirect, HttpStatusCode.OK })
        {
            using var http = new HttpClient(new StaticHandler(status, "<html>login</html>"));
            True(!await HealthChecks.InternetAsync(http, default));
        }
    }),
    ("DNS 压缩记录、错误事务和截断响应", () => Sync(() => {
        var query = DnsPacket.Query("example.com", 123);
        var response = query.Concat(new byte[] { 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 20, 0, 4, 1, 2, 3, 4 }).ToArray();
        response[2] = 0x81; response[3] = 0x80; response[7] = 1;
        Equal("1.2.3.4", DnsPacket.Parse(response, 123).Single().ToString());
        Throws<InvalidDataException>(() => DnsPacket.Parse(response, 321));
        for (var length = 0; length < response.Length; length++)
        { var truncated = response[..length]; Throws<InvalidDataException>(() => DnsPacket.Parse(truncated, 123)); }
    })),
    ("设置拒绝不安全或带令牌的地址", () => Sync(() => {
        var valid = new Settings { InterfaceId = Guid.NewGuid().ToString(), WifiProfile = "test" }; valid.Validate();
        Throws<ArgumentException>(() => (valid with { PortalOrigin = "http://example.com" }).Validate());
        Throws<ArgumentException>(() => (valid with { PortalOrigin = "https://example.com/login?token=x" }).Validate());
        Throws<ArgumentException>(() => (valid with { ProxyUrl = "http://remote.example.com:8080" }).Validate());
        Throws<ArgumentException>(() => (valid with { UuEndpoint = "https://example.com/?token=x" }).Validate());
        foreach (var address in new[] { "https://evil.example", "https://login.csust.edu.cn.evil.example", "https://login.csust.edu.cn:802", "https://login.csust.edu.cn:444", "https://user@login.csust.edu.cn", "https://login.csust.edu.cn/#x", "https://login.csust.edu.cn/?x=y", "https://login.csust.edu.cn./" })
            Throws<ArgumentException>(() => (valid with { PortalOrigin = address }).Validate());
    }))
};
tests = tests.Concat(MonitorRegression.Cases).ToArray();
tests = tests.Concat(PortalRegression.Cases).ToArray();
tests = tests.Concat(NetworkRegression.Cases).ToArray();
tests = tests.Concat(LogRegression.Cases).ToArray();
tests = tests.Concat(DiagnosticRegression.Cases).ToArray();
tests = tests.Concat(DiagnosticsPackageRegression.Cases).ToArray();
tests = tests.Concat(SafetyRegression.Cases).ToArray();
tests = tests.Concat(AcceptanceRegression.Cases).ToArray();
tests = tests.Concat(AuthenticationPersistenceRegression.Cases).ToArray();
tests = tests.Concat(RecoveryPersistenceRegression.Cases).ToArray();
tests = tests.Concat(DisplayRegression.Cases).ToArray();
tests = tests.Concat(LogCentreRegression.Cases).ToArray();
tests = tests.Concat(TrayRegression.Cases).ToArray();
tests = tests.Concat(UiFreezeRegression.Cases).ToArray();
tests = tests.Concat(AvaloniaPrototypeRegression.Cases).ToArray();
tests = tests.Concat(AuthChannelRegression.Cases).ToArray();
tests = tests.Concat(AvaloniaShellRegression.Cases).ToArray();
tests = tests.Concat(OverviewRegression.Cases).ToArray();
tests = tests.Concat(UnblockRegression.Cases).ToArray();
tests = tests.Concat(DiagnosticsPageRegression.Cases).ToArray();
tests = tests.Concat(LogsPageRegression.Cases).ToArray();
tests = tests.Concat(SettingsPageRegression.Cases).ToArray();
tests = tests.Concat(ThemePreferenceRegression.Cases).ToArray();
tests = tests.Concat(NotificationRouterRegression.Cases).ToArray();
tests = tests.Concat(PortalOpenRegression.Cases).ToArray();
tests = tests.Concat(DiagnosticsExporterRegression.Cases).ToArray();
tests = tests.Concat(AccessibilityRegression.Cases).ToArray();

if (args.Length != 0 && (args.Length != 2 || args[0] != "--filter"))
{
    Console.WriteLine("用法：AutoNet.Tests [--filter 测试名称片段]");
    return 2;
}
var selected = args.Length == 0 ? tests : tests.Where(t => t.Name.Contains(args[1], StringComparison.Ordinal)).ToArray();
if (selected.Length == 0) { Console.WriteLine("没有匹配的测试。"); return 2; }
var failures = 0;
foreach (var (name, run) in selected)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
Console.WriteLine($"{selected.Length - failures}/{selected.Length} passed");
return failures == 0 ? 0 : 1;

static DateTimeOffset Now() => new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
static Task Sync(Action action) { action(); return Task.CompletedTask; }
static void True(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

sealed class PortalFixture : HttpMessageHandler
{
    public List<Uri> Calls { get; } = [];
    public bool AlreadyOnline { get; init; }
    public bool ChangedMode { get; init; }
    public string? ConfigJson { get; init; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!; Calls.Add(uri);
        if (uri.AbsolutePath == "/connecttest.txt")
        {
            var redirected = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
            redirected.Headers.Location = new Uri(MonitorHttp.GatewayRedirect);
            return Task.FromResult(redirected);
        }
        var body = uri.AbsolutePath switch
        {
            "/drcom/chkstatus" => "dr1001({\"result\":" + (AlreadyOnline ? "1" : "0") + ",\"v4ip\":\"10.0.0.1\"});",
            "/" => "<html><script>v4ip='10.0.0.1';</script></html>",
            "/eportal/portal/page/loadConfig" => ConfigJson ?? "dr1001({\"code\":200,\"data\":{\"en_md5\":" + (ChangedMode ? "1" : "0") + ",\"password_cut\":0,\"enable_r3\":0}});",
            "/eportal/portal/login" => "dr1003({\"result\":1});",
            _ => throw new Exception("Unexpected fixture path")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}

sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
}
