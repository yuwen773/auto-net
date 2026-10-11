using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AutoNet.Core;
using Microsoft.Extensions.Hosting;

namespace AutoNet.Service;

public sealed class MonitorWorker(IMonitorEnvironment environment, TimeProvider clock) : BackgroundService
{
    private readonly RecoveryPolicy policy = new();
    private readonly RecoveryJournal journal = new(environment, clock);
    private readonly DateTimeOffset clockOrigin = clock.GetUtcNow();
    private readonly long clockStart = clock.GetTimestamp();
    private string budgetKey = "";
    private DisconnectBudget persistedBudget = new();
    private PortalFailure blockFailure;
    private string lastEvent = "";
    private long? lastAuxiliary;
    private long auxiliaryStarted;
    private Task<(string SystemPath, string Uu)>? auxiliaryTask;
    private CancellationTokenSource? auxiliaryCancellation;
    private string auxiliaryKey = "";
    private bool discardAuxiliary;
    private string systemPath = "待检测";
    private string uu = "待检测";
    private double? lastRecoverySeconds;
    private DateTimeOffset? lastRecoveryAt;
    private DateTimeOffset? outageObservedAt;
    private DateTimeOffset? actionStartedAt;
    private DateTimeOffset? actionFinishedAt;
    private DateTimeOffset? authenticationAcceptedAt;
    private PortalAttempt? lastAuthentication;
    private bool authenticationBlockPending;
    private AuthenticationBlock? authentication;
    private AuthenticationBlock? authenticationExpected;
    private AuthenticationBlock? authenticationToWrite;
    private DateTimeOffset authenticationRetryUntil;
    private string lastError => journal.LastError;
    private HttpClient? directHttp;
    private string directKey = "";

    private DateTimeOffset PolicyNow => clockOrigin + clock.GetElapsedTime(clockStart);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var started = clock.GetTimestamp();
                await RunOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(200, 2000 - clock.GetElapsedTime(started).TotalMilliseconds)), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception e) { journal.Fault("ServiceLoop", "监测主循环退出", e); Environment.Exit(1); }
        finally
        {
            journal.Write("SessionStopped", "监测进程正在停止；未结束的故障和动作不代表成功。", new() { Reason = "ServiceStopping" });
            StopAuxiliary(); directHttp?.Dispose(); UpdatePower(false);
        }
    }

    // A complete cycle, with only external Windows/HTTP/file boundaries replaced in offline fixtures.
    public async Task<StatusSnapshot> RunOnceAsync(CancellationToken ct)
    {
        journal.BeginCycle();
        try { return await TickAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            journal.Write("CycleInterrupted", "检测轮次被取消；进行中的动作结果未确认。", new() { Stage = journal.Stage, ActionId = journal.ActionId, Reason = "Cancellation" });
            throw;
        }
        catch (Exception e)
        {
            policy.InterruptObservation();
            UpdatePower(false);
            journal.Fault("MainCycle/" + journal.Stage, "主检测异常", e);
            journal.Decision(new() { Stage = journal.Stage, Outcome = "None", Reason = "ObservationFailed", Error = DiagnosticError.From(e) }, new() { State = "检测异常" });
            return Publish(new StatusSnapshot { State = "检测异常", Detail = "检测将自动重试；" + lastError });
        }
    }

    private async Task<StatusSnapshot> TickAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = ReadStep("SettingsRead", environment.LoadSettings);
        if (settings is null)
        {
            policy.Reset();
            UpdatePower(false);
            CancelAuxiliary();
            journal.Suspend("SettingsMissing");
            return Publish(new StatusSnapshot { State = "等待设置", Detail = "请在托盘窗口中设置校园 Wi-Fi 和认证信息。" });
        }
        ReadStep("SettingsValidation", () => { settings.Validate(); return true; });
        var newBudgetKey = Guid.Parse(settings.InterfaceId).ToString("N") + "/" + settings.WifiProfile;
        journal.SetNetwork(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(newBudgetKey)))[..16]);
        if (newBudgetKey != budgetKey)
        {
            var budget = ReadStep("DisconnectBudgetRead", () => environment.LoadDisconnectBudget(settings));
            policy.Reset();
            policy.RestoreDisconnectBudget(budget, PolicyNow);
            persistedBudget = budget;
            budgetKey = newBudgetKey;
        }
        if (authenticationBlockPending && policy.CredentialsBlocked) PersistAuthenticationBlock();
        var loadedAuthentication = ReadStep("AuthenticationStateRead", environment.LoadAuthenticationBlock);
        if (loadedAuthentication != authentication)
        {
            authentication = loadedAuthentication;
            // Wall time cannot prove how much of a retry elapsed while the service was down.
            authenticationRetryUntil = loadedAuthentication?.State == AuthenticationState.RetryCoolingDown
                ? PolicyNow.AddSeconds(loadedAuthentication.RetryDelaySeconds) : DateTimeOffset.MinValue;
        }
        var block = authenticationBlockPending ? authenticationToWrite : authentication;
        if (policy.CredentialsBlocked && (block is null || !block.BlocksSubmissions))
        {
            journal.Recovered("Authentication");
            lastAuthentication = null;
        }
        policy.CredentialsBlocked = block?.BlocksSubmissions == true;
        blockFailure = block?.Failure ?? PortalFailure.None;
        if (block?.State == AuthenticationState.RetryCoolingDown) policy.DeferAuthenticationUntil(authenticationRetryUntil);
        var credentials = ReadStep("CredentialsRead", environment.LoadCredentials);
        UpdatePower(settings.Enabled && settings.KeepAwakeOnAc);
        var link = ReadStep("WifiRead", () => environment.ReadWifi(settings));
        if (link.OtherProfile)
        {
            policy.Reset();
            outageObservedAt = null;
            CancelAuxiliary();
            journal.Suspend("OtherWifiSelected");
            return Publish(new StatusSnapshot { Enabled = settings.Enabled, State = "已切换其他 Wi-Fi", Wifi = "其他配置", Detail = "保留你当前选择的网络，等待校园 Wi-Fi。" });
        }
        var direct = false;
        var portalState = PortalState.Unknown;
        PortalAttempt? inspection = null;
        InternetEvidence? internet = null;
        if (link.State == WifiState.Ready)
        {
            journal.Stage = "InternetProbe";
            var http = DirectClient(link);
            internet = await HealthChecks.InternetDetailedAsync(http, ct);
            direct = internet.Online;
            journal.Recovered("MainCycle/InternetProbe");
            if (!direct)
            {
                journal.Stage = "PortalInspection";
                inspection = await new DrcomPortal(http, new Uri(settings.PortalOrigin)).InspectDetailedAsync(ct);
                portalState = inspection.ObservedState;
            }
        }
        else { directHttp?.Dispose(); directHttp = null; directKey = ""; }
        var observation = new Observation(link.State, direct, portalState);
        var diagnostic = new DiagnosticData
        {
            WifiState = link.State.ToString(), HasIpv4 = link.Address is not null, DnsServerCount = link.DnsServers.Length,
            Condition = observation.Condition.ToString(), Enabled = settings.Enabled, HasCredentials = credentials is not null,
            AllowActiveDisconnect = settings.AllowActiveDisconnect, Probes = internet?.Probes, Authentication = inspection,
            AuthenticationState = block?.State.ToString() ?? "Ready", AuthenticationFailure = blockFailure.ToString(),
            AuthenticationRecordSource = environment.AuthenticationRecordSource.ToString(),
            AuthenticationAttempts = block?.Attempts ?? 0, DisconnectAttempts = policy.DisconnectAttempts,
            DisconnectState = policy.DisconnectBudget.State.ToString(), AuthenticationAttemptMax = RecoveryPolicy.AuthenticationAttemptCap,
            DisconnectAttemptMax = RecoveryPolicy.DisconnectAttemptCap, RequiredFailureRounds = RecoveryPolicy.StalledRoundThreshold,
            RequiredObservationSeconds = RecoveryPolicy.StalledObservationSeconds
        };
        journal.Observe(direct, diagnostic);
        if (direct && authentication is { State: AuthenticationState.Ready, Attempts: > 0 })
            WriteAuthentication(new() { State = AuthenticationState.Ready });
        await PollAuxiliaryAsync(settings, link, ct);
        if (!direct && policy.OutageSince is null)
        {
            actionStartedAt = actionFinishedAt = authenticationAcceptedAt = null;
            lastAuthentication = null;
        }
        if (direct && policy.OutageSince is { } since)
        {
            lastRecoverySeconds = Math.Round((PolicyNow - since).TotalSeconds, 2);
            lastRecoveryAt = clock.GetUtcNow();
            Log("观测到联网恢复", $"HTTPS 外网检查已通过；本轮自动动作 {policy.Attempts} 次。恢复来源无法确认，可能包含人工处理或网络自行恢复。", lastRecoverySeconds,
                new StatusSnapshot { Wifi = "已连接", Direct = "外网可达", Attempts = policy.Attempts, OutageSince = outageObservedAt,
                    ActionStartedAt = actionStartedAt, ActionFinishedAt = actionFinishedAt, AuthenticationAcceptedAt = authenticationAcceptedAt,
                    LastRecoveryAt = lastRecoveryAt, Tunnel = systemPath, Uu = uu });
        }
        var oldBudget = policy.DisconnectBudget;
        var action = policy.Decide(observation, PolicyNow, settings.Enabled, credentials is not null, settings.AllowActiveDisconnect);
        diagnostic = diagnostic with
        {
            Outcome = action.ToString(), Reason = policy.DecisionReason, FailedRounds = policy.FailedRounds,
            ObservationSeconds = policy.ObservationSeconds(PolicyNow),
            NextAttempt = policy.NextAttempt > PolicyNow ? clock.GetUtcNow() + (policy.NextAttempt - PolicyNow) : null
        };
        journal.Decision(diagnostic, new() { Wifi = link.State.ToString(), Direct = direct ? "外网可达" : "外网检查未通过", Portal = portalState.ToString() });
        if (policy.DisconnectBudget != oldBudget)
        {
            try { SaveBudget(settings, policy.DisconnectBudget); }
            catch { policy.RestoreDisconnectBudget(oldBudget, PolicyNow); throw; }
        }
        if (policy.OutageSince is null) outageObservedAt = null;
        else outageObservedAt ??= clock.GetUtcNow();
        var detail = direct ? "指定 Wi-Fi 绑定的 HTTPS 内容检查通过；物理出口需抓包核验。" : observation.Condition switch
        {
            NetworkCondition.AuthenticatedOffline => $"校园网显示已认证，但两个外网检查均未通过；连续异常至少 {RecoveryPolicy.StalledObservationSeconds} 秒且至少 {RecoveryPolicy.StalledRoundThreshold} 轮失败后允许重连校园 Wi-Fi。",
            NetworkCondition.NeedsAuthentication => credentials is null ? "需要认证，请在设置中保存账号密码。" : "校园网认证已失效。",
            NetworkCondition.WaitingForAddress => "Wi-Fi 已关联，等待 IPv4/DHCP；不反复重连。",
            NetworkCondition.Associating => "Wi-Fi 正在关联或链路认证，等待完成。",
            NetworkCondition.InterfaceUnavailable => "选定 Wi-Fi 网卡尚未就绪，等待恢复。",
            NetworkCondition.WifiDisconnected => "等待校园 Wi-Fi 连接。",
            _ => $"两个外网检查均未通过，认证状态未知；连续异常至少 {RecoveryPolicy.StalledObservationSeconds} 秒且至少 {RecoveryPolicy.StalledRoundThreshold} 轮失败后允许重连，确认需认证后再登录。"
        };
        if (inspection is { Failure: not PortalFailure.None }) detail += " " + inspection.SafeSummary;
        if (!direct && link.State == WifiState.Ready && credentials is null) detail = "外网检查未通过；未保存认证信息，请在设置中保存账号密码。";
        if (!settings.Enabled) detail = "自动恢复已暂停；仅检测网络，不重连或认证。";
        else if (!settings.AllowActiveDisconnect && !direct) detail += " 主动断线已独立关闭；仍检测网络并允许连接及安全认证。";
        if (policy.DisconnectBudget.State == DisconnectRequestState.RequestPending)
            detail += " 上次断线请求的完成状态未能持久确认，禁止继续主动断线；需人工核查安全状态。";
        if (!direct && settings.Enabled && policy.DisconnectAttempts >= RecoveryPolicy.DisconnectAttemptCap)
            detail += $" 本次故障的 {RecoveryPolicy.DisconnectAttemptCap} 次主动断线额度已用尽，停止主动断线；连续在线 {RecoveryPolicy.OnlineResetSeconds} 秒后恢复额度。";
        string? actionEvent = null;
        WifiRequestStatus? disconnectResult = null;
        WifiRequestResult? wifiRequest = null;
        if (action != ActionKind.None)
        {
            ct.ThrowIfCancellationRequested();
            journal.ActionId = Guid.NewGuid().ToString("N");
            journal.Stage = action.ToString();
            var actionTick = clock.GetTimestamp();
            journal.Write("RecoveryActionStarted", "恢复动作开始；动作完成与联网恢复分别记录。", diagnostic with { ActionId = journal.ActionId, Stage = action.ToString() });
            if (action == ActionKind.DisconnectWifi)
            {
                policy.Attempted(PolicyNow);
                // Persist the reservation before touching WLAN; a crash or write failure cannot bypass the cap.
                try { SaveBudget(settings, policy.DisconnectBudget); }
                catch { policy.CancelUnissuedDisconnectReservation(); throw; }
            }
            actionStartedAt = clock.GetUtcNow();
            var plannedAction = action switch { ActionKind.DisconnectWifi => "准备断开并重连校园 Wi-Fi", ActionKind.ReconnectWifi => "准备连接校园 Wi-Fi", _ => "准备重新认证" };
            Log("开始恢复动作", plannedAction + "；原因：" + detail, snapshot: new StatusSnapshot { Wifi = link.Connected ? "已连接" : "未连接", Direct = "外网检查未通过",
                Portal = portalState switch { PortalState.LoggedIn => "已认证", PortalState.NeedsLogin => "需认证", _ => "未知" },
                Attempts = policy.Attempts + (action == ActionKind.DisconnectWifi ? 0 : 1), DisconnectAttempts = policy.DisconnectAttempts,
                OutageSince = outageObservedAt, ActionStartedAt = actionStartedAt });
            if (action == ActionKind.ReconnectWifi)
            {
                try
                {
                    var current = environment.LoadSettings();
                    var result = current != settings || current?.Enabled != true ? new WifiRequestResult(WifiRequestStatus.Skipped) : environment.ConnectWifi(settings);
                    wifiRequest = result;
                    if (result.Status == WifiRequestStatus.Issued) journal.Recovered("WifiRequest");
                    if (result.Status == WifiRequestStatus.Failed) journal.Fault("WifiRequest", "Windows 拒绝 Wi-Fi 连接请求", new System.ComponentModel.Win32Exception(result.NativeError ?? 0));
                    journal.Write("WifiRequestResult", "连接步骤结束；Issued 为原生请求成功返回，Skipped 为未发出，Failed 为失败。", new()
                    {
                        Stage = "ReconnectWifi", Outcome = result.Status.ToString(), NativeError = result.NativeError, ActionId = journal.ActionId,
                        Reason = result.Status == WifiRequestStatus.Skipped ? current != settings ? "SettingsChanged" : "FinalWifiChanged" : null
                    });
                    detail = result.Status switch
                    {
                        WifiRequestStatus.Issued => "已请求连接保存的校园 Wi-Fi。",
                        WifiRequestStatus.Skipped => "连接请求已取消：设置或当前 Wi-Fi 状态已变化，保留用户选择。",
                        _ => $"Wi-Fi 连接请求失败（Windows 错误 {result.NativeError}），将退避重试。"
                    };
                }
                catch (System.ComponentModel.Win32Exception e) { wifiRequest = new(WifiRequestStatus.Failed, e.NativeErrorCode); journal.Fault("WifiRequest", "Wi-Fi 连接请求失败", e); detail = "Wi-Fi 连接请求失败，将退避重试。"; }
            }
            else if (action == ActionKind.DisconnectWifi)
            {
                try
                {
                    var check = await RecheckDisconnectAsync(settings, ct);
                    link = check.Link;
                    if (check.Online)
                    {
                        direct = true;
                        observation = new(link.State, true, PortalState.Unknown);
                        policy.Decide(observation, PolicyNow, settings.Enabled, credentials is not null, settings.AllowActiveDisconnect);
                    }
                    ct.ThrowIfCancellationRequested();
                    var result = check.Allowed ? environment.DisconnectWifi(settings) : new WifiRequestResult(WifiRequestStatus.Skipped);
                    wifiRequest = result;
                    if (result.Status == WifiRequestStatus.Issued) journal.Recovered("WifiRequest");
                    if (result.Status == WifiRequestStatus.Failed) journal.Fault("WifiRequest", "Windows 拒绝 Wi-Fi 断开请求", new System.ComponentModel.Win32Exception(result.NativeError ?? 0));
                    journal.Write("WifiRequestResult", "断开步骤结束；Skipped 为未发出，Issued 不代表物理链路已经断开。", new()
                    { Stage = "DisconnectWifi", Outcome = result.Status.ToString(), NativeError = result.NativeError, ActionId = journal.ActionId,
                        Reason = result.Status == WifiRequestStatus.Skipped ? check.Allowed ? "FinalWifiChanged" : "RecheckRejected" : null });
                    disconnectResult = result.Status;
                    detail = result.Status switch
                    {
                        WifiRequestStatus.Issued => "持续离线，已请求断开选定校园 Wi-Fi；等待断开后连接原配置，再检查是否需要认证。",
                        WifiRequestStatus.Skipped => "主动断线请求未发出：" + (check.Allowed ? "最终 WLAN 复核时状态已变化。" : check.Detail),
                        _ => $"校园 Wi-Fi 重连准备失败（Windows 错误 {result.NativeError}），将退避重试。"
                    };
                    var completed = policy.DisconnectBudget with
                    {
                        State = DisconnectRequestState.Completed, CompletedAt = clock.GetUtcNow(),
                        NextAttempt = PolicyNow.AddSeconds(120)
                    };
                    SaveBudget(settings, completed);
                    policy.DisconnectCompleted(completed, PolicyNow);
                }
                catch (System.ComponentModel.Win32Exception e) { wifiRequest = new(WifiRequestStatus.Failed, e.NativeErrorCode); journal.Fault("WifiRequest", "Wi-Fi 断开请求失败", e); detail = $"校园 Wi-Fi 重连准备失败（Windows 错误 {e.NativeErrorCode}），将退避重试。"; }
                // Drop pooled connections even if the WLAN request failed; the next probe uses fresh sockets.
                directHttp?.Dispose(); directHttp = null; directKey = "";
            }
            else
            {
                try
                {
                    authenticationAcceptedAt = null;
                    lastAuthentication = PrepareAuthentication()
                        ? await new DrcomPortal(DirectClient(link), new Uri(settings.PortalOrigin), step =>
                        {
                            journal.Stage = "Authentication/" + step.Stage;
                            journal.Write("PortalStep", "认证 HTTP 步骤；只记录固定阶段、状态和耗时。", new()
                            {
                                Stage = step.Stage.ToString(), Outcome = step.Phase, ActionId = journal.ActionId,
                                ElapsedMilliseconds = step.ElapsedMilliseconds, Error = step.Error,
                                HttpStatus = step.HttpStatus, Parameters = step.Evidence
                            });
                        }).LoginDetailedAsync(credentials!, ct,
                            () => AuthorizeSubmission(settings, link, ct))
                        : new(LoginResult.Unsupported, PortalStage.Submission, PortalFailure.SafetyPersistence);
                    detail = lastAuthentication.SafeSummary;
                    if (lastAuthentication.Failure != PortalFailure.None) journal.Fault("Authentication", detail);
                    else journal.Recovered("Authentication");
                }
                catch (OperationCanceledException e) when (!ct.IsCancellationRequested) { journal.Fault("Authentication", "认证请求超时", e); detail = "认证请求超时，将退避重试。"; }
                catch (Exception e) when (e is HttpRequestException or IOException or JsonException) { journal.Fault("Authentication", "认证响应无法确认", e); detail = "认证响应无法确认，将退避重试。"; }
            }
            actionFinishedAt = clock.GetUtcNow();
            if (action != ActionKind.DisconnectWifi) policy.Attempted(PolicyNow);
            if (action == ActionKind.Authenticate && lastAuthentication is not null) CompleteAuthentication(lastAuthentication);
            journal.Write("RecoveryActionCompleted", detail, diagnostic with
            {
                Stage = action.ToString(), ActionId = journal.ActionId, Authentication = action == ActionKind.Authenticate ? lastAuthentication : null,
                Outcome = action == ActionKind.Authenticate ? lastAuthentication?.Result.ToString() ?? "Unconfirmed" : wifiRequest?.Status.ToString() ?? "Unconfirmed",
                NativeError = wifiRequest?.NativeError,
                ElapsedMilliseconds = clock.GetElapsedTime(actionTick).TotalMilliseconds,
                AuthenticationState = authentication?.State.ToString() ?? "Ready",
                AuthenticationFailure = blockFailure.ToString(), AuthenticationAttempts = authentication?.Attempts ?? 0,
                DisconnectAttempts = policy.DisconnectAttempts, DisconnectState = policy.DisconnectBudget.State.ToString(),
                NextAttempt = policy.NextAttempt > PolicyNow ? clock.GetUtcNow() + (policy.NextAttempt - PolicyNow) : null
            });
            journal.ActionId = null;
            actionEvent = action switch
            {
                ActionKind.ReconnectWifi => "连接校园 Wi-Fi",
                ActionKind.DisconnectWifi => disconnectResult switch
                {
                    WifiRequestStatus.Issued => "重连校园 Wi-Fi", WifiRequestStatus.Skipped => "取消主动断线", _ => "主动断线请求失败"
                },
                _ => "重新认证"
            };
        }
        if (policy.CredentialsBlocked) detail = lastAuthentication is null
            ? blockFailure == PortalFailure.BindingRejected
                ? "网关或设备绑定被拒绝，认证及主动断线已暂停；请核实 VLAN、MAC/AC 参数后使用“解除认证暂停”，不要仅修改密码。"
                : "自动认证因历史安全标记暂停；请先查看诊断和日志，再使用“解除认证暂停”。保存设置不会解除暂停。"
            : "自动认证及主动断线已暂停；" + lastAuthentication.SafeSummary + "；核实后使用“解除认证暂停”。";
        if (policy.CredentialsBlocked && authentication?.State == AuthenticationState.SubmissionPending && blockFailure == PortalFailure.None)
            detail = "认证提交的持久结果尚未确认，认证及主动断线已暂停；请查看诊断和日志后使用“解除认证暂停”。保存设置或密码不会解除。";
        if (policy.CredentialsBlocked && blockFailure == PortalFailure.InteractionRequired && authentication?.Attempts >= RecoveryPolicy.AuthenticationAttemptCap)
            detail = $"本次故障已达到 {RecoveryPolicy.AuthenticationAttemptCap} 次自动认证尝试，认证及主动断线已暂停；请诊断后人工确认，不会无限重试。";
        var state = !settings.Enabled ? "已暂停恢复" : direct ? "校园网在线" : link.OtherProfile ? "已切换其他 Wi-Fi" : policy.CredentialsBlocked ? "需要处理认证" : disconnectResult == WifiRequestStatus.Issued ? "正在重连校园 Wi-Fi" : observation.Condition switch
        {
            NetworkCondition.WaitingForAddress => "等待网络地址",
            NetworkCondition.Associating => "Wi-Fi 正在连接",
            NetworkCondition.AuthenticatedOffline => "已认证但外网不可达",
            NetworkCondition.PortalUnknown => "认证状态未知",
            NetworkCondition.InterfaceUnavailable => "网卡未就绪",
            _ => "正在恢复"
        };
        var status = new StatusSnapshot
        {
            Enabled = settings.Enabled, State = state, Detail = detail,
            Wifi = link.State switch { WifiState.Ready => "已连接", WifiState.WaitingForAddress => "已关联，等待 IPv4", WifiState.Associating => "正在连接", WifiState.Unavailable => "网卡未就绪", _ => "未连接" },
            Direct = direct ? "外网可达" : "外网不可达或未知",
            Portal = direct ? "未查询（外网检查已通过）" : portalState switch { PortalState.LoggedIn => "已认证", PortalState.NeedsLogin => "需认证", _ => "未知，不自动认证" },
            Tunnel = systemPath, Uu = uu, Attempts = policy.Attempts, DisconnectAttempts = policy.DisconnectAttempts, OutageSince = outageObservedAt,
            DisconnectBudgetMax = RecoveryPolicy.DisconnectAttemptCap,
            DisconnectCooldownSeconds = RecoveryPolicy.DisconnectCooldownSeconds,
            DisconnectResetAfterSeconds = RecoveryPolicy.OnlineResetSeconds,
            DisconnectRequestUnconfirmed = policy.DisconnectBudget.State == DisconnectRequestState.RequestPending,
            NextAttempt = policy.NextAttempt > PolicyNow ? clock.GetUtcNow() + (policy.NextAttempt - PolicyNow) : null,
            LastRecoverySeconds = lastRecoverySeconds, LastRecoveryAt = lastRecoveryAt,
            ActionStartedAt = actionStartedAt, ActionFinishedAt = actionFinishedAt, AuthenticationAcceptedAt = authenticationAcceptedAt,
            AuthenticationStage = lastAuthentication?.Stage.ToString() ?? "Unknown",
            AuthenticationFailure = lastAuthentication?.Failure.ToString() ?? blockFailure.ToString(),
            AuthenticationHttpStatus = lastAuthentication?.HttpStatus, CredentialRequestStarted = lastAuthentication?.CredentialRequestStarted ?? false,
            AuthenticationPersistentState = authentication?.State.ToString() ?? "Ready",
            CredentialsBlocked = policy.CredentialsBlocked
        };
        if (actionEvent is not null) Log(actionEvent, detail, snapshot: status with { LastError = lastError });
        return Publish(status);
    }

    private HttpClient DirectClient(WifiLink link)
    {
        var key = $"{link.Index}/{link.Address}/{string.Join(',', link.DnsServers.Select(a => a.ToString()))}";
        if (directHttp is null || key != directKey)
        {
            directHttp?.Dispose();
            directHttp = environment.CreateDirectClient(link);
            directKey = key;
        }
        return directHttp;
    }

    private T ReadStep<T>(string stage, Func<T> read)
    {
        journal.Stage = stage;
        try
        {
            var result = read();
            journal.Recovered("MainCycle/" + stage);
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            journal.Fault("MainCycle/" + stage, "主检测异常", e);
            throw;
        }
    }

    private void SaveBudget(Settings settings, DisconnectBudget next)
    {
        next = next with { Revision = Guid.NewGuid() };
        journal.Stage = "DisconnectBudgetWrite";
        journal.Write("SafetyStateWriteStarted", "准备持久保存断线额度；尚未确认写入。", new()
            { Stage = journal.Stage, ActionId = journal.ActionId, DisconnectState = next.State.ToString(), DisconnectAttempts = next.Attempts });
        if (!ReadStep("DisconnectBudgetWrite", () => environment.TryTransitionDisconnectBudget(settings, persistedBudget, next)))
            throw new InvalidDataException("主动断线安全状态已变化，取消操作并保留额度。");
        persistedBudget = next;
        journal.Write("SafetyStateWritten", "断线额度已持久保存。", new()
            { Stage = "DisconnectBudgetWrite", ActionId = journal.ActionId, DisconnectState = next.State.ToString(), DisconnectAttempts = next.Attempts });
    }

    private async Task<(bool Allowed, WifiLink Link, bool Online, string Detail)> RecheckDisconnectAsync(Settings settings, CancellationToken ct)
    {
        InternetEvidence? internet = null;
        PortalAttempt? inspection = null;
        (bool Allowed, WifiLink Link, bool Online, string Detail) Result(bool allowed, WifiLink observed, bool online, string detail, string reason)
        {
            journal.Write("DisconnectRecheck", "主动断线前重新采集条件；不使用较早轮次的失败作为授权。", new()
            {
                Stage = "DisconnectWifi", ActionId = journal.ActionId, Outcome = allowed ? "Authorized" : "Skipped", Reason = reason,
                WifiState = observed.State.ToString(), HasIpv4 = observed.Address is not null, Probes = internet?.Probes, Authentication = inspection
            });
            return (allowed, observed, online, detail);
        }
        ct.ThrowIfCancellationRequested();
        var link = ReadStep("WifiRead", () => environment.ReadWifi(settings));
        if (link.State != WifiState.Ready) return Result(false, link, false, "指定 Wi-Fi 已变化或尚未取得 IPv4。", "WifiNotReady");
        var current = ReadStep("SettingsRead", environment.LoadSettings);
        if (current is null || current != settings || !current.Enabled || !current.AllowActiveDisconnect)
            return Result(false, link, false, "已暂停、独立关闭或更改设置。", current != settings ? "SettingsChanged" : !current.Enabled ? "RecoveryDisabled" : "ActiveDisconnectDisabled");
        var block = ReadStep("AuthenticationStateRead", environment.LoadAuthenticationBlock);
        if (block?.BlocksSubmissions == true) return Result(false, link, false, "认证安全状态已阻断。", "AuthenticationPaused");
        // New client: do not reuse sockets or the earlier cycle's failure evidence.
        using var fresh = environment.CreateDirectClient(link);
        journal.Stage = "DisconnectRecheckInternet";
        internet = await HealthChecks.InternetDetailedAsync(fresh, ct);
        journal.Recovered("MainCycle/DisconnectRecheckInternet");
        if (internet.Online) return Result(false, link, true, "新鲜 HTTPS 检查已恢复，取消断线。", "FreshInternetRecovered");
        journal.Stage = "DisconnectRecheckPortal";
        inspection = await new DrcomPortal(fresh, new Uri(settings.PortalOrigin)).InspectDetailedAsync(ct);
        if (inspection.ObservedState == PortalState.NeedsLogin) return Result(false, link, false, "最新状态明确需认证，取消断线并优先等待安全认证恢复。", "AuthenticateRequired");
        ct.ThrowIfCancellationRequested();
        current = ReadStep("SettingsRead", environment.LoadSettings);
        block = ReadStep("AuthenticationStateRead", environment.LoadAuthenticationBlock);
        var finalLink = ReadStep("WifiRead", () => environment.ReadWifi(settings));
        if (current is null || current != settings || !current.Enabled || !current.AllowActiveDisconnect || block?.BlocksSubmissions == true ||
            finalLink.State != WifiState.Ready || finalLink.Index != link.Index || !Equals(finalLink.Address, link.Address))
            return Result(false, finalLink, false, "最终复核发现暂停、认证阻断、用户切网或地址变化。",
                current != settings ? "SettingsChanged" : current?.Enabled != true ? "RecoveryDisabled" : !current.AllowActiveDisconnect ? "ActiveDisconnectDisabled" :
                block?.BlocksSubmissions == true ? "AuthenticationPaused" : "FinalWifiChanged");
        return Result(true, finalLink, false, "", "ChecksPassed");
    }

    private async Task PollAuxiliaryAsync(Settings settings, WifiLink link, CancellationToken ct)
    {
        var key = $"{settings.ProxyUrl}/{settings.UuEndpoint}/{link.Index}/{link.Address}";
        if (key != auxiliaryKey)
        {
            CancelAuxiliary();
            auxiliaryKey = key;
            lastAuxiliary = null;
            systemPath = "系统路径未知，等待新检测";
            uu = "UU 状态未知，等待新检测";
        }
        if (auxiliaryTask is { IsCompleted: true })
        {
            try
            {
                var result = await auxiliaryTask;
                if (!discardAuxiliary) { (systemPath, uu) = result; journal.Recovered("Auxiliary"); }
            }
            catch (Exception e)
            {
                if (!discardAuxiliary)
                {
                    systemPath = "系统路径未知，辅助检测失败";
                    uu = "UU 状态未知，稍后重试";
                    journal.Fault("Auxiliary", "辅助检测异常", e);
                    Log("辅助检测失败", lastError);
                }
            }
            finally { auxiliaryTask = null; auxiliaryCancellation?.Dispose(); auxiliaryCancellation = null; discardAuxiliary = false; }
        }
        if (auxiliaryTask is not null && clock.GetElapsedTime(auxiliaryStarted) >= TimeSpan.FromSeconds(3))
        {
            CancelAuxiliary();
            systemPath = "系统路径未知，辅助检测超时";
            uu = "UU 状态未知，辅助检测超时";
            journal.Fault("Auxiliary", "辅助检测超时");
        }
        if (auxiliaryTask is null && (lastAuxiliary is null || clock.GetElapsedTime(lastAuxiliary.Value) >= TimeSpan.FromSeconds(20)))
        {
            lastAuxiliary = auxiliaryStarted = clock.GetTimestamp();
            discardAuxiliary = false;
            auxiliaryCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            auxiliaryTask = StartAuxiliaryAsync(settings, link, auxiliaryCancellation.Token);
        }
    }

    private async Task<(string SystemPath, string Uu)> StartAuxiliaryAsync(Settings settings, WifiLink link, CancellationToken ct) =>
        await environment.CheckAuxiliaryAsync(settings, link, ct);

    private void CancelAuxiliary()
    {
        discardAuxiliary = true;
        try { auxiliaryCancellation?.Cancel(); }
        catch (Exception e) { journal.Fault("AuxiliaryCancel", "辅助检测取消异常", e); }
    }

    private void StopAuxiliary()
    {
        CancelAuxiliary();
        if (auxiliaryTask is not null)
            _ = auxiliaryTask.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        auxiliaryCancellation?.Dispose();
        auxiliaryCancellation = null;
    }

    private void UpdatePower(bool enabled)
    {
        try { environment.UpdatePower(enabled); journal.Recovered("Power"); }
        catch (Exception e) { journal.Fault("Power", "电源请求异常", e); }
    }

    private bool PrepareAuthentication()
    {
        var current = ReadStep("AuthenticationStateRead", environment.LoadAuthenticationBlock);
        if (current != authentication || current?.BlocksSubmissions == true || PolicyNow < authenticationRetryUntil)
            return RejectSubmission(current != authentication ? "AuthenticationStateChanged" : current?.BlocksSubmissions == true ? "AuthenticationPaused" : "AuthenticateCooldown");
        if ((current?.Attempts ?? 0) >= 3)
        {
            WriteAuthentication(new(PortalFailure.InteractionRequired) { Attempts = 3 });
            return RejectSubmission("AuthenticationAttemptLimit");
        }
        // Reserve an attempt and a conservative retry before even the preflight. If a
        // later cooldown write fails, restarting cannot turn that failure into a new authorization.
        return WriteAuthentication(new()
        {
            State = AuthenticationState.RetryCoolingDown, Attempts = (current?.Attempts ?? 0) + 1,
            RetryDelaySeconds = 60, RetryNotBefore = clock.GetUtcNow().AddSeconds(60)
        });
    }

    private bool AuthorizeSubmission(Settings settings, WifiLink link, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var currentSettings = ReadStep("SettingsRead", environment.LoadSettings);
        var currentLink = ReadStep("WifiRead", () => environment.ReadWifi(settings));
        if (currentSettings is null || currentSettings != settings || !currentSettings.Enabled || currentLink.State != WifiState.Ready ||
            currentLink.Index != link.Index || !Equals(currentLink.Address, link.Address))
            return RejectSubmission(currentSettings != settings ? "SettingsChanged" : currentSettings?.Enabled != true ? "RecoveryDisabled" : "WifiChanged");
        var current = ReadStep("AuthenticationStateRead", environment.LoadAuthenticationBlock);
        if (current != authentication || current?.State != AuthenticationState.RetryCoolingDown) return RejectSubmission("AuthenticationStateChanged");
        if (!WriteAuthentication(new()
        {
            State = AuthenticationState.SubmissionPending, Attempts = current.Attempts
        })) return RejectSubmission("SubmissionReservationFailed");
        ct.ThrowIfCancellationRequested();
        currentSettings = ReadStep("SettingsRead", environment.LoadSettings);
        currentLink = ReadStep("WifiRead", () => environment.ReadWifi(settings));
        var allowed = currentSettings is not null && currentSettings == settings && currentSettings.Enabled && currentLink.State == WifiState.Ready &&
            currentLink.Index == link.Index && Equals(currentLink.Address, link.Address);
        if (!allowed) return RejectSubmission("FinalSettingsOrWifiChanged");
        journal.Write("SubmissionAuthorized", "提交授权已持久保存，最终复核通过；尚未表示服务器接受。", new()
            { Stage = "Submission", ActionId = journal.ActionId, Outcome = "Authorized" });
        return true;
    }

    private bool RejectSubmission(string reason)
    {
        journal.Write("SubmissionNotAuthorized", "本轮未获得凭据提交授权；未因此绕过安全守卫。", new()
            { Stage = "Submission", ActionId = journal.ActionId, Outcome = "Blocked", Reason = reason });
        return false;
    }

    private void CompleteAuthentication(PortalAttempt attempt)
    {
        if (attempt.Failure == PortalFailure.SafetyPersistence) return;
        var attempts = authentication?.Attempts ?? 0;
        AuthenticationBlock next;
        if (attempt.Result is LoginResult.RejectedBinding or LoginResult.RejectedCredentials or LoginResult.NeedsInteraction or LoginResult.Unsupported)
            next = new(attempt.Failure) { Attempts = attempts };
        else if (attempt.Result == LoginResult.Accepted || attempt.Failure == PortalFailure.AlreadyAuthenticated)
            next = new() { State = AuthenticationState.Ready, Attempts = attempts };
        else if (attempt.CredentialRequestStarted && !attempt.RetryPermitted)
        {
            // SubmissionPending was flushed before sending. An ambiguous response never grants a retry.
            policy.CredentialsBlocked = true;
            journal.Fault("Authentication", "认证提交结果未确认，保留持久安全暂停；请诊断后人工解除。");
            return;
        }
        else if (attempts >= 3) next = new(PortalFailure.InteractionRequired) { Attempts = 3 };
        else
        {
            var seconds = (int)Math.Clamp(Math.Ceiling((policy.NextAttempt - PolicyNow).TotalSeconds), 2, 60);
            next = new(attempt.Failure)
            {
                State = AuthenticationState.RetryCoolingDown, Attempts = attempts,
                RetryDelaySeconds = seconds, RetryNotBefore = clock.GetUtcNow().AddSeconds(seconds)
            };
        }
        if (WriteAuthentication(next) && attempt.Result == LoginResult.Accepted && attempt.CredentialRequestStarted)
            authenticationAcceptedAt = clock.GetUtcNow();
    }

    private bool WriteAuthentication(AuthenticationBlock next)
    {
        authenticationExpected = authentication;
        authenticationToWrite = next with { Revision = Guid.NewGuid() };
        return PersistAuthenticationBlock();
    }

    private bool PersistAuthenticationBlock()
    {
        journal.Stage = "AuthenticationStateWrite";
        journal.Write("SafetyStateWriteStarted", "准备持久保存认证授权状态；尚未确认写入。", new()
        {
            Stage = journal.Stage, ActionId = journal.ActionId, AuthenticationState = authenticationToWrite?.State.ToString(),
            AuthenticationFailure = authenticationToWrite?.Failure.ToString(), AuthenticationAttempts = authenticationToWrite?.Attempts
        });
        try
        {
            if (!environment.TryTransitionAuthentication(authenticationExpected, authenticationToWrite!))
            {
                authenticationBlockPending = false;
                policy.CredentialsBlocked = true;
                journal.Fault("AuthenticationStateWrite", "认证授权状态已变化，取消本次提交；下一轮重新检查。");
                return false;
            }
            authentication = authenticationToWrite;
            authenticationBlockPending = false;
            policy.CredentialsBlocked = authentication!.BlocksSubmissions;
            blockFailure = authentication.Failure;
            authenticationRetryUntil = authentication.State == AuthenticationState.RetryCoolingDown
                ? PolicyNow.AddSeconds(authentication.RetryDelaySeconds) : DateTimeOffset.MinValue;
            journal.Recovered("AuthenticationStateWrite");
            journal.Write("SafetyStateWritten", "认证授权状态已持久保存。", new()
            {
                Stage = "AuthenticationStateWrite", ActionId = journal.ActionId, AuthenticationState = authentication.State.ToString(),
                AuthenticationFailure = authentication.Failure.ToString(), AuthenticationAttempts = authentication.Attempts
            });
            return true;
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            if (authenticationToWrite?.State == AuthenticationState.SubmissionPending)
                authenticationToWrite = new(PortalFailure.SafetyPersistence) { Attempts = authentication?.Attempts ?? 0, Revision = Guid.NewGuid() };
            authenticationBlockPending = true;
            policy.CredentialsBlocked = true;
            blockFailure = authenticationToWrite?.Failure ?? PortalFailure.SafetyPersistence;
            journal.Fault("AuthenticationStateWrite", "认证安全状态写入失败，停止提交；已持久保存的保守状态继续生效", e);
            return false;
        }
    }

    private StatusSnapshot Publish(StatusSnapshot status)
    {
        status = status with { UpdatedAt = clock.GetUtcNow(), LastError = lastError,
            DisconnectAttempts = policy.DisconnectAttempts, CredentialsBlocked = policy.CredentialsBlocked };
        var key = status.State + "/" + status.Detail + "/" + status.Wifi + "/" + status.Direct + "/" + status.Portal + "/" + status.Tunnel + "/" + status.Uu + "/" + status.LastError;
        if (key != lastEvent && Log(status.State, status.Detail, snapshot: status)) lastEvent = key;
        status = status with { LastError = journal.ErrorExcept("StatusWrite") };
        journal.Stage = "StatusWrite";
        try { environment.WriteStatus(status); journal.Recovered("StatusWrite"); }
        catch (Exception e) when (IsStorageFailure(e)) { journal.Fault("StatusWrite", "状态写入失败", e); }
        return status with { LastError = lastError };
    }

    private bool Log(string action, string detail, double? seconds = null, StatusSnapshot? snapshot = null)
        => journal.Write("StatusChanged", detail, snapshot: snapshot, displayAction: action, seconds: seconds);

    private static bool IsStorageFailure(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    public override void Dispose()
    {
        StopAuxiliary();
        directHttp?.Dispose();
        base.Dispose();
    }
}
