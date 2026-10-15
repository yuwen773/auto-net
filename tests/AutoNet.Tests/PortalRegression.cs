using System.Net;
using System.Text.Json;
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
        ("阶段预算：预检、复核、提交默认值符合 ADR-0004 的 4/5/3 秒", () =>
        {
            // Policy anchor: the ADR's numbers live here; a change must update the record.
            var budgets = PortalBudgets.Default;
            Require(budgets.Preflight == TimeSpan.FromSeconds(4));
            Require(budgets.Review == TimeSpan.FromSeconds(5));
            Require(budgets.Submission == TimeSpan.FromSeconds(3));
            return Task.CompletedTask;
        }),
        ("阶段预算：预检挂死归类在预检阶段且不发送凭据", async () =>
        {
            // A status endpoint that never answers is aborted by the preflight budget alone;
            // the timeout must be classified at the preflight stage, not disguised as a
            // submission timeout, and no credential request may leave the machine.
            var budgets = new PortalBudgets(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150));
            using var http = new HttpClient(new HangingHandler("/drcom/chkstatus"));
            var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn"), null, budgets)
                .LoginDetailedAsync(new("test", "never-real"), default);
            Require(outcome.Stage == PortalStage.Status && outcome.Failure == PortalFailure.Timeout);
            Require(outcome.Result == LoginResult.Rejected && !outcome.CredentialRequestStarted);
        }),
        ("阶段预算：复核链收到可取消令牌，链超时按安全拒发", async () =>
        {
            var budgets = new PortalBudgets(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(200));
            var handler = new MonitorHttp();
            using var http = new HttpClient(handler);
            var portal = new DrcomPortal(http, new Uri("https://login.csust.edu.cn"), null, budgets);
            var cancelled = false;
            var outcome = await portal.LoginDetailedAsync(new("test", "never-real"), default, review =>
            {
                // The chain learns its budget expired only between steps, like the real
                // file/WLAN reads; aborting there must refuse the submission safely.
                while (!review.IsCancellationRequested) Thread.Sleep(5);
                cancelled = true;
                review.ThrowIfCancellationRequested();
                return true;
            });
            Require(cancelled);
            Require(outcome.Result == LoginResult.Unsupported && outcome.Failure == PortalFailure.SafetyPersistence);
            Require(outcome.Stage == PortalStage.Submission && !outcome.CredentialRequestStarted);
            Require(handler.Requests.All(u => !u.Query.Contains("user_password")));
        }),
        ("阶段预算：提交窗口独立于预检耗时，登录超时仍归类提交阶段", async () =>
        {
            var budgets = new PortalBudgets(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(150));
            var handler = new MonitorHttp
            {
                Delay = uri => uri.AbsolutePath == "/eportal/portal/login" ? TimeSpan.FromMilliseconds(400) : TimeSpan.FromMilliseconds(60)
            };
            using var http = new HttpClient(handler);
            var portal = new DrcomPortal(http, new Uri("https://login.csust.edu.cn"), null, budgets);
            // The preflight stays inside its own budget, yet the credential request still
            // times out: the submission window is a fresh budget, not "whatever is left".
            var slow = await portal.LoginDetailedAsync(new("test", "never-real"), default);
            Require(slow.Stage == PortalStage.Submission && slow.Failure == PortalFailure.Timeout);
            Require(slow.Result == LoginResult.Rejected && slow.CredentialRequestStarted);
            handler.Delay = null;
            var fast = await portal.LoginDetailedAsync(new("test", "never-real"), default);
            Require(fast.Result == LoginResult.Accepted && fast.CredentialRequestStarted && fast.Failure == PortalFailure.None);
            Require(handler.Requests.Count(u => u.Query.Contains("user_password")) == 2);
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
        }),
        ("探测语义：门户重定向记劫持嫌疑，非门户重定向仍是 HTTP 状态", async () =>
        {
            // ADR-0006: production probes never follow redirects, so a 3xx reaches the probe
            // itself. A Location carrying portal keywords is a captive-portal hijack — a
            // different fact from a body that failed the content check. The vocabulary is
            // the ADR's (drcom/portal/webauth/srun), matched anywhere in the target URL;
            // the campus host itself carries none of them by design.
            foreach (var (location, expected) in new (string?, string)[]
            {
                ("https://portal.example/", "HijackSuspected"),
                ("https://webauth.example/portal/page", "HijackSuspected"),
                ("http://www.drcom.com.cn/", "HijackSuspected"),
                ("https://srun.example/", "HijackSuspected"),
                ("https://www.example.com/?next=portal", "HijackSuspected"),
                ("https://www.example.com/", "HttpStatus"),
                (null, "HttpStatus")
            })
            {
                using var http = new HttpClient(new RedirectHandler(location));
                var evidence = await HealthChecks.InternetDetailedAsync(http, default);
                Require(!evidence.Online);
                Require(evidence.Probes.All(p => p.Failure == expected && p.HttpStatus == 302 && !p.Passed));
            }
        }),
        ("探测语义：生产探测客户端禁用自动重定向", () =>
        {
            // The hijack classification only works if the handler answers the 3xx to the
            // probe; both production probe clients are pinned here so a future handler
            // change cannot silently start following portal redirects.
            Require(!HealthChecks.SystemHandler("").AllowAutoRedirect);
            Require(!DirectNetwork.CreateHandler(new(true, null, IPAddress.Parse("10.0.0.1"), 7, [])).AllowAutoRedirect);
            return Task.CompletedTask;
        }),
        ("探测语义：探测证据带置信度标注，当前均为 Confirmed", async () =>
        {
            using var http = new HttpClient(new StaticHandler(HttpStatusCode.OK, "User-agent: *"));
            var evidence = await HealthChecks.InternetDetailedAsync(http, default);
            Require(evidence.Online && evidence.Probes.All(p => p.Confidence == "Confirmed"));
            // The annotation rides the structured diagnostic record, not just the return value.
            var serialized = JsonSerializer.Serialize(evidence.Probes[0], Storage.Json);
            Require(serialized.Contains("Confirmed"));
        }),
        ("认证诊断：chkstatus 缺 result 字段记结构变化，异常取值仍是状态未知", async () =>
        {
            // ADR-0005 distinguishes a changed response shape (InvalidResponse, a layout
            // signal that pauses like the other structural failures) from an unrecognised
            // state value (StatusUnknown, not a layout signal).
            foreach (var (body, expected) in new (string, PortalFailure)[]
            {
                ("dr1001({\"code\":200,\"v4ip\":\"10.0.0.1\"})", PortalFailure.InvalidResponse),
                ("{\"result\":-1,\"v4ip\":\"10.0.0.1\"}", PortalFailure.StatusUnknown)
            })
            {
                var handler = new MonitorHttp { StatusJson = body };
                using var http = new HttpClient(handler);
                var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), default);
                Require(outcome.Stage == PortalStage.Status && outcome.Failure == expected && !outcome.CredentialRequestStarted);
                Require(outcome.Result == (expected == PortalFailure.InvalidResponse ? LoginResult.Unsupported : LoginResult.Rejected));
            }
        }),
        ("认证诊断：状态接口非200不升级为门户改版信号", async () =>
        {
            // A transient 5xx/429 on chkstatus keeps the pre-existing status-unknown
            // classification; only a 200 with a changed body shape is a layout signal.
            var handler = new MonitorHttp { StatusJson = null, FailurePath = "/drcom/chkstatus", FailureStatus = HttpStatusCode.ServiceUnavailable };
            using var http = new HttpClient(handler);
            var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), default);
            Require(outcome.Stage == PortalStage.Status && outcome.Failure == PortalFailure.StatusUnknown && outcome.HttpStatus == 503);
            Require(!outcome.CredentialRequestStarted && !PortalLayoutSignal.IsStructural(outcome.Failure));
            var inspect = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).InspectDetailedAsync(default);
            Require(inspect.Failure == PortalFailure.HttpStatus && inspect.HttpStatus == 503);
        }),
        ("认证诊断：状态无法识别时只读检查不报告完成", async () =>
        {
            // The read-only path must not hide a changed chkstatus shape behind "complete";
            // an already-logged-in portal still completes, because there is nothing to submit.
            var handler = new MonitorHttp { StatusJson = "dr1001({\"code\":200})" };
            using var http = new HttpClient(handler);
            var changed = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).DiagnoseAsync(default);
            Require(changed.Failure == PortalFailure.InvalidResponse && !changed.CredentialRequestStarted);
            handler.StatusJson = null;
            handler.PortalResult = "1";
            var online = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).DiagnoseAsync(default);
            Require(online.Failure == PortalFailure.None && online.Stage == PortalStage.Complete);
        }),
        ("门户改版：结构类集合只含门户形状失败，与普通失败互斥", () =>
        {
            foreach (var failure in new[]
            {
                PortalFailure.MissingFlag, PortalFailure.InvalidFlagType, PortalFailure.UnsupportedFlag,
                PortalFailure.DuplicateFlag, PortalFailure.MalformedConfiguration, PortalFailure.InvalidResponse,
                PortalFailure.MissingGatewayParameters
            })
                Require(PortalLayoutSignal.IsStructural(failure));
            foreach (var failure in new[]
            {
                PortalFailure.None, PortalFailure.InvalidTarget, PortalFailure.AlreadyAuthenticated, PortalFailure.StatusUnknown,
                PortalFailure.HttpStatus, PortalFailure.MissingClientIpv4, PortalFailure.Timeout, PortalFailure.Transport,
                PortalFailure.Tls, PortalFailure.CredentialsRejected, PortalFailure.InteractionRequired, PortalFailure.LoginRejected,
                PortalFailure.BindingRejected, PortalFailure.SafetyPersistence
            })
                Require(!PortalLayoutSignal.IsStructural(failure));
            return Task.CompletedTask;
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

// Answers every probe with a redirect, optionally carrying a Location; models a captive
// portal or gateway intercepting the probe targets (ADR-0006).
internal sealed class RedirectHandler(string? location) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new StringContent("") };
        if (location is not null) response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return Task.FromResult(response);
    }
}

// Models a portal stage that never answers: the request is aborted only when the
// caller's token fires, exactly like a real black-holed connection.
internal sealed class HangingHandler(string path) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath == path) await Task.Delay(Timeout.Infinite, ct);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
