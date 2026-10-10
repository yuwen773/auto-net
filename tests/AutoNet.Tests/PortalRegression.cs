using System.Net;
using AutoNet.Core;
using AutoNet.Service;

internal static class PortalRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("认证诊断：暂停标记写入恢复后补写且期间不再认证", async () =>
        {
            var fixture = new MonitorFixture { FailBlock = true };
            fixture.Http.ConfigJson = "{\"data\":{\"en_md5\":1,\"password_cut\":0,\"enable_r3\":0}}";
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            Require((await worker.RunOnceAsync(default)).CredentialsBlocked && !fixture.Blocked);
            fixture.FailBlock = false; fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            await worker.RunOnceAsync(default);
            Require(fixture.Blocked && fixture.Http.Requests.All(u => u.AbsolutePath != "/drcom/login"));
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
            Require(!outcome.CredentialRequestStarted && handler.Requests.All(u => u.AbsolutePath != "/drcom/login" && !u.Query.Contains("upass")));
        }),
        ("认证诊断：IPv4、802、结构、模式和提交分别定位", async () =>
        {
            foreach (var scenario in new[] { "ip", "802", "structure", "mode", "submission" })
            {
                var handler = new MonitorHttp();
                var expectedStage = PortalStage.ClientIpv4;
                var expectedFailure = PortalFailure.MissingClientIpv4;
                switch (scenario)
                {
                    case "ip": handler.HomePage = "<html>fixture-secret-response</html>"; break;
                    case "802": handler.FailurePath = "/eportal/portal/page/loadConfig"; handler.FailureStatus = HttpStatusCode.BadGateway; expectedStage = PortalStage.Configuration; expectedFailure = PortalFailure.HttpStatus; break;
                    case "structure": handler.ConfigJson = "{\"data\":null}"; expectedStage = PortalStage.Configuration; expectedFailure = PortalFailure.MalformedConfiguration; break;
                    case "mode": handler.ConfigJson = "{\"data\":{\"en_md5\":1,\"password_cut\":0,\"enable_r3\":0}}"; expectedStage = PortalStage.AuthenticationMode; expectedFailure = PortalFailure.UnsupportedFlag; break;
                    case "submission": handler.LoginResult = "{\"result\":0,\"msg\":\"userid error1\"}"; expectedStage = PortalStage.Submission; expectedFailure = PortalFailure.CredentialsRejected; break;
                }
                using var http = new HttpClient(handler);
                var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), default);
                Require(outcome.Stage == expectedStage && outcome.Failure == expectedFailure);
                Require(outcome.CredentialRequestStarted == (scenario == "submission"));
                Require(!outcome.SafeSummary.Contains("secret") && !outcome.SafeSummary.Contains("never-real"));
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
                var handler = new MonitorHttp { FailurePath = path, FailureStatus = HttpStatusCode.Redirect };
                using var http = new HttpClient(handler);
                var outcome = await new DrcomPortal(http, new Uri("https://login.csust.edu.cn")).LoginDetailedAsync(new("test", "never-real"), default);
                Require(outcome.Result == LoginResult.Unsupported && !outcome.CredentialRequestStarted);
                Require(handler.Requests.All(u => u.Host == "login.csust.edu.cn" && !u.Query.Contains("upass")));
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
            Require(fixture.Http.Requests.All(u => u.AbsolutePath != "/drcom/login"));
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
