using System.Net;
using AutoNet.Core;
using AutoNet.Service;

internal static class PortalRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("认证加固：网关超时或传输失败退避后可恢复", async () =>
        {
            foreach (var failure in new Exception[] { new TaskCanceledException("fixture-secret-url"), new HttpRequestException("fixture-secret-url") })
            {
                var fixture = new MonitorFixture();
                fixture.Http.FailurePath = "/connecttest.txt";
                fixture.Http.FailureException = failure;
                using var worker = new MonitorWorker(fixture, fixture.Clock);
                var first = await worker.RunOnceAsync(default);
                Require(first.AuthenticationStage == "GatewayRedirect" && first.AuthenticationFailure == (failure is TaskCanceledException ? "Timeout" : "Transport"));
                Require(!first.CredentialsBlocked && !first.CredentialRequestStarted && first.NextAttempt is not null);
                Require(!first.Detail.Contains("secret") && !first.LastError.Contains("secret"));
                fixture.Http.FailurePath = null;
                fixture.Clock.Advance(TimeSpan.FromSeconds(2));
                Require((await worker.RunOnceAsync(default)).CredentialRequestStarted);
            }
        }),
        ("认证加固：网关读取期间调用方取消必须传播且不提交", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var handler = new MonitorHttp();
            using var http = new HttpClient(new GatewayCancellationHandler(cancellation) { InnerHandler = handler });
            var cancelled = false;
            try { await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
            Require(cancelled && handler.Requests.All(u => !u.Query.Contains("user_password")));
        }),
        ("认证加固：网关 IPv4 优先且有效上下文只向固定 HTTPS 端点提交一次", async () =>
        {
            var handler = new MonitorHttp { StatusClientIpv4 = "10.9.9.9" };
            using var http = new HttpClient(handler);
            var portal = new DrcomPortal(http, new Uri("https://login.csust.edu.cn"));
            var result = await portal.LoginDetailedAsync(new("test", "never-real"), default);
            Require(result.Result == LoginResult.Accepted && result.CredentialRequestStarted);
            var requests = handler.Requests.Where(u => u.Query.Contains("user_password")).ToArray();
            Require(requests.Length == 1 && requests[0].Scheme == "https" && requests[0].Host == "login.csust.edu.cn" && requests[0].Port == 802);
            Require(requests[0].AbsolutePath == "/eportal/portal/login" && requests[0].Query.Contains("wlan_user_ip=10.0.0.1"));
            handler.Requests.Clear();
            handler.PortalResult = "1";
            var already = await portal.LoginDetailedAsync(new("test", "never-real"), default);
            Require(already.Failure == PortalFailure.AlreadyAuthenticated && !already.CredentialRequestStarted && handler.Requests.Count == 1);
        }),
        ("认证加固：历史暂停及明确凭据拒绝和人工验证仍跨重启保留", async () =>
        {
            foreach (var message in new string?[] { null, "密码错误", "captcha" })
            {
                var fixture = new MonitorFixture { Blocked = message is null };
                if (message is not null) fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"" + message + "\"}";
                using (var worker = new MonitorWorker(fixture, fixture.Clock))
                {
                    var first = await worker.RunOnceAsync(default);
                    Require(first.CredentialsBlocked && fixture.Blocked && first.CredentialRequestStarted == (message is not null));
                    if (message is null) Require(first.Detail.Contains("历史"));
                }
                var requests = fixture.Http.Requests.Count(u => u.Query.Contains("user_password"));
                fixture.Http.LoginResult = "{\"result\":1}";
                fixture.Clock.Advance(TimeSpan.FromMinutes(5));
                using var restarted = new MonitorWorker(fixture, fixture.Clock);
                Require((await restarted.RunOnceAsync(default)).CredentialsBlocked);
                Require(fixture.Http.Requests.Count(u => u.Query.Contains("user_password")) == requests);
            }
        }),
        ("认证加固：重复网关字段和无效必需参数不能开启凭据提交", async () =>
        {
            foreach (var field in new[] { "wlanuserip", "wlanacip", "wlanacname", "wlanusermac" })
            foreach (var suffix in new[] { "=", "=different", "" })
            {
                var handler = new MonitorHttp { GatewayLocation = MonitorHttp.GatewayRedirect + "&" + field + suffix };
                using var http = new HttpClient(handler);
                var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), default);
                Require(outcome.Stage == PortalStage.GatewayRedirect && outcome.Failure == PortalFailure.InvalidResponse && outcome.Result == LoginResult.Rejected);
                Require(!outcome.CredentialRequestStarted && handler.Requests.All(u => !u.Query.Contains("user_password")));
            }
            foreach (var (field, value) in new[] {
                ("wlanacip", "not-an-ip"), ("wlanacip", "::1"), ("wlanacip", "123"), ("wlanacip", "0.0.0.0"),
                ("wlanacip", "127.0.0.1"), ("wlanacip", "255.255.255.255"), ("wlanacip", "224.0.0.1"),
                ("wlanusermac", ""), ("wlanusermac", " "), ("wlanusermac", "synthetic\nvalue") })
            {
                var query = "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=" +
                    Uri.EscapeDataString(field == "wlanacip" ? value : "192.168.100.254") + "&wlanusermac=" +
                    Uri.EscapeDataString(field == "wlanusermac" ? value : "synthetic");
                var handler = new MonitorHttp { GatewayLocation = query };
                using var http = new HttpClient(handler);
                var portal = new DrcomPortal(http, new Uri("https://login.csust.edu.cn"));
                var diagnosis = await portal.DiagnoseAsync(default);
                var login = await portal.LoginDetailedAsync(new("test", "never-real"), default);
                Require(diagnosis.Failure == PortalFailure.MissingGatewayParameters && login.Failure == diagnosis.Failure);
                Require(!diagnosis.CredentialRequestStarted && !login.CredentialRequestStarted);
                Require(!diagnosis.SafeSummary.Contains(value, StringComparison.Ordinal) || value.Length < 4);
            }
        }),
        ("认证加固：诊断与登录对缺少 MAC 或 AC 给出相同失败且不提交", async () =>
        {
            foreach (var location in new string?[] { null,
                "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254",
                "https://login.csust.edu.cn/?wlanuserip=10.0.0.1&wlanusermac=synthetic" })
            {
                var handler = new MonitorHttp { GatewayLocation = location };
                using var http = new HttpClient(handler);
                var portal = new DrcomPortal(http, new Uri("https://login.csust.edu.cn"));
                var diagnosis = await portal.DiagnoseAsync(default);
                var login = await portal.LoginDetailedAsync(new("test", "never-real"), default);
                Require(diagnosis.Failure == PortalFailure.MissingGatewayParameters && diagnosis.Result == LoginResult.Rejected);
                Require(diagnosis.Stage == login.Stage && diagnosis.Failure == login.Failure && diagnosis.Result == login.Result);
                Require(!diagnosis.CredentialRequestStarted && !login.CredentialRequestStarted);
                Require(handler.Requests.All(u => !u.Query.Contains("user_password")));
            }
        }),
        ("认证加固：相对网关地址受控失败并在冷却后重新检测", async () =>
        {
            var fixture = new MonitorFixture();
            fixture.Http.GatewayLocation = "/?wlanuserip=10.0.0.1&wlanacip=192.168.100.254&wlanusermac=synthetic";
            using var http = new HttpClient(fixture.Http, false);
            var portal = new DrcomPortal(http, new Uri("https://login.csust.edu.cn"));
            var login = await portal.LoginDetailedAsync(new("test", "never-real"), default);
            var diagnosis = await portal.DiagnoseAsync(default);
            Require(login.Stage == PortalStage.GatewayRedirect && login.Failure == PortalFailure.InvalidTarget && diagnosis.Failure == login.Failure);
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            var first = await worker.RunOnceAsync(default);
            Require(first.State != "检测异常" && first.AuthenticationStage == "GatewayRedirect" && first.AuthenticationFailure == "InvalidTarget");
            Require(!first.CredentialsBlocked && !first.CredentialRequestStarted && first.Attempts == 1 && first.NextAttempt is not null);
            var probes = fixture.Http.Requests.Count(u => u.AbsolutePath == "/connecttest.txt");
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var cooling = await worker.RunOnceAsync(default);
            Require(cooling.Attempts == 1 && fixture.Http.Requests.Count(u => u.AbsolutePath == "/connecttest.txt") == probes);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var retried = await worker.RunOnceAsync(default);
            Require(retried.Attempts == 2 && !retried.CredentialsBlocked && retried.AuthenticationStage == "GatewayRedirect");
            fixture.Http.GatewayLocation = MonitorHttp.GatewayRedirect;
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            Require((await worker.RunOnceAsync(default)).CredentialRequestStarted);
        }),
        ("认证加固：异常网关状态和目标不允许提交且保留退避", async () =>
        {
            foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.InternalServerError, HttpStatusCode.MovedPermanently })
            {
                var fixture = new MonitorFixture();
                fixture.Http.GatewayStatus = status;
                using var worker = new MonitorWorker(fixture, fixture.Clock);
                var outcome = await worker.RunOnceAsync(default);
                Require(outcome.AuthenticationStage == "GatewayRedirect" && outcome.AuthenticationFailure == "HttpStatus" && outcome.AuthenticationHttpStatus == (int)status);
                Require(!outcome.CredentialRequestStarted && !outcome.CredentialsBlocked && outcome.Attempts == 1 && outcome.NextAttempt is not null);
                Require(fixture.Http.Requests.All(u => !u.Query.Contains("user_password")));
            }
            foreach (var target in new[] {
                "https://untrusted.example/", "http://login.csust.edu.cn/", "https://login.csust.edu.cn:802/",
                "https://login.csust.edu.cn/other", "https://user@login.csust.edu.cn/", "https://login.csust.edu.cn./",
                "https://login.csust.edu.cn.evil.example/", "https://login.csust.edu.cn/#fragment" })
            {
                var handler = new MonitorHttp { GatewayLocation = target + MonitorHttp.GatewayRedirect[MonitorHttp.GatewayRedirect.IndexOf('?')..] };
                using var http = new HttpClient(handler);
                var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), default);
                Require(outcome.Stage == PortalStage.GatewayRedirect && outcome.Failure == PortalFailure.InvalidTarget && outcome.Result == LoginResult.Rejected);
                Require(!outcome.CredentialRequestStarted && handler.Requests.All(u => !u.Query.Contains("user_password")));
                Require(!outcome.SafeSummary.Contains("10.0.0.1") && !outcome.SafeSummary.Contains("never-real"));
            }
        }),
        ("认证加固：临时缺 IPv4 退避后恢复且不写入持久暂停", async () =>
        {
            var fixture = new MonitorFixture();
            fixture.Http.GatewayLocation = null;
            fixture.Http.StatusClientIpv4 = null;
            fixture.Http.HomePage = "<html>no-address</html>";
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            var first = await worker.RunOnceAsync(default);
            Require(first.AuthenticationFailure == "MissingClientIpv4" && !first.CredentialRequestStarted);
            Require(!first.CredentialsBlocked && !fixture.Blocked && first.Attempts == 1 && first.NextAttempt is not null);
            var probes = fixture.Http.Requests.Count(u => u.AbsolutePath == "/connecttest.txt");
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var cooling = await worker.RunOnceAsync(default);
            Require(cooling.Attempts == 1 && fixture.Http.Requests.Count(u => u.AbsolutePath == "/connecttest.txt") == probes);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var missing = await worker.RunOnceAsync(default);
            Require(missing.Attempts == 2 && !missing.CredentialsBlocked && !missing.CredentialRequestStarted);
            fixture.Http.GatewayLocation = MonitorHttp.GatewayRedirect;
            fixture.Http.StatusClientIpv4 = "10.0.0.1";
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
            var recovered = await worker.RunOnceAsync(default);
            Require(!recovered.CredentialsBlocked && recovered.CredentialRequestStarted && recovered.AuthenticationAcceptedAt is not null);
            Require(fixture.Http.Requests.Count(u => u.Query.Contains("user_password")) == 1);
        }),
        ("认证诊断：暂停标记写入恢复后补写且期间不再认证", async () =>
        {
            var fixture = new MonitorFixture { FailBlock = true };
            fixture.Http.ConfigJson = "{\"data\":{\"en_md5\":1,\"password_cut\":0,\"enable_r3\":0}}";
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            Require((await worker.RunOnceAsync(default)).CredentialsBlocked && !fixture.Blocked);
            fixture.FailBlock = false; fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            await worker.RunOnceAsync(default);
            Require(fixture.Blocked && fixture.Http.Requests.All(u => u.AbsolutePath != "/eportal/portal/login"));
        }),
        ("认证诊断：每个模式字段严格接受数字零或字符串零", async () =>
        {
            foreach (var field in new[] { "en_md5", "password_cut", "enable_r3" })
            foreach (var value in new[] { "0", "\"0\"", "1", "null", "false", "\"\"", "{}", "[]" })
            {
                var fields = new[] { "en_md5", "password_cut", "enable_r3" }.Select(name => "\"" + name + "\":" + (name == field ? value : "0"));
                var handler = new MonitorHttp { ConfigJson = "{\"data\":{" + string.Join(',', fields) + "}}" };
                using var http = new HttpClient(handler);
                var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).DiagnoseAsync(default);
                Require((outcome.Failure == PortalFailure.None) == (value is "0" or "\"0\""));
                Require(!outcome.CredentialRequestStarted);
            }
        }),
        ("认证诊断：只读前置检查通过也不提交凭据", async () =>
        {
            var handler = new MonitorHttp();
            using var http = new HttpClient(handler);
            var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).DiagnoseAsync(default);
            Require(outcome.Stage == PortalStage.Complete && outcome.Failure == PortalFailure.None);
            Require(outcome.Flags?.Count == 3 && outcome.Flags.Values.All(v => v == "Zero"));
            Require(!outcome.CredentialRequestStarted && handler.Requests.All(u => u.AbsolutePath != "/eportal/portal/login" && !u.Query.Contains("user_password")));
        }),
        ("认证诊断：IPv4、802、结构、模式和提交端点分别定位", async () =>
        {
            foreach (var scenario in new[] { "ip", "802", "structure", "mode", "gateway" })
            {
                var handler = new MonitorHttp();
                var expectedStage = PortalStage.ClientIpv4;
                var expectedFailure = PortalFailure.MissingClientIpv4;
                switch (scenario)
                {
                    case "ip": handler.StatusClientIpv4 = null; handler.GatewayLocation = null; handler.HomePage = "<html>fixture-secret-response</html>"; break;
                    case "802": handler.FailurePath = "/eportal/portal/page/loadConfig"; handler.FailureStatus = HttpStatusCode.BadGateway; expectedStage = PortalStage.Configuration; expectedFailure = PortalFailure.HttpStatus; break;
                    case "structure": handler.ConfigJson = "{\"data\":null}"; expectedStage = PortalStage.Configuration; expectedFailure = PortalFailure.MalformedConfiguration; break;
                    case "mode": handler.ConfigJson = "{\"data\":{\"en_md5\":1,\"password_cut\":0,\"enable_r3\":0}}"; expectedStage = PortalStage.AuthenticationMode; expectedFailure = PortalFailure.UnsupportedFlag; break;
                    // No gateway context means no MAC token; submitting would be rejected by the portal.
                    case "gateway": handler.GatewayLocation = null; expectedStage = PortalStage.Submission; expectedFailure = PortalFailure.MissingGatewayParameters; break;
                }
                using var http = new HttpClient(handler);
                var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), default);
                Require(outcome.Stage == expectedStage && outcome.Failure == expectedFailure);
                Require(!outcome.CredentialRequestStarted);
                Require(!outcome.SafeSummary.Contains("secret") && !outcome.SafeSummary.Contains("never-real"));
            }
        }),
        ("认证诊断：客户端 IPv4 取自状态响应，不依赖首页", async () =>
        {
            // Regression: the home page only carries v4ip while a session exists, so scraping
            // it there failed exactly when login was needed. The status response always has it.
            var handler = new MonitorHttp { HomePage = "<html>no-client-address-here</html>" };
            using var http = new HttpClient(handler);
            var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), default);
            Require(outcome.Stage == PortalStage.Submission && outcome.Result == LoginResult.Accepted);
            Require(!handler.Requests.Any(u => u.AbsolutePath == "/"));
            // Exactly one request carries the password, and it is the portal's login endpoint.
            Require(handler.Requests.Count(u => u.Query.Contains("user_password")) == 1);
            Require(handler.Requests.Single(u => u.Query.Contains("user_password")).AbsolutePath == "/eportal/portal/login");

            // The gateway redirect supplies the client address and the AC details.
            Require(handler.Requests.Any(u => u.AbsolutePath == "/connecttest.txt" && u.Scheme == "http"));

            // When neither the gateway nor the status response has it, the home page is tried.
            var fallback = new MonitorHttp { GatewayLocation = null, StatusClientIpv4 = null, HomePage = "<script>v4ip='10.0.0.1';</script>" };
            using var fallbackHttp = new HttpClient(fallback);
            var second = await new DrcomPortal(fallbackHttp, new Uri("https://login.csust.edu.cn")).DiagnoseAsync(default);
            Require(second.Stage == PortalStage.Submission && second.Failure == PortalFailure.MissingGatewayParameters && fallback.Requests.Any(u => u.AbsolutePath == "/"));

            // An unusable address is rejected rather than silently used.
            foreach (var bad in new[] { "not-an-ip", "::1", "" })
            {
                var invalid = new MonitorHttp { GatewayLocation = null, StatusClientIpv4 = bad, HomePage = "<html>no-client-address-here</html>" };
                using var invalidHttp = new HttpClient(invalid);
                var third = await new DrcomPortal(invalidHttp, new Uri("https://login.csust.edu.cn")).DiagnoseAsync(default);
                Require(third.Stage == PortalStage.ClientIpv4 && third.Failure == PortalFailure.MissingClientIpv4);
            }
        }),
        ("认证诊断：不受信目标在任何网络请求前拒绝", async () =>
        {
            foreach (var origin in new[] { "https://untrusted.example", "http://login.csust.edu.cn", "https://login.csust.edu.cn:802", "https://login.csust.edu.cn/?token=x" })
            {
                var handler = new MonitorHttp(); using var http = new HttpClient(handler);
                var outcome = await new DrcomPortal(http, new Uri(origin)).LoginDetailedAsync(new("test", "never-real"), default);
                Require(outcome.Failure == PortalFailure.InvalidTarget && handler.Requests.Count == 0);
            }
        }),
        ("认证诊断：前置重定向和无效响应绝不提交密码", async () =>
        {
            foreach (var path in new[] { "/", "/eportal/portal/page/loadConfig" })
            {
                var handler = new MonitorHttp { StatusClientIpv4 = null, GatewayLocation = null, FailurePath = path, FailureStatus = HttpStatusCode.Redirect };
                using var http = new HttpClient(handler);
                var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), default);
                Require(outcome.Result == LoginResult.Unsupported && !outcome.CredentialRequestStarted);
                // Only the gateway probe leaves the portal host, and it is plain HTTP with no credentials.
                Require(handler.Requests.Where(u => u.AbsolutePath != "/connecttest.txt")
                    .All(u => u.Host == "login.csust.edu.cn" && !u.Query.Contains("user_password")));
            }
            var malformed = new MonitorHttp { ConfigJson = "fixture-secret-response" };
            using var malformedHttp = new HttpClient(malformed);
            var failed = await new DrcomPortal(malformedHttp, new Uri("https://login.csust.edu.cn")).DiagnoseAsync(default);
            Require(failed.Stage == PortalStage.Configuration && failed.Failure == PortalFailure.InvalidResponse && !failed.SafeSummary.Contains("secret"));
        }),
        ("认证诊断：暂时802故障退避后可认证，不永久暂停", async () =>
        {
            var fixture = new MonitorFixture();
            fixture.Http.FailurePath = "/eportal/portal/page/loadConfig"; fixture.Http.FailureStatus = HttpStatusCode.ServiceUnavailable;
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            var failed = await worker.RunOnceAsync(default);
            Require(!failed.CredentialsBlocked && failed.AuthenticationStage == "Configuration" && failed.AuthenticationHttpStatus == 503);
            Require(!failed.CredentialRequestStarted);
            fixture.Http.FailurePath = null; fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            var accepted = await worker.RunOnceAsync(default);
            Require(accepted.CredentialRequestStarted && accepted.AuthenticationAcceptedAt is not null && !accepted.CredentialsBlocked);
        }),
        ("认证诊断：超时与TLS故障保留阶段并采用不同暂停策略", async () =>
        {
            foreach (var tls in new[] { false, true })
            {
                var handler = new MonitorHttp { FailurePath = "/eportal/portal/page/loadConfig",
                    FailureException = tls ? new HttpRequestException(HttpRequestError.SecureConnectionError, "fixture-secret-url") : new TaskCanceledException("fixture-secret-url") };
                using var http = new HttpClient(handler);
                var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), default);
                Require(outcome.Stage == PortalStage.Configuration && outcome.Failure == (tls ? PortalFailure.Tls : PortalFailure.Timeout));
                Require(outcome.Result == (tls ? LoginResult.Unsupported : LoginResult.Rejected) && !outcome.SafeSummary.Contains("secret"));
            }
        }),
        ("认证诊断：协议异常暂停保留具体原因且不重复提交", async () =>
        {
            var fixture = new MonitorFixture(); fixture.Http.ConfigJson = "{\"data\":{\"en_md5\":1,\"password_cut\":0,\"enable_r3\":0}}";
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            var blocked = await worker.RunOnceAsync(default);
            Require(blocked.CredentialsBlocked && blocked.AuthenticationStage == "AuthenticationMode" && blocked.Detail.Contains("UnsupportedFlag"));
            fixture.Clock.Advance(TimeSpan.FromMinutes(1)); await worker.RunOnceAsync(default);
            Require(fixture.Http.Requests.All(u => u.AbsolutePath != "/eportal/portal/login"));
            fixture.Http.Online = true;
            var online = await worker.RunOnceAsync(default);
            Require(online.CredentialsBlocked && online.Direct == "外网可达");
            using var restarted = new MonitorWorker(fixture, fixture.Clock);
            var historical = await restarted.RunOnceAsync(default);
            Require(historical.CredentialsBlocked && historical.Detail.Contains("历史"));
        })
    ];

    private static void Require(bool condition) { if (!condition) throw new Exception("认证诊断行为不符合预期"); }
}

internal sealed class GatewayCancellationHandler(CancellationTokenSource cancellation) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath == "/connecttest.txt")
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
        }
        return base.SendAsync(request, ct);
    }
}
