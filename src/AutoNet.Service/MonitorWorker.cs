using System.Diagnostics;
using System.Text.Json;
using AutoNet.Core;
using Microsoft.Extensions.Hosting;

namespace AutoNet.Service;

internal sealed class MonitorWorker : BackgroundService
{
    private readonly RecoveryPolicy policy = new();
    private readonly PowerGuard power = new();
    private string fingerprint = "";
    private string lastEvent = "";
    private DateTimeOffset lastAuxiliary = DateTimeOffset.MinValue;
    private Task<(string SystemPath, string Uu)>? auxiliaryTask;
    private string systemPath = "待检测";
    private string uu = "待检测";
    private double? lastRecoverySeconds;
    private HttpClient? directHttp;
    private string directKey = "";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var started = Stopwatch.StartNew();
                try { await TickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    power.Update(false);
                    // Log only the exception TYPE; exception messages can contain URLs/passwords.
                    var reason = e is System.ComponentModel.Win32Exception native
                        ? "系统接口异常，错误码 " + native.NativeErrorCode
                        : "检测将自动重试：" + e.GetType().Name;
                    Publish(new StatusSnapshot { State = "检测异常", Detail = reason });
                }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(200, 2000 - started.Elapsed.TotalMilliseconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception) { Environment.Exit(1); } // SCM must see a failure to restart an unrecoverable worker.
        finally { directHttp?.Dispose(); power.Dispose(); }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var settings = Storage.LoadSettings();
        if (settings is null)
        {
            power.Update(false);
            Publish(new StatusSnapshot { State = "等待设置", Detail = "请在托盘窗口中设置校园 Wi-Fi 和认证信息。" });
            return;
        }
        settings.Validate();
        var newFingerprint = Storage.CredentialFingerprint();
        if (newFingerprint != fingerprint)
        {
            fingerprint = newFingerprint;
            policy.CredentialsBlocked = File.Exists(Storage.AuthBlockPath) && Storage.ReadShared(Storage.AuthBlockPath).Trim() == fingerprint;
        }
        var credentials = Storage.LoadCredentials();
        power.Update(settings.Enabled && settings.KeepAwakeOnAc);
        var link = Wifi.Read(settings);
        if (link.OtherProfile)
        {
            policy.Reset();
            Publish(new StatusSnapshot { Enabled = settings.Enabled, State = "已切换其他 Wi-Fi", Wifi = "其他配置", Detail = "保留你当前选择的网络，等待校园 Wi-Fi。" });
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var direct = false;
        var portalState = PortalState.Unknown;
        if (link.Connected && link.Address is not null)
        {
            var http = DirectClient(link);
            direct = await HealthChecks.InternetAsync(http, ct);
            if (!direct)
            {
                try { portalState = await new DrcomPortal(http, new Uri(settings.PortalOrigin)).InspectAsync(ct); }
                catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException) { }
            }
        }
        else { directHttp?.Dispose(); directHttp = null; directKey = ""; }
        // Auxiliary checks never block the Wi-Fi/authentication recovery loop.
        if (auxiliaryTask is { IsCompletedSuccessfully: true })
        {
            (systemPath, uu) = auxiliaryTask.Result;
            auxiliaryTask = null;
        }
        if (auxiliaryTask is null && now - lastAuxiliary >= TimeSpan.FromSeconds(20))
        { lastAuxiliary = now; auxiliaryTask = CheckAuxiliaryAsync(settings, link, ct); }
        if (direct && policy.OutageSince is { } since)
        {
            // Recovery clock starts at the first observed failure; physical outage can precede it by a polling interval.
            lastRecoverySeconds = Math.Round((DateTimeOffset.UtcNow - since).TotalSeconds, 2);
            Log("恢复联网", lastRecoverySeconds <= 15 ? "观测恢复耗时在 15 秒内" : "观测恢复耗时超过 15 秒", lastRecoverySeconds);
        }
        var action = policy.Decide(new(link.Connected && link.Address is not null, direct, portalState == PortalState.NeedsLogin), now, settings.Enabled, credentials is not null);
        var detail = direct ? "校园网直连已通过 HTTPS 内容检查。" : portalState switch
        {
            PortalState.LoggedIn => "校园网仍显示已认证，外网不可达；继续检测，不重复提交密码。",
            PortalState.NeedsLogin => credentials is null ? "需要认证，请在设置中保存账号密码。" : "校园网认证已失效。",
            _ => link.Connected ? "外网和认证状态暂不可确认，持续检测。" : "等待校园 Wi-Fi 连接。"
        };
        if (action == ActionKind.ReconnectWifi)
        {
            try { Wifi.Connect(settings); detail = "已请求连接保存的校园 Wi-Fi。"; }
            catch (System.ComponentModel.Win32Exception) { detail = "Wi-Fi 连接请求失败，将退避重试。"; }
            policy.Attempted(DateTimeOffset.UtcNow);
            Log("重连 Wi-Fi", detail);
        }
        else if (action == ActionKind.Authenticate)
        {
            try
            {
                var http = DirectClient(link);
                using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                loginTimeout.CancelAfter(TimeSpan.FromSeconds(6));
                var result = await new DrcomPortal(http, new Uri(settings.PortalOrigin)).LoginAsync(credentials!, loginTimeout.Token);
                detail = result switch
                {
                    LoginResult.Accepted => "认证接口已接受，正在验证实际联网。",
                    LoginResult.RejectedCredentials => "凭据错误：请更新校园网账号密码。",
                    LoginResult.NeedsInteraction => "认证页面要求人工验证，请检查登录页面。",
                    LoginResult.Unsupported => "认证页面参数发生变化，需要重新适配。",
                    _ => "认证未成功，将退避重试。"
                };
                if (result is LoginResult.RejectedCredentials or LoginResult.NeedsInteraction or LoginResult.Unsupported)
                {
                    policy.CredentialsBlocked = true;
                    Storage.AtomicWrite(Storage.AuthBlockPath, fingerprint);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { detail = "认证请求超时，将退避重试。"; }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException) { detail = "认证响应无法确认，将退避重试。"; }
            policy.Attempted(DateTimeOffset.UtcNow);
            Log("重新认证", detail);
        }
        if (policy.CredentialsBlocked) detail = "自动认证已暂停，请重新保存凭据并检查认证页面；网络检测仍在运行。";
        var state = !settings.Enabled ? "已暂停恢复" : direct ? "校园网在线" : policy.CredentialsBlocked ? "需要处理认证" : "正在恢复";
        Publish(new StatusSnapshot
        {
            Enabled = settings.Enabled, State = state, Detail = detail, Wifi = link.Connected ? "已连接" : "未连接",
            Direct = direct ? "外网可达" : "外网不可达", Tunnel = systemPath, Uu = uu,
            Attempts = policy.Attempts, OutageSince = policy.OutageSince,
            NextAttempt = policy.NextAttempt > DateTimeOffset.UtcNow ? policy.NextAttempt : null,
            LastRecoverySeconds = lastRecoverySeconds, CredentialsBlocked = policy.CredentialsBlocked
        });
    }

    private HttpClient DirectClient(WifiLink link)
    {
        var key = $"{link.Index}/{link.Address}/{string.Join(',', link.DnsServers.Select(a => a.ToString()))}";
        if (directHttp is null || key != directKey)
        {
            directHttp?.Dispose();
            directHttp = DirectNetwork.CreateClient(link);
            directKey = key;
        }
        return directHttp;
    }

    private static async Task<(string, string)> CheckAuxiliaryAsync(Settings settings, WifiLink link, CancellationToken ct)
    {
        var uuStatus = HealthChecks.UuService();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(3000);
            var path = await HealthChecks.SystemPathAsync(settings, link, timeout.Token);
            if (settings.UuEndpoint != "")
            {
                using var system = HealthChecks.SystemClient(settings.ProxyUrl);
                uuStatus += await HealthChecks.ProbeAsync(system, new Uri(settings.UuEndpoint), false, timeout.Token)
                    ? "；指定服务可达" : "；指定服务不可达";
            }
            return (path, uuStatus);
        }
        catch (OperationCanceledException) { return ("系统路径检测超时", uuStatus); }
        catch (Exception) { return ("系统路径状态未知", uuStatus); }
    }

    private void Publish(StatusSnapshot status)
    {
        Storage.AtomicWrite(Storage.StatusPath, JsonSerializer.Serialize(status, Storage.Json));
        var key = status.State + "/" + status.Detail + "/" + status.Tunnel + "/" + status.Uu;
        if (key != lastEvent) { lastEvent = key; Log(status.State, status.Detail, snapshot: status); }
    }

    private static void Log(string action, string detail, double? seconds = null, StatusSnapshot? snapshot = null)
    {
        Directory.CreateDirectory(Storage.Logs);
        var now = DateTimeOffset.Now;
        var path = Path.Combine(Storage.Logs, $"autonet-{now:yyyy-MM-dd}.jsonl");
        File.AppendAllText(path, JsonSerializer.Serialize(new { time = now, action, detail, seconds,
            wifi = snapshot?.Wifi, direct = snapshot?.Direct, systemPath = snapshot?.Tunnel, uu = snapshot?.Uu }) + Environment.NewLine);
        foreach (var old in Directory.EnumerateFiles(Storage.Logs, "autonet-????-??-??.jsonl"))
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-14)) File.Delete(old);
    }
}
