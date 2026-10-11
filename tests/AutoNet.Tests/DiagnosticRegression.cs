using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text.Json;
using AutoNet.Core;
using AutoNet.Service;

internal static class DiagnosticRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("诊断：持续认证暂停保留故障关联、精确决策及周期摘要", async () =>
        {
            var fixture = new MonitorFixture { Blocked = true };
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            var start = fixture.Logs.Single(e => e.EventCode == "IncidentStarted");
            var decision = fixture.Logs.Last(e => e.EventCode == "RecoveryDecision");
            Require(start.IncidentId is not null && decision.IncidentId == start.IncidentId && decision.Diagnostics?.Reason == "AuthenticationPaused");
            Require(decision.Diagnostics?.AuthenticationState == "SafetyBlocked" && decision.Diagnostics.HasCredentials == true);
            var count = fixture.Logs.Count(e => e.EventCode == "RecoveryDecision");
            fixture.Clock.Advance(TimeSpan.FromSeconds(2)); await worker.RunOnceAsync(default);
            Require(fixture.Logs.Count(e => e.EventCode == "RecoveryDecision") == count);
            fixture.Clock.Advance(TimeSpan.FromSeconds(30)); await worker.RunOnceAsync(default);
            Require(fixture.Logs.Count(e => e.EventCode == "RecoveryDecision") == count + 1 && fixture.Connects == 0);
        }),
        ("诊断：动作起止、认证参数来源与联网验证串在同次故障", async () =>
        {
            var fixture = new MonitorFixture();
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            fixture.Http.Online = true;
            fixture.Clock.Advance(TimeSpan.FromSeconds(2)); await worker.RunOnceAsync(default);
            var start = fixture.Logs.Single(e => e.EventCode == "RecoveryActionStarted");
            var finish = fixture.Logs.Single(e => e.EventCode == "RecoveryActionCompleted");
            var recovered = fixture.Logs.Single(e => e.EventCode == "IncidentRecovered");
            Require(start.Diagnostics?.ActionId is not null && start.Diagnostics.ActionId == finish.Diagnostics?.ActionId);
            Require(start.IncidentId == recovered.IncidentId && recovered.Diagnostics?.Reason == "RecoverySourceUnconfirmed");
            Require(fixture.Logs.Any(e => e.EventCode == "PortalStep" && e.Diagnostics?.Stage == "Submission"));
            Require(finish.Diagnostics?.Authentication?.Evidence?.AddressSource == "GatewayRedirect");
            fixture.Http.Online = false;
            fixture.Clock.Advance(TimeSpan.FromSeconds(2)); await worker.RunOnceAsync(default);
            Require(fixture.Logs.Last(e => e.EventCode == "IncidentStarted").IncidentId != recovered.IncidentId);
        }),
        ("诊断：两个外网目标分别保留DNS失败、原生错误和脱敏证据", async () =>
        {
            var fixture = new MonitorFixture { Blocked = true };
            fixture.Http.FailurePath = "/robots.txt";
            fixture.Http.FailureException = new HttpRequestException(HttpRequestError.NameResolutionError,
                "https://secret.example/?user_password=fixture-secret", new SocketException((int)SocketError.HostNotFound));
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            var probes = fixture.Logs.Last(e => e.EventCode == "RecoveryDecision").Diagnostics?.Probes;
            Require(probes?.Length == 2 && probes.All(p => p.Failure == "NameResolutionError" && p.Error?.SocketError == "HostNotFound"));
            var readable = fixture.Logs.Last(e => e.EventCode == "RecoveryDecision").ToReadableText();
            Require(readable.Contains("原生错误 " + (int)SocketError.HostNotFound) && readable.Contains("链路观测：Ready"));
            var json = JsonSerializer.Serialize(fixture.Logs, ActivityLog.Json);
            Require(!json.Contains("fixture-secret") && !json.Contains("secret.example") && !json.Contains("10.0.0.1"));
        }),
        ("诊断：参数缺失准确区分门户地址与本机IPv4且不提交密码", async () =>
        {
            var fixture = new MonitorFixture();
            fixture.Http.StatusClientIpv4 = null; fixture.Http.GatewayLocation = null; fixture.Http.HomePage = "<html>fixture-secret</html>";
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            var finish = fixture.Logs.Single(e => e.EventCode == "RecoveryActionCompleted");
            var evidence = finish.Diagnostics?.Authentication?.Evidence;
            Require(finish.Diagnostics?.Authentication?.Failure == PortalFailure.MissingClientIpv4 && finish.Diagnostics.HasIpv4 == true);
            Require(evidence?.StatusAddress == "Missing" && evidence.GatewayAddress == "Missing" && evidence.HomeAddress == "Missing");
            Require(!fixture.Http.Requests.Any(u => u.AbsolutePath == "/eportal/portal/login"));
        }),
        ("诊断：状态写入错误有发生和恢复事件且不持续冒充当前错误", async () =>
        {
            var fixture = new MonitorFixture { FailStatus = true }; fixture.Http.Online = true;
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            Require((await worker.RunOnceAsync(default)).LastError.Contains("状态写入"));
            fixture.FailStatus = false;
            var status = await worker.RunOnceAsync(default);
            Require(!status.LastError.Contains("状态写入"));
            Require(fixture.Logs.Any(e => e.EventCode == "OperationFailed" && e.Diagnostics?.Stage == "StatusWrite"));
            Require(fixture.Logs.Any(e => e.EventCode == "OperationRecovered" && e.Diagnostics?.Stage == "StatusWrite"));
        }),
        ("诊断：日志写入失败使用独立告警，恢复后补报明确缺口", async () =>
        {
            var fixture = new MonitorFixture { Blocked = true, FailLog = true };
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            Require(fixture.FallbackLogs.Count == 1);
            fixture.FailLog = false;
            fixture.Clock.Advance(TimeSpan.FromSeconds(30)); await worker.RunOnceAsync(default);
            Require(fixture.Logs.Any(e => e.EventCode == "LoggingGap" && e.Diagnostics?.LostEvents > 0));
            Require(fixture.Logs.Select(e => e.Sequence).Distinct().Count() == fixture.Logs.Count);
        }),
        ("诊断：服务重启保留未结束故障关联并标注观测中断", async () =>
        {
            var fixture = new MonitorFixture { Blocked = true };
            using (var worker = new MonitorWorker(fixture, fixture.Clock)) await worker.RunOnceAsync(default);
            var prior = fixture.Logs.Last(e => e.EventCode == "RecoveryDecision");
            fixture.PreviousDiagnostic = prior;
            using var restarted = new MonitorWorker(fixture, fixture.Clock);
            await restarted.RunOnceAsync(default);
            var resumed = fixture.Logs.Last(e => e.EventCode == "IncidentResumed");
            Require(resumed.IncidentId == prior.IncidentId && resumed.RunId != prior.RunId && resumed.Diagnostics?.PreviousRunId == prior.RunId);
        }),
        ("诊断：检测API异常保留准确阶段原生码调用位置及恢复事件", async () =>
        {
            var fixture = new MonitorFixture { BeforeWifiRead = () => throw new System.Net.NetworkInformation.NetworkInformationException(5) };
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            var failure = fixture.Logs.Single(e => e.EventCode == "OperationFailed");
            Require(failure.Diagnostics?.Stage == "MainCycle/WifiRead" && failure.Diagnostics.Error?.NativeError == 5);
            Require(failure.Diagnostics?.Error?.CallSites.Any(s => s.StartsWith("AutoNet.Service.", StringComparison.Ordinal)) == true);
            fixture.Clock.Advance(TimeSpan.FromSeconds(31)); await worker.RunOnceAsync(default);
            Require(fixture.Logs.Count(e => e.EventCode == "RecoveryDecision") == 2);
            fixture.BeforeWifiRead = () => throw new System.Net.NetworkInformation.NetworkInformationException(1168);
            await worker.RunOnceAsync(default);
            Require(fixture.Logs.Count(e => e.EventCode == "OperationFailed") == 2);
            Require(fixture.Logs.Last(e => e.EventCode == "OperationFailed").Diagnostics?.Error?.NativeError == 1168);
            fixture.BeforeWifiRead = null; fixture.Http.Online = true;
            Require(!(await worker.RunOnceAsync(default)).LastError.Contains("主检测异常"));
            Require(fixture.Logs.Any(e => e.EventCode == "OperationRecovered" && e.Diagnostics?.Stage == "MainCycle/WifiRead"));
        }),
        ("诊断：HTTP错误与内容不匹配不再统一为外网不可达", async () =>
        {
            var fixture = new MonitorFixture { Blocked = true };
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            Require(fixture.Logs.Last(e => e.EventCode == "RecoveryDecision").Diagnostics?.Probes?.All(p => p.Failure == "ContentMismatch" && p.HttpStatus == 200) == true);
            fixture.Http.FailurePath = "/robots.txt"; fixture.Http.FailureStatus = HttpStatusCode.ServiceUnavailable;
            await worker.RunOnceAsync(default);
            Require(fixture.Logs.Last(e => e.EventCode == "RecoveryDecision").Diagnostics?.Probes?.All(p => p.Failure == "HttpStatus" && p.HttpStatus == 503) == true);
        }),
        ("诊断：提交后连接丢失保留请求阶段及Pending而非误报密码错误", async () =>
        {
            var fixture = new MonitorFixture();
            fixture.Http.FailurePath = "/eportal/portal/login";
            fixture.Http.FailureException = new HttpRequestException(HttpRequestError.ConnectionError, "fixture-secret",
                new SocketException((int)SocketError.ConnectionReset));
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            var result = fixture.Logs.Single(e => e.EventCode == "RecoveryActionCompleted").Diagnostics;
            Require(result?.AuthenticationState == "SubmissionPending" && result.Authentication?.CredentialRequestStarted == true);
            Require(result.Authentication.Failure == PortalFailure.Transport && result.Authentication.Error?.SocketError == "ConnectionReset");
            Require(fixture.Logs.Any(e => e.EventCode == "PortalStep" && e.Diagnostics?.Stage == "Submission" && e.Diagnostics.Outcome == "Failed"));
        }),
        ("诊断：取消中的动作保留未完成证据且不伪造结束", async () =>
        {
            var fixture = new MonitorFixture();
            using var canceled = new CancellationTokenSource();
            fixture.Http.BeforeLogin = canceled.Cancel;
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            try { await worker.RunOnceAsync(canceled.Token); throw new Exception("应传播取消"); }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
            var start = fixture.Logs.Single(e => e.EventCode == "RecoveryActionStarted");
            Require(fixture.Logs.Single(e => e.EventCode == "CycleInterrupted").Diagnostics?.ActionId == start.Diagnostics?.ActionId);
            Require(!fixture.Logs.Any(e => e.EventCode == "RecoveryActionCompleted"));
        }),
        ("诊断：缺失前置条件与主动断线开关各自说明不动作原因", async () =>
        {
            foreach (var (reason, fixture) in new[]
            {
                ("RecoveryDisabled", new MonitorFixture { Settings = new() { InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "fixture", Enabled = false } }),
                ("CredentialsMissing", new MonitorFixture { HasCredentials = false }),
                ("ActiveDisconnectDisabled", new MonitorFixture { Settings = new() { InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "fixture", AllowActiveDisconnect = false } }),
                ("WaitingForAddress", new MonitorFixture { Link = new(true, null, null, 7, []) })
            })
            {
                fixture.Http.PortalResult = "-1";
                using var worker = new MonitorWorker(fixture, fixture.Clock);
                await worker.RunOnceAsync(default);
                Require(fixture.Logs.Last(e => e.EventCode == "RecoveryDecision").Diagnostics?.Reason == reason);
                Require(fixture.Connects == 0 && fixture.Disconnects == 0);
            }
        }),
        ("诊断：配置解析失败保留HTTP成功与响应类型长度而非原文", async () =>
        {
            var fixture = new MonitorFixture(); fixture.Http.ConfigJson = "<html>fixture-secret</html>";
            using var worker = new MonitorWorker(fixture, fixture.Clock);
            await worker.RunOnceAsync(default);
            var attempt = fixture.Logs.Single(e => e.EventCode == "RecoveryActionCompleted").Diagnostics?.Authentication;
            Require(attempt?.Stage == PortalStage.Configuration && attempt.HttpStatus == 200 && attempt.Failure == PortalFailure.InvalidResponse);
            Require(attempt.Evidence?.ResponseCharacters == fixture.Http.ConfigJson.Length && attempt.Evidence.ResponseContentType == "Text");
            Require(!JsonSerializer.Serialize(fixture.Logs, ActivityLog.Json).Contains("fixture-secret"));
        }),
        ("诊断：分享摘要保留关联和错误依据但拒绝结构化自由文本注入", () =>
        {
            var run = Guid.NewGuid().ToString("N"); var incident = Guid.NewGuid().ToString("N");
            var entry = new ActivityLog
            {
                EventCode = "RecoveryDecision", RunId = run, IncidentId = incident, Sequence = 12,
                Diagnostics = new()
                {
                    Reason = "AuthenticationPaused", AuthenticationState = "SafetyBlocked", HasIpv4 = true,
                    Probes = [new("BaiduRobots", false, "NameResolutionError", 1, Error: new("HttpRequestException", -1, "NameResolutionError", "HostNotFound", 11001, ["fixture-secret"]))],
                    Authentication = new(LoginResult.Rejected, PortalStage.ClientIpv4, PortalFailure.MissingClientIpv4)
                        { Evidence = new() { StatusAddress = "Missing", GatewayAddress = "fixture-secret", AddressSource = "fixture-secret" } }
                }
            };
            var summary = LogSummary.Format([entry]);
            Require(summary.Contains(run) && summary.Contains(incident) && summary.Contains("AuthenticationPaused") && summary.Contains("HostNotFound"));
            Require(summary.Contains("MissingClientIpv4") && !summary.Contains("fixture-secret"));
            var hostile = entry with { RunId = "fixture-secret", IncidentId = "fixture-secret", Diagnostics = entry.Diagnostics with { Reason = "fixture-secret", Stage = "fixture-secret" } };
            Require(!LogSummary.Format([hostile]).Contains("fixture-secret"));
            return Task.CompletedTask;
        }),
        ("诊断：暂停记录区分旧指纹损坏结构与版本异常且保持零提交", () =>
        {
            AcceptanceRegression.WithStore((store, directory) =>
            {
                foreach (var (content, kind) in new[]
                {
                    (new string('A', 64), AuthenticationRecordKind.LegacyFingerprintShape),
                    ("{broken", AuthenticationRecordKind.InvalidJson),
                    ("{}", AuthenticationRecordKind.LegacyRecord),
                    ("{\"Version\":1}", AuthenticationRecordKind.MissingFields),
                    (JsonSerializer.Serialize(new AuthenticationBlock() { Version = 99 }, Storage.Json), AuthenticationRecordKind.UnsupportedVersion)
                })
                {
                    File.WriteAllText(Path.Combine(directory, "auth-block.txt"), content);
                    var fixture = new MonitorFixture();
                    using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                    Require(worker.RunOnceAsync(default).GetAwaiter().GetResult().CredentialsBlocked);
                    Require(fixture.Logs.Last(e => e.EventCode == "RecoveryDecision").Diagnostics?.AuthenticationRecordSource == kind.ToString());
                    Require(!fixture.Http.Requests.Any(u => u.AbsolutePath == "/eportal/portal/login"));
                }
            });
            return Task.CompletedTask;
        })
    ];

    private static void Require([DoesNotReturnIf(false)] bool value) { if (!value) throw new Exception("故障诊断链不完整或证据不准确"); }
}
