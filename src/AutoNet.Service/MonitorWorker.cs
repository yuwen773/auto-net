using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AutoNet.Core;
using Microsoft.Extensions.Hosting;

namespace AutoNet.Service;

public sealed class MonitorWorker(IMonitorEnvironment environment, TimeProvider clock, PortalBudgets? budgets = null, bool qaSubmissionLost = false) : BackgroundService
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
    private StatusNotification? notification;
    private bool blockedPublished;
    private bool authenticationBlockPending;
    private bool submissionResolutionStorageFault;
    private Guid? submissionResolutionFaultRevision;
    private AuthenticationBlock? authentication;
    private AuthenticationBlock? authenticationExpected;
    private AuthenticationBlock? authenticationToWrite;
    private readonly SubmissionRetryPolicy submissionRetry = new();
    private SubmissionBudget? submissionBudget;
    private SubmissionBudget? activeSubmission;
    private SubmissionRetryDecision submissionDecision;
    private bool submissionExhaustedPublished;
    private bool retryingSubmission;
    private DateTimeOffset pendingRetryAfter;
    private DateTimeOffset nextSubmissionRecheck;
    private DateTimeOffset authenticationRetryUntil;
    // ADR-0001 recheck schedule: read-only probes about 2s and 6s after the pending state
    // appeared; the schedule re-arms after a restart.
    private DateTimeOffset? submissionPendingSince;
    private int submissionRechecks;
    private static readonly TimeSpan[] SubmissionRecheckDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];
    private string lastError => journal.LastError;
    private HttpClient? directHttp;
    private string directKey = "";
    // ADR-0005 portal layout detection: the durable signal derived from the latest portal
    // observation, plus the transition tracker that keeps the announcement one-shot.
    private bool portalLayoutSuspected;
    private bool portalLayoutPublished;
    // ADR-0002 browser fallback: the cap-reached flag with its cooldown timestamp. The
    // service never launches a browser (session 0); the tray acts on the flag.
    private bool browserFallbackRequested;
    private bool browserFallbackPublished;
    private DateTimeOffset browserFallbackNotBefore;
    // ADR-0009 embedded auth channel: same evidence bar as the browser fallback plus an
    // explicit PortalLayoutChanged signal, gated by the configured AuthChannelMode. The
    // service only raises the flag; the tray launches the elevated helper in the user session.
    private bool authChannelRequested;
    private bool authChannelPublished;
    private DateTimeOffset authChannelNotBefore;
    private string authChannelMode = AuthChannelModes.ManualOnly;
    // One one-shot notification per cycle: a more specific event raised earlier in the
    // same cycle already tells the user authentication is paused, so the generic pause
    // announcement in Publish must not supersede it.
    private bool notifiedThisCycle;
    private static readonly TimeSpan BrowserFallbackCooldown = RecoveryPolicy.BrowserFallbackOpenCooldown;

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
        authChannelMode = AuthChannelModes.ManualOnly;
        notifiedThisCycle = false;
        journal.BeginCycle();
        try { return await TickAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            journal.Write("CycleInterrupted", "检测轮次被取消；进行中的动作结果未确认。", new() { Stage = journal.Stage, ActionId = journal.ActionId, Reason = "Cancellation" });
            throw;
        }
        catch (Exception e)
        {
            activeSubmission = null;
            authChannelRequested = false;
            authChannelPublished = false;
            authChannelNotBefore = default;
            policy.InterruptObservation();
            UpdatePower(false);
            journal.Fault("MainCycle/" + journal.Stage, "主检测异常", e);
            journal.Decision(new() { Stage = journal.Stage, Outcome = "None", Reason = "ObservationFailed", Error = DiagnosticError.From(e) }, new() { State = "检测异常" });
            return Publish(new StatusSnapshot { AuthChannelMode = authChannelMode, State = "检测异常", Detail = "检测将自动重试；" + lastError });
        }
    }

    private async Task<StatusSnapshot> TickAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = ReadStep("SettingsRead", environment.LoadSettings);
        if (settings is null)
        {
            authChannelRequested = false;
            authChannelPublished = false;
            authChannelNotBefore = default;
            policy.Reset();
            UpdatePower(false);
            CancelAuxiliary();
            journal.Suspend("SettingsMissing");
            return Publish(new StatusSnapshot { State = "等待设置", Detail = "请在托盘窗口中设置校园 Wi-Fi 和认证信息。" });
        }
        authChannelMode = AuthChannelModes.IsKnown(settings.AuthChannelMode) ? settings.AuthChannelMode : AuthChannelModes.ManualOnly;
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
        submissionBudget = ReadStep("SubmissionBudgetRead", environment.LoadSubmissionBudget);
        if (submissionBudget is { State: SubmissionBudgetState.Reserved } interrupted && activeSubmission is null)
        {
            using var execution = environment.TryAcquireSubmissionExecution();
            if (execution is not null && environment.TryTransitionSubmissionBudget(interrupted, SubmissionBudgetState.PossiblySent))
            {
                submissionBudget = environment.LoadSubmissionBudget();
                journal.Write("SubmissionInterrupted", "上次预留的执行进程已结束；按可能发送计数并重新完整等待。",
                    new() { Stage = "Submission", Reason = "InterruptedReservation", Outcome = "PossiblySent" });
            }
        }
        submissionRetry.Evaluate(submissionBudget, clock.GetUtcNow(), clock.GetElapsedTime(clockStart));
        if (loadedAuthentication != authentication)
        {
            authentication = loadedAuthentication;
            // Wall time cannot prove how much of a retry elapsed while the service was down.
            authenticationRetryUntil = loadedAuthentication?.State == AuthenticationState.RetryCoolingDown
                ? PolicyNow.AddSeconds(loadedAuthentication.RetryDelaySeconds) : DateTimeOffset.MinValue;
        }
        var block = authenticationBlockPending ? authenticationToWrite : authentication;
        if (submissionResolutionStorageFault && block?.Revision != submissionResolutionFaultRevision)
        {
            submissionResolutionStorageFault = false;
            submissionResolutionFaultRevision = null;
        }
        if (policy.CredentialsBlocked && (block is null || !block.BlocksSubmissions))
        {
            journal.Recovered("Authentication");
            lastAuthentication = null;
        }
        policy.CredentialsBlocked = block?.BlocksSubmissions == true;
        blockFailure = block?.Failure ?? PortalFailure.None;
        if (block?.State == AuthenticationState.RetryCoolingDown) policy.DeferAuthenticationUntil(authenticationRetryUntil);
        // ADR-0001: the recheck schedule arms when the durable pending state is first observed
        // (including after a restart) and is cleared as soon as the state leaves pending.
        if (block?.State == AuthenticationState.SubmissionPending) submissionPendingSince ??= PolicyNow;
        else { submissionPendingSince = null; submissionRechecks = 0; }
        var credentials = ReadStep("CredentialsRead", environment.LoadCredentials);
        UpdatePower(settings.Enabled && settings.KeepAwakeOnAc);
        var link = ReadStep("WifiRead", () => environment.ReadWifi(settings));
        if (link.OtherProfile)
        {
            authChannelRequested = false;
            authChannelPublished = false;
            authChannelNotBefore = default;
            policy.Reset();
            outageObservedAt = null;
            CancelAuxiliary();
            journal.Suspend("OtherWifiSelected");
            var budgetCode = SubmissionBudgetCode();
            return Publish(new StatusSnapshot
            {
                Enabled = settings.Enabled, AuthChannelMode = settings.AuthChannelMode,
                State = "已切换其他 Wi-Fi", Wifi = "其他配置", Detail = "保留你当前选择的网络，等待校园 Wi-Fi。",
                SubmissionBudgetCode = budgetCode,
                SubmissionAttempts = submissionBudget?.Attempts ?? (budgetCode == SubmissionBudgetCodes.Unused ? 0 : null),
                SubmissionAttemptMax = SubmissionRetryPolicy.AttemptCap,
                SubmissionBudgetExhausted = submissionBudget is null
                    ? budgetCode == SubmissionBudgetCodes.Unused ? false : null
                    : submissionBudget.Attempts >= SubmissionRetryPolicy.AttemptCap
            });
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
                inspection = await new DrcomPortal(http, new Uri(settings.PortalOrigin), null, budgets).InspectDetailedAsync(ct);
                portalState = inspection.ObservedState;
            }
        }
        else { directHttp?.Dispose(); directHttp = null; directKey = ""; }
        await RecheckSubmissionAsync(settings, link, ct);
        submissionDecision = submissionRetry.Evaluate(submissionBudget, clock.GetUtcNow(), clock.GetElapsedTime(clockStart), direct);
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
            Notify(NotificationCodes.InternetRecovered);
            Log("观测到联网恢复", $"HTTPS 外网检查已通过；本轮自动动作 {policy.Attempts} 次。恢复来源无法确认，可能包含人工处理或网络自行恢复。", lastRecoverySeconds,
                new StatusSnapshot { Wifi = "已连接", Direct = "外网可达", Notification = notification, Attempts = policy.Attempts, OutageSince = outageObservedAt,
                    ActionStartedAt = actionStartedAt, ActionFinishedAt = actionFinishedAt, AuthenticationAcceptedAt = authenticationAcceptedAt,
                    LastRecoveryAt = lastRecoveryAt, Tunnel = systemPath, Uu = uu });
        }
        var oldBudget = policy.DisconnectBudget;
        var action = policy.Decide(observation, PolicyNow, settings.Enabled, credentials is not null, settings.AllowActiveDisconnect);
        retryingSubmission = authentication?.State == AuthenticationState.SubmissionPending &&
            (submissionBudget is { Attempts: > 0 } || environment.IsConfirmedSubmissionRestart(submissionBudget));
        if (retryingSubmission && submissionDecision == SubmissionRetryDecision.Authorized && settings.Enabled && credentials is not null &&
            link.State == WifiState.Ready && portalState != PortalState.LoggedIn && PolicyNow >= pendingRetryAfter)
            action = ActionKind.Authenticate;
        if (action == ActionKind.Authenticate && submissionDecision != SubmissionRetryDecision.Authorized)
        {
            RejectSubmission("SubmissionBudget" + submissionDecision);
            action = ActionKind.None;
        }
        if (retryingSubmission && submissionDecision == SubmissionRetryDecision.Exhausted)
            RejectSubmission("SubmissionBudgetExhausted");
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
        using var submissionExecution = action == ActionKind.Authenticate ? environment.TryAcquireSubmissionExecution() : null;
        if (action == ActionKind.Authenticate && submissionExecution is null)
        {
            RejectSubmission("SubmissionExecutionUnavailable");
            action = ActionKind.None;
        }
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
                        }, budgets, qaSubmissionLost).LoginDetailedAsync(credentials!, ct, allowUnknownStatus: retryingSubmission,
                            authorizeSubmissionAsync: review => AuthorizeSubmissionAsync(settings, link, ct, review))
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
        // #35: project the budget again after the action so a reservation, result transition
        // or reset in this cycle is visible in the same snapshot. The pre-action decision
        // above still governs the action itself and the channel-fallback condition.
        var displaySubmissionDecision = submissionRetry.Evaluate(
            submissionBudget, clock.GetUtcNow(), clock.GetElapsedTime(clockStart), direct);
        var submissionExhausted = submissionBudget is { Attempts: >= SubmissionRetryPolicy.AttemptCap };
        // Only announce exhaustion when the quota is the operative stop reason: a terminal
        // authentication failure (bad password, binding rejected, interaction required) has
        // its own pause wording and must not be superseded by the generic budget message.
        var exhaustedStopsSubmission = submissionExhausted && policy.CredentialsBlocked &&
            authentication?.State == AuthenticationState.SubmissionPending && blockFailure == PortalFailure.None;
        // ADR-0005 portal layout detection: a preflight failure inside the structural set
        // means the portal's response shape changed — not bad credentials, not a network
        // fault. Aggregate it into one durable signal (journal event, status flag, one-shot
        // notification) instead of a generic dynamic parser, which has no fixtures and
        // would put guesswork into the production authentication path. Only this cycle's
        // fresh evidence counts; a persisted block keeps the signal across restarts.
        // Computed before the budget notification so the two events sharing the one-shot
        // channel cannot silently drop one of them.
        portalLayoutSuspected = PortalLayoutSignal.IsStructural(blockFailure) ||
            (inspection is { Failure: var observedFailure } && PortalLayoutSignal.IsStructural(observedFailure)) ||
            (action == ActionKind.Authenticate && lastAuthentication is { Failure: var attemptedFailure } && PortalLayoutSignal.IsStructural(attemptedFailure));
        var layoutWillPublish = portalLayoutSuspected && !portalLayoutPublished;
        // The one-shot slot holds a single event. If the portal layout event is about to
        // take it this cycle, leave the budget event unpublished instead of marking it as
        // delivered: the layout event is already published, so the next cycle claims the
        // slot for the budget event and it is not lost for the rest of the pending episode.
        if (exhaustedStopsSubmission && !submissionExhaustedPublished && !layoutWillPublish)
        {
            submissionExhaustedPublished = true;
            Notify(NotificationCodes.SubmissionBudgetExhausted);
            journal.Write("SubmissionBudgetExhausted", "提交额度已耗尽，已停止自动提交；" + RecheckText(link,
                    "继续低频只读复检，", "校园 Wi-Fi 当前不可用，暂不发起只读复检，恢复连接后继续低频只读复检；") +
                "门户明确成功或手动重新开始认证后重置额度。",
                diagnostic with { Stage = "Submission", Outcome = "Exhausted", AuthenticationAttempts = authentication?.Attempts ?? 0 });
        }
        if (!exhaustedStopsSubmission) submissionExhaustedPublished = false;
        if (layoutWillPublish)
        {
            portalLayoutPublished = true;
            Notify(NotificationCodes.PortalLayoutChanged);
            journal.Write("PortalLayoutChanged", "门户预检失败落在结构类集合；门户结构疑似变化，自动认证已安全暂停，请导出诊断包反馈给网管。",
                diagnostic with { Authentication = inspection ?? lastAuthentication });
        }
        if (!portalLayoutSuspected) portalLayoutPublished = false;
        // ADR-0002 browser fallback: consecutive authentication failures reaching the cap
        // mean the automatic path is exhausted. The service only raises the flag with a
        // cooldown timestamp in status.json — it runs in session 0 and cannot show a
        // browser to the user, and credentials never enter a browser process. The
        // timestamp is stamped once per episode (grant time + cooldown); the tray owns
        // first-open dedup and its own repeat cadence, and a service restart re-grants.
        browserFallbackRequested = blockFailure == PortalFailure.InteractionRequired &&
            (authentication?.Attempts ?? 0) >= RecoveryPolicy.AuthenticationAttemptCap;
        if (browserFallbackRequested && !browserFallbackPublished)
        {
            browserFallbackPublished = true;
            browserFallbackNotBefore = clock.GetUtcNow().Add(BrowserFallbackCooldown);
            Notify(NotificationCodes.BrowserFallbackRequested);
            journal.Write("BrowserFallbackRequested", $"连续自动认证失败已达 {RecoveryPolicy.AuthenticationAttemptCap} 次阈值；请在用户会话用默认浏览器打开门户认证页人工处理。只开页、不填表，凭据不进浏览器进程。",
                diagnostic with { AuthenticationAttempts = authentication?.Attempts ?? 0, AuthenticationAttemptMax = RecoveryPolicy.AuthenticationAttemptCap });
        }
        if (!browserFallbackRequested) { browserFallbackPublished = false; browserFallbackNotBefore = default; }
        // ADR-0009 embedded auth channel: same threshold and failure shape as the browser
        // fallback plus a fresh PortalLayoutChanged signal, only in Automatic mode. The
        // helper runs in the user session (WebView2 needs an interactive session), reads
        // the machine-scope credentials itself and reports through its result file — the
        // credentials never travel through status.json, and the service stays session 0.
        authChannelRequested = RecoveryPolicy.ShouldRequestAuthChannel(
            settings.AuthChannelMode, portalLayoutSuspected && PortalLayoutSignal.IsStructural(authentication?.ChannelFallbackCause ?? PortalFailure.None),
            blockFailure, authentication?.Attempts ?? 0) ||
            settings.AuthChannelMode == AuthChannelModes.Automatic && retryingSubmission &&
            submissionDecision == SubmissionRetryDecision.Authorized && action == ActionKind.Authenticate &&
            lastAuthentication is { CredentialRequestStarted: false } failedPreflight && PortalLayoutSignal.IsStructural(failedPreflight.Failure);
        if (authChannelRequested && !authChannelPublished)
        {
            authChannelPublished = true;
            authChannelNotBefore = clock.GetUtcNow().Add(RecoveryPolicy.AuthChannelCooldown);
            Notify(NotificationCodes.AuthChannelStarted);
            journal.Write("AuthChannelStarted", "门户结构性预检失败；已请求在用户会话启动内嵌认证通道，实际提交仍需独立授权。",
                diagnostic with { AuthenticationAttempts = authentication?.Attempts ?? 0, AuthenticationAttemptMax = RecoveryPolicy.AuthenticationAttemptCap });
        }
        if (!authChannelRequested) { authChannelPublished = false; authChannelNotBefore = default; }
        if (policy.CredentialsBlocked) detail = lastAuthentication is null
            ? blockFailure == PortalFailure.BindingRejected
                ? "网关或设备绑定被拒绝，认证及主动断线已暂停；请核实 VLAN、MAC/AC 参数后使用“解除认证暂停”，不要仅修改密码。"
                : "自动认证因历史安全标记暂停；请先查看诊断和日志，再使用“解除认证暂停”。保存设置不会解除暂停。"
            : "自动认证及主动断线已暂停；" + lastAuthentication.SafeSummary + "；核实后使用“解除认证暂停”。";
        if (policy.CredentialsBlocked && authentication?.State == AuthenticationState.SubmissionPending && blockFailure == PortalFailure.None)
            detail = submissionBudget is null
                ? "认证提交的持久结果尚未确认且缺少提交额度记录；" + RecheckText(link,
                    "保持暂停并只读复检，不自动重发凭据。",
                    "保持暂停；校园 Wi-Fi 当前不可用，暂不发起只读复检，恢复连接后再复检，不自动重发凭据。")
                : displaySubmissionDecision == SubmissionRetryDecision.Exhausted
                    ? "已停止自动提交：首次提交后的两次额外额度已用尽；" + RecheckText(link,
                        "继续低频只读复检。",
                        "校园 Wi-Fi 当前不可用，暂不发起只读复检，恢复连接后继续低频只读复检。")
                    : "认证提交结果未知；" + RecheckText(link,
                        "只读复检继续，",
                        "校园 Wi-Fi 当前不可用，暂不发起只读复检，恢复连接后再继续；") +
                        "只有外网不可达且共享额度和至少120秒等待满足后才受控重试。" + (displaySubmissionDecision switch
                    {
                        SubmissionRetryDecision.InternetOnline => "外网已恢复，暂停提交。",
                        SubmissionRetryDecision.WaitClock or SubmissionRetryDecision.InvalidTime => "时间不可信，保守延后。",
                        SubmissionRetryDecision.InFlight => "等待上次提交执行结束。",
                        SubmissionRetryDecision.Concluded => "本次提交已明确结束，已停止自动提交。",
                        _ => "等待下一次安全复核。"
                    });
        if (authentication?.State != AuthenticationState.SubmissionPending && submissionBudget is { Attempts: > 0 } &&
            displaySubmissionDecision is SubmissionRetryDecision.Concluded or SubmissionRetryDecision.Exhausted)
            detail += displaySubmissionDecision == SubmissionRetryDecision.Exhausted
                ? " 已停止自动提交：提交额度已用尽；继续检测网络。"
                : " 已停止自动提交：本次凭据提交已明确结束；继续检测网络。";
        if (policy.CredentialsBlocked && browserFallbackRequested)
            detail = $"本次故障已达到 {RecoveryPolicy.AuthenticationAttemptCap} 次自动认证尝试，认证及主动断线已暂停；请诊断后人工确认，不会无限重试。可打开门户认证页在浏览器中人工登录。";
        // Appended last: the pause wordings above replace the detail wholesale, and the
        // structural scenario is exactly the one that ends up paused.
        if (portalLayoutSuspected) detail += " 门户结构疑似变化，自动认证已安全暂停；请导出诊断包反馈给网管。";
        var state = !settings.Enabled ? "已暂停恢复" : direct ? "校园网在线" : link.OtherProfile ? "已切换其他 Wi-Fi" : policy.CredentialsBlocked ? "需要处理认证" : disconnectResult == WifiRequestStatus.Issued ? "正在重连校园 Wi-Fi" : observation.Condition switch
        {
            NetworkCondition.WaitingForAddress => "等待网络地址",
            NetworkCondition.Associating => "Wi-Fi 正在连接",
            NetworkCondition.AuthenticatedOffline => "已认证但外网不可达",
            NetworkCondition.PortalUnknown => "认证状态未知",
            NetworkCondition.InterfaceUnavailable => "网卡未就绪",
            _ => "正在恢复"
        };
        var submissionBudgetCode = SubmissionBudgetCode();
        var status = new StatusSnapshot
        {
            Enabled = settings.Enabled, AuthChannelMode = settings.AuthChannelMode, State = state, Detail = detail,
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
            SubmissionResolutionStorageFault = submissionResolutionStorageFault,
            AuthenticationHttpStatus = lastAuthentication?.HttpStatus, CredentialRequestStarted = lastAuthentication?.CredentialRequestStarted ?? false,
            AuthenticationPersistentState = authentication?.State.ToString() ?? "Ready",
            CredentialsBlocked = policy.CredentialsBlocked,
            SubmissionBudgetCode = submissionBudgetCode,
            SubmissionRetryCode = displaySubmissionDecision.ToString(),
            SubmissionAttempts = submissionBudget?.Attempts ?? (submissionBudgetCode == SubmissionBudgetCodes.Unused ? 0 : null),
            SubmissionAttemptMax = SubmissionRetryPolicy.AttemptCap,
            SubmissionBudgetExhausted = submissionBudget is null
                ? submissionBudgetCode == SubmissionBudgetCodes.Unused ? false : null
                : submissionExhausted,
            SubmissionNextPossibleAt = submissionRetry.NextPossibleAt
        };
        status = WithFallbackFlags(status);
        if (actionEvent is not null) Log(actionEvent, detail, snapshot: status with { LastError = lastError });
        return Publish(status);
    }

    // ADR-0001 recheck resolution: an unknown submission outcome is classified by read-only
    // chkstatus probes alone — about 2s and 6s, then every 60s while pending, never a new
    // credential request; the internet probe is recorded as evidence only. LoggedIn resolves to
    // Accepted (release audit first, then a revision-compared block clear with the attempt count
    // reset); NeedsLogin retains the shared submission interval. Legacy pending records
    // without a submission budget remain paused rather than gaining a fresh allowance.
    private async Task RecheckSubmissionAsync(Settings settings, WifiLink link, CancellationToken ct)
    {
        if (submissionPendingSince is not { } since) return;
        if (submissionRechecks < SubmissionRecheckDelays.Length
            ? PolicyNow < since.Add(SubmissionRecheckDelays[submissionRechecks]) : PolicyNow < nextSubmissionRecheck) return;
        // The recheck reads chkstatus through the Wi-Fi adapter; while the link is unusable a
        // probe would only fail, so the slot is not spent and the probe runs once it returns.
        if (link.State != WifiState.Ready) return;
        using var execution = environment.TryAcquireSubmissionExecution();
        if (execution is null) return;
        var ordinal = ++submissionRechecks;
        nextSubmissionRecheck = PolicyNow.AddSeconds(60);
        journal.Stage = "SubmissionRecheck";
        if (ordinal == 1) Notify(NotificationCodes.SubmissionRechecking);
        journal.Write("SubmissionRechecking", $"提交结果未知，开始第 {ordinal} 次只读复检；不发送新凭据请求。", new()
            { Stage = "SubmissionRecheck", Outcome = "Started", AuthenticationAttempts = authentication?.Attempts ?? 0 });
        var inspection = await new DrcomPortal(DirectClient(link), new Uri(settings.PortalOrigin), null, budgets).InspectDetailedAsync(ct);
        switch (inspection.ObservedState)
        {
            case PortalState.LoggedIn:
                ResolveSubmissionByRecheck(inspection);
                return;
            case PortalState.NeedsLogin:
                journal.Write("SubmissionRechecked", submissionBudget is null
                    ? "复检确认门户未登录，但缺少提交额度记录；保守保留暂停，不自动初始化新额度。"
                    : "复检确认门户未登录；保留提交额度与最短间隔，不据此提前授权。",
                    new() { Stage = "SubmissionRecheck", Outcome = "NeedsLogin", Authentication = inspection });
                return;
            default:
                if (submissionRechecks < SubmissionRecheckDelays.Length) return;
                if (submissionRechecks == SubmissionRecheckDelays.Length) Notify(NotificationCodes.SubmissionUnresolved);
                journal.Write("SubmissionUnresolved", "只读复检仍无法定性；保持提交结果未知状态，继续低频复检并遵守提交额度。",
                    new() { Stage = "SubmissionRecheck", Authentication = inspection });
                return;
        }
    }

    private bool ResolveSubmissionByRecheck(PortalAttempt inspection, bool directConfirmation = false)
    {
        var stage = directConfirmation ? "Submission" : "SubmissionRecheck";
        var next = new AuthenticationBlock { State = AuthenticationState.Ready, Revision = Guid.NewGuid() };
        bool cleared;
        try
        {
            cleared = environment.TryResolveSubmissionPending(authentication, submissionBudget, next, directConfirmation);
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            submissionResolutionStorageFault = true;
            submissionResolutionFaultRevision = authentication?.Revision;
            journal.Fault(stage, directConfirmation
                ? "门户提交响应确认成功，但消解状态写入失败；保持安全暂停。"
                : "复检确认门户已登录，但消解状态写入失败；保持安全暂停。", e);
            return false;
        }
        if (!cleared)
        {
            journal.Fault(stage, directConfirmation
                ? "门户提交响应确认成功，但认证安全状态已变化；取消消解。"
                : "复检确认门户已登录，但认证安全状态已变化；取消消解。");
            return false;
        }
        authentication = next;
        submissionResolutionStorageFault = false;
        submissionResolutionFaultRevision = null;
        submissionBudget = environment.LoadSubmissionBudget();
        activeSubmission = null;
        authenticationBlockPending = false;
        policy.CredentialsBlocked = false;
        blockFailure = PortalFailure.None;
        authenticationRetryUntil = DateTimeOffset.MinValue;
        authenticationAcceptedAt = clock.GetUtcNow();
        journal.Recovered("Authentication");
        journal.Write("SubmissionResolved", directConfirmation ? "门户提交响应确认成功；认证阻断已清除并追加解除审计。" : "复检确认门户已登录；提交结果消解为已接受，认证阻断已清除并追加解除审计。", new()
            { Stage = stage, Outcome = "Accepted", Authentication = inspection });
        return true;
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
        inspection = await new DrcomPortal(fresh, new Uri(settings.PortalOrigin), null, budgets).InspectDetailedAsync(ct);
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
        if (retryingSubmission)
            return current == authentication && current?.State == AuthenticationState.SubmissionPending || RejectSubmission("AuthenticationStateChanged");
        if (current != authentication || current?.BlocksSubmissions == true || PolicyNow < authenticationRetryUntil)
            return RejectSubmission(current != authentication ? "AuthenticationStateChanged" : current?.BlocksSubmissions == true ? "AuthenticationPaused" : "AuthenticateCooldown");
        if ((current?.Attempts ?? 0) >= 3 && submissionBudget is not { State: SubmissionBudgetState.PossiblySent, Attempts: < 3 })
        {
            WriteAuthentication(new(PortalFailure.InteractionRequired) { Attempts = 3 });
            return RejectSubmission("AuthenticationAttemptLimit");
        }
        // Reserve an attempt and a conservative retry before even the preflight. If a
        // later cooldown write fails, restarting cannot turn that failure into a new authorization.
        return WriteAuthentication(new()
        {
            State = AuthenticationState.RetryCoolingDown, Attempts = Math.Min(3, (current?.Attempts ?? 0) + 1),
            RetryDelaySeconds = 60, RetryNotBefore = clock.GetUtcNow().AddSeconds(60)
        });
    }

    // The submission review chain. `review` is the chain's own budget (ADR-0004): steps are
    // only interruptible at their boundaries, and expiry is an explicit, logged refusal —
    // never a silent authorization and never a portal timeout. `ct` is the caller's token:
    // its cancellation propagates out of the chain instead of being converted.
    private async Task<bool> AuthorizeSubmissionAsync(Settings settings, WifiLink link, CancellationToken ct, CancellationToken review)
    {
        try
        {
            review.ThrowIfCancellationRequested();
            var currentSettings = ReadStep("SettingsRead", environment.LoadSettings);
            review.ThrowIfCancellationRequested();
            var currentLink = ReadStep("WifiRead", () => environment.ReadWifi(settings));
            if (currentSettings is null || currentSettings != settings || !currentSettings.Enabled || currentLink.State != WifiState.Ready ||
                currentLink.Index != link.Index || !Equals(currentLink.Address, link.Address))
                return RejectSubmission(currentSettings != settings ? "SettingsChanged" : currentSettings?.Enabled != true ? "RecoveryDisabled" : "WifiChanged");
            using var fresh = environment.CreateDirectClient(currentLink);
            var internet = await HealthChecks.InternetDetailedAsync(fresh, review);
            if (internet.Online) return RejectSubmission("FreshInternetRecovered");
            review.ThrowIfCancellationRequested();
            var current = ReadStep("AuthenticationStateRead", environment.LoadAuthenticationBlock);
            if (current != authentication || current?.State != (retryingSubmission ? AuthenticationState.SubmissionPending : AuthenticationState.RetryCoolingDown))
                return RejectSubmission("AuthenticationStateChanged");
            submissionBudget = ReadStep("SubmissionBudgetRead", environment.LoadSubmissionBudget);
            if (retryingSubmission && submissionBudget is { Attempts: 0 } && !environment.IsConfirmedSubmissionRestart(submissionBudget))
                return RejectSubmission("SubmissionRestartNotConfirmed");
            var decision = submissionRetry.Evaluate(submissionBudget, clock.GetUtcNow(), clock.GetElapsedTime(clockStart));
            if (decision != SubmissionRetryDecision.Authorized) return RejectSubmission("SubmissionBudget" + decision);
            var reservationUtc = clock.GetUtcNow();
            var reservationTick = clock.GetTimestamp();
            var reservation = environment.TryReserveSubmissionBudget(submissionBudget, reservationUtc);
            if (reservation.Outcome != SubmissionReservationOutcome.Reserved) return RejectSubmission("SubmissionBudget" + reservation.Outcome);
            activeSubmission = submissionBudget = reservation.Budget!;
            journal.Write("SubmissionBudgetReserved", "凭据提交额度已持久预留；本次按可能发出计数。", new()
                { Stage = "Submission", Outcome = "Reserved", Reason = "SubmissionBudgetReserved", ActionId = journal.ActionId });
            if (!WriteAuthentication(new()
            {
                State = AuthenticationState.SubmissionPending, Attempts = current.Attempts
            })) return RejectSubmission("SubmissionReservationFailed");
            submissionPendingSince = PolicyNow;
            submissionRechecks = 0;
            review.ThrowIfCancellationRequested();
            currentSettings = ReadStep("SettingsRead", environment.LoadSettings);
            currentLink = ReadStep("WifiRead", () => environment.ReadWifi(settings));
            var finalAuthentication = ReadStep("AuthenticationStateRead", environment.LoadAuthenticationBlock);
            var finalBudget = ReadStep("SubmissionBudgetRead", environment.LoadSubmissionBudget);
            review.ThrowIfCancellationRequested();
            if (finalAuthentication != authentication || finalBudget != activeSubmission)
                return RejectSubmission("FinalAuthenticationOrBudgetChanged");
            if (((clock.GetUtcNow() - reservationUtc) - clock.GetElapsedTime(reservationTick)).Duration() > TimeSpan.FromSeconds(2))
                return RejectSubmission("FinalClockUntrusted");
            var allowed = currentSettings is not null && currentSettings == settings && currentSettings.Enabled && currentLink.State == WifiState.Ready &&
                currentLink.Index == link.Index && Equals(currentLink.Address, link.Address);
            if (!allowed) return RejectSubmission("FinalSettingsOrWifiChanged");
            journal.Write("SubmissionAuthorized", "提交授权已持久保存，最终复核通过；尚未表示服务器接受。", new()
                { Stage = "Submission", ActionId = journal.ActionId, Outcome = "Authorized" });
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The chain ran out of its own review budget; an explicit refusal, logged apart
            // from a portal timeout so the evidence tells the two apart.
            return RejectSubmission("ReviewChainTimeout");
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            journal.Fault("SubmissionAuthorization", "提交预留或最终复核存储不可用，停止发送。", e);
            return RejectSubmission("SubmissionStorageUnavailable");
        }
    }

    private bool RejectSubmission(string reason)
    {
        journal.Write("SubmissionNotAuthorized", "本轮未获得凭据提交授权；未因此绕过安全守卫。", new()
            { Stage = "Submission", ActionId = journal.ActionId, Outcome = "Blocked", Reason = reason });
        return false;
    }

    private void CompleteAuthentication(PortalAttempt attempt)
    {
        if (attempt.Result == LoginResult.Accepted && attempt.CredentialRequestStarted)
        {
            if (!ResolveSubmissionByRecheck(attempt, directConfirmation: true))
            {
                WriteAuthentication(new AuthenticationBlock(PortalFailure.SafetyPersistence) { Attempts = authentication?.Attempts ?? 0 });
                if (submissionResolutionStorageFault)
                    submissionResolutionFaultRevision = (authenticationBlockPending ? authenticationToWrite : authentication)?.Revision;
            }
            return;
        }
        if (attempt.Result is LoginResult.RejectedCredentials or LoginResult.RejectedBinding or LoginResult.NeedsInteraction)
        {
            // Either durable stop is sufficient. Do not lose an explicit refusal merely
            // because the independent budget file cannot be updated.
            WriteAuthentication(new(attempt.Failure) { Attempts = authentication?.Attempts ?? 0 });
            CompleteSubmissionBudget(SubmissionBudgetState.Concluded);
            return;
        }
        if (!CompleteSubmissionBudget(attempt.Result == LoginResult.Accepted ? SubmissionBudgetState.Concluded : SubmissionBudgetState.PossiblySent)) return;
        if (attempt.Failure == PortalFailure.SafetyPersistence) return;
        if (retryingSubmission && !attempt.CredentialRequestStarted)
        {
            pendingRetryAfter = PolicyNow.AddSeconds(60);
            if (attempt.Failure == PortalFailure.AlreadyAuthenticated) ResolveSubmissionByRecheck(attempt);
            return;
        }
        var attempts = authentication?.Attempts ?? 0;
        AuthenticationBlock next;
        if (attempt.Result == LoginResult.Accepted || attempt.Failure == PortalFailure.AlreadyAuthenticated)
            next = new() { State = AuthenticationState.Ready, Attempts = attempts };
        else if (attempt.CredentialRequestStarted && !attempt.RetryPermitted)
        {
            // The durable pending state survives ambiguous replies. ADR-0011 allows a
            // later attempt only through the independent budget and full review chain.
            policy.CredentialsBlocked = true;
            submissionPendingSince ??= PolicyNow;
            journal.Fault("Authentication", "认证提交结果未确认，保留持久安全暂停；稍后进行只读复检定性。");
            return;
        }
        else if (attempts >= 3 && !attempt.CredentialRequestStarted) next = new(PortalFailure.InteractionRequired)
        {
            Attempts = 3,
            ChannelFallbackCause = PortalLayoutSignal.IsStructural(attempt.Failure) ? attempt.Failure : PortalFailure.None
        };
        else if (attempt.Result == LoginResult.Unsupported) next = new(attempt.Failure) { Attempts = attempts };
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

    private bool CompleteSubmissionBudget(SubmissionBudgetState nextState)
    {
        try
        {
            if (activeSubmission is { } reserved)
            {
                if (!environment.TryTransitionSubmissionBudget(reserved, nextState))
                {
                    activeSubmission = null;
                    RejectSubmission("SubmissionBudgetStateChanged");
                    return false;
                }
                activeSubmission = null;
                submissionBudget = environment.LoadSubmissionBudget();
                submissionRetry.Evaluate(submissionBudget, clock.GetUtcNow(), clock.GetElapsedTime(clockStart));
            }
            return true;
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            activeSubmission = null;
            journal.Fault("SubmissionBudgetWrite", "提交结果的额度状态无法持久确认；保留已用额度，不提前重发。", e);
            return false;
        }
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

    // #35: controlled budget level. A missing record is only "Unused" when there is no
    // pending submission; a legacy pending record without a budget stays "Unknown" so the
    // tray never claims a fresh allowance or a reset that was not durably observed.
    private string SubmissionBudgetCode()
    {
        if (submissionBudget is null)
            return authentication?.State == AuthenticationState.SubmissionPending
                ? SubmissionBudgetCodes.Unknown : SubmissionBudgetCodes.Unused;
        if (submissionBudget.Attempts >= SubmissionRetryPolicy.AttemptCap) return SubmissionBudgetCodes.Exhausted;
        if (submissionBudget is { Attempts: 0, State: SubmissionBudgetState.Concluded }) return SubmissionBudgetCodes.Reset;
        if (submissionBudget.Attempts >= SubmissionRetryPolicy.AttemptCap - 1) return SubmissionBudgetCodes.NearLimit;
        return SubmissionBudgetCodes.Available;
    }

    // Single projection for the B5 status flags, used by both the main construction and
    // Publish so an early-return snapshot carries the same durable signal values.
    private StatusSnapshot WithFallbackFlags(StatusSnapshot status) => status with
    {
        PortalLayoutSuspected = portalLayoutSuspected,
        BrowserFallbackRequested = browserFallbackRequested,
        BrowserFallbackNotBefore = browserFallbackRequested ? browserFallbackNotBefore : null,
        AuthChannelRequested = status.AuthChannelMode == AuthChannelModes.Automatic && authChannelRequested,
        AuthChannelNotBefore = status.AuthChannelMode == AuthChannelModes.Automatic && authChannelRequested ? authChannelNotBefore : null
    };

    private StatusSnapshot Publish(StatusSnapshot status)
    {
        // Entering the safety pause is announced once per transition, whichever path set the block;
        // leaving it re-arms the announcement for the next pause.
        if (policy.CredentialsBlocked && !blockedPublished)
        {
            blockedPublished = true;
            // A more specific event raised earlier in the same cycle (portal layout change,
            // browser fallback) already tells the user authentication is paused; the generic
            // announcement must not supersede it on the one-shot channel.
            if (!notifiedThisCycle) Notify(NotificationCodes.AuthenticationPaused);
        }
        if (!policy.CredentialsBlocked) blockedPublished = false;
        status = WithFallbackFlags(status with { UpdatedAt = clock.GetUtcNow(), LastError = lastError, Notification = notification,
            DisconnectAttempts = policy.DisconnectAttempts, CredentialsBlocked = policy.CredentialsBlocked });
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

    // One-shot broadcast to the tray. The payload persists until superseded and the tray balloons
    // only when At advances, so no persistent sequence counter is needed.
    private void Notify(string code)
    {
        notification = new(code, clock.GetUtcNow());
        notifiedThisCycle = true;
    }

    /// <summary>
    /// 复检文案跟随校园 Wi-Fi 状态：服务只在链路就绪时执行只读复检，
    /// 状态详情与日志不能声称 Wi-Fi 不可用时仍在复检。
    /// </summary>
    private static string RecheckText(WifiLink link, string active, string suspended) =>
        link.State == WifiState.Ready ? active : suspended;

    private static bool IsStorageFailure(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException;

    public override void Dispose()
    {
        StopAuxiliary();
        directHttp?.Dispose();
        base.Dispose();
    }
}
