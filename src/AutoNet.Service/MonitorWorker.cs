using System.Text.Json;
using AutoNet.Core;
using Microsoft.Extensions.Hosting;

namespace AutoNet.Service;

public sealed class MonitorWorker(IMonitorEnvironment environment, TimeProvider clock) : BackgroundService
{
    private readonly RecoveryPolicy policy = new();
    private readonly DateTimeOffset clockOrigin = clock.GetUtcNow();
    private readonly long clockStart = clock.GetTimestamp();
    private string fingerprint = "";
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
    private string lastError = "";
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
        catch (Exception) { Environment.Exit(1); }
        finally { StopAuxiliary(); directHttp?.Dispose(); UpdatePower(false); }
    }

    // A complete cycle, with only external Windows/HTTP/file boundaries replaced in offline fixtures.
    public async Task<StatusSnapshot> RunOnceAsync(CancellationToken ct)
    {
        try { return await TickAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            UpdatePower(false);
            lastError = "主检测异常：" + e.GetType().Name;
            return Publish(new StatusSnapshot { State = "检测异常", Detail = "检测将自动重试；" + lastError });
        }
    }

    private async Task<StatusSnapshot> TickAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = environment.LoadSettings();
        if (settings is null)
        {
            UpdatePower(false);
            CancelAuxiliary();
            return Publish(new StatusSnapshot { State = "等待设置", Detail = "请在托盘窗口中设置校园 Wi-Fi 和认证信息。" });
        }
        settings.Validate();
        var newFingerprint = environment.CredentialFingerprint();
        if (newFingerprint != fingerprint)
        {
            policy.CredentialsBlocked = environment.AuthenticationBlocked(newFingerprint);
            authenticationBlockPending = false;
            fingerprint = newFingerprint;
        }
        if (authenticationBlockPending && policy.CredentialsBlocked) PersistAuthenticationBlock();
        var credentials = environment.LoadCredentials();
        UpdatePower(settings.Enabled && settings.KeepAwakeOnAc);
        var link = environment.ReadWifi(settings);
        if (link.OtherProfile)
        {
            policy.Reset();
            outageObservedAt = null;
            CancelAuxiliary();
            return Publish(new StatusSnapshot { Enabled = settings.Enabled, State = "已切换其他 Wi-Fi", Wifi = "其他配置", Detail = "保留你当前选择的网络，等待校园 Wi-Fi。" });
        }
        var direct = false;
        var portalState = PortalState.Unknown;
        if (link.State == WifiState.Ready)
        {
            var http = DirectClient(link);
            direct = await HealthChecks.InternetAsync(http, ct);
            if (!direct)
            {
                try { portalState = await new DrcomPortal(http, new Uri(settings.PortalOrigin)).InspectAsync(ct); }
                catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException) { ct.ThrowIfCancellationRequested(); }
            }
        }
        else { directHttp?.Dispose(); directHttp = null; directKey = ""; }
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
            Log("恢复联网", "从首次观测故障到 HTTPS 恢复的耗时", lastRecoverySeconds);
        }
        var observation = new Observation(link.State, direct, portalState);
        var action = policy.Decide(observation, PolicyNow, settings.Enabled, credentials is not null);
        if (policy.OutageSince is null) outageObservedAt = null;
        else outageObservedAt ??= clock.GetUtcNow();
        var detail = direct ? "指定 Wi-Fi 绑定的 HTTPS 内容检查通过；物理出口需抓包核验。" : observation.Condition switch
        {
            NetworkCondition.AuthenticatedOffline => "校园网仍显示已认证，外网不可达；继续检测，不重复提交密码。",
            NetworkCondition.NeedsAuthentication => credentials is null ? "需要认证，请在设置中保存账号密码。" : "校园网认证已失效。",
            NetworkCondition.WaitingForAddress => "Wi-Fi 已关联，等待 IPv4/DHCP；不反复重连。",
            NetworkCondition.Associating => "Wi-Fi 正在关联或链路认证，等待完成。",
            NetworkCondition.InterfaceUnavailable => "选定 Wi-Fi 网卡尚未就绪，等待恢复。",
            NetworkCondition.WifiDisconnected => "等待校园 Wi-Fi 连接。",
            _ => "外网和认证状态暂不可确认；持续检测，不提交密码。"
        };
        if (action != ActionKind.None)
        {
            ct.ThrowIfCancellationRequested();
            actionStartedAt = clock.GetUtcNow();
            if (action == ActionKind.ReconnectWifi)
            {
                try { environment.ConnectWifi(settings); detail = "已请求连接保存的校园 Wi-Fi。"; }
                catch (System.ComponentModel.Win32Exception) { detail = "Wi-Fi 连接请求失败，将退避重试。"; }
            }
            else
            {
                try
                {
                    lastAuthentication = await new DrcomPortal(DirectClient(link), new Uri(settings.PortalOrigin)).LoginDetailedAsync(credentials!, ct);
                    var result = lastAuthentication.Result;
                    detail = lastAuthentication.SafeSummary;
                    if (lastAuthentication.Failure != PortalFailure.None) lastError = detail;
                    if (result == LoginResult.Accepted) authenticationAcceptedAt = clock.GetUtcNow();
                    if (result is LoginResult.RejectedCredentials or LoginResult.NeedsInteraction or LoginResult.Unsupported)
                    {
                        policy.CredentialsBlocked = true;
                        PersistAuthenticationBlock();
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { detail = "认证请求超时，将退避重试。"; }
                catch (Exception e) when (e is HttpRequestException or IOException or JsonException) { detail = "认证响应无法确认，将退避重试。"; }
            }
            actionFinishedAt = clock.GetUtcNow();
            policy.Attempted(PolicyNow);
            Log(action == ActionKind.ReconnectWifi ? "重连 Wi-Fi" : "重新认证", detail);
        }
        if (policy.CredentialsBlocked) detail = lastAuthentication is null
            ? "自动认证因历史安全标记暂停，原失败阶段未知；请先运行只读认证诊断，勿仅为解锁而重存密码。"
            : "自动认证已暂停；" + lastAuthentication.SafeSummary;
        var state = !settings.Enabled ? "已暂停恢复" : direct ? "校园网在线" : policy.CredentialsBlocked ? "需要处理认证" : observation.Condition switch
        {
            NetworkCondition.WaitingForAddress => "等待网络地址",
            NetworkCondition.Associating => "Wi-Fi 正在连接",
            NetworkCondition.AuthenticatedOffline => "已认证但外网不可达",
            NetworkCondition.PortalUnknown => "认证状态未知",
            NetworkCondition.InterfaceUnavailable => "网卡未就绪",
            _ => "正在恢复"
        };
        return Publish(new StatusSnapshot
        {
            Enabled = settings.Enabled, State = state, Detail = detail,
            Wifi = link.State switch { WifiState.Ready => "已连接", WifiState.WaitingForAddress => "已关联，等待 IPv4", WifiState.Associating => "正在连接", WifiState.Unavailable => "网卡未就绪", _ => "未连接" },
            Direct = direct ? "外网可达" : "外网不可达或未知",
            Portal = direct ? "未查询（外网检查已通过）" : portalState switch { PortalState.LoggedIn => "已认证", PortalState.NeedsLogin => "需认证", _ => "未知，不自动认证" },
            Tunnel = systemPath, Uu = uu, Attempts = policy.Attempts, OutageSince = outageObservedAt,
            NextAttempt = policy.NextAttempt > PolicyNow ? clock.GetUtcNow() + (policy.NextAttempt - PolicyNow) : null,
            LastRecoverySeconds = lastRecoverySeconds, LastRecoveryAt = lastRecoveryAt,
            ActionStartedAt = actionStartedAt, ActionFinishedAt = actionFinishedAt, AuthenticationAcceptedAt = authenticationAcceptedAt,
            AuthenticationStage = lastAuthentication?.Stage.ToString() ?? "Unknown", AuthenticationFailure = lastAuthentication?.Failure.ToString() ?? "None",
            AuthenticationHttpStatus = lastAuthentication?.HttpStatus, CredentialRequestStarted = lastAuthentication?.CredentialRequestStarted ?? false,
            CredentialsBlocked = policy.CredentialsBlocked
        });
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
                if (!discardAuxiliary) (systemPath, uu) = result;
            }
            catch (Exception e)
            {
                if (!discardAuxiliary)
                {
                    systemPath = "系统路径未知，辅助检测失败";
                    uu = "UU 状态未知，稍后重试";
                    lastError = "辅助检测异常：" + e.GetType().Name;
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
        catch (Exception e) { lastError = "辅助检测取消异常：" + e.GetType().Name; }
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
        try { environment.UpdatePower(enabled); }
        catch (Exception e) { lastError = "电源请求异常：" + e.GetType().Name; }
    }

    private void PersistAuthenticationBlock()
    {
        try { environment.BlockAuthentication(fingerprint); authenticationBlockPending = false; }
        catch (Exception e) when (IsStorageFailure(e))
        {
            authenticationBlockPending = true;
            lastError = "认证暂停标记写入失败，将重试；重启后可能再次检查：" + e.GetType().Name;
        }
    }

    private StatusSnapshot Publish(StatusSnapshot status)
    {
        status = status with { UpdatedAt = clock.GetUtcNow(), LastError = lastError };
        var key = status.State + "/" + status.Detail + "/" + status.Tunnel + "/" + status.Uu;
        if (key != lastEvent && Log(status.State, status.Detail, snapshot: status)) lastEvent = key;
        status = status with { LastError = lastError };
        try { environment.WriteStatus(status); }
        catch (Exception e) when (IsStorageFailure(e)) { lastError = "状态写入失败：" + e.GetType().Name; }
        return status with { LastError = lastError };
    }

    private bool Log(string action, string detail, double? seconds = null, StatusSnapshot? snapshot = null)
    {
        try { environment.WriteLog(action, detail, seconds, snapshot); return true; }
        catch (Exception e) when (IsStorageFailure(e)) { lastError = "日志写入失败：" + e.GetType().Name; return false; }
    }

    private static bool IsStorageFailure(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    public override void Dispose()
    {
        StopAuxiliary();
        directHttp?.Dispose();
        base.Dispose();
    }
}
