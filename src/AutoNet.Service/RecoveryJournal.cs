using System.Reflection;
using AutoNet.Core;

namespace AutoNet.Service;

// Correlation, bounded sampling and logging failures live outside the recovery policy.
internal sealed class RecoveryJournal(IMonitorEnvironment environment, TimeProvider clock)
{
    private readonly string runId = Guid.NewGuid().ToString("N");
    private readonly long origin = clock.GetTimestamp();
    private readonly string buildId = typeof(MonitorWorker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        + "/" + typeof(MonitorWorker).Assembly.ManifestModule.ModuleVersionId + "/" + typeof(ActivityLog).Assembly.ManifestModule.ModuleVersionId;
    private readonly Dictionary<string, (string Summary, string Signature)> faults = [];
    private ActivityLog? previous;
    private string? networkId;
    private string? incidentId;
    private long incidentStarted;
    private long sequence;
    private long cycle;
    private bool started;
    private string lastDecision = "";
    private long lastDecisionAt;
    private int lostEvents;
    private long? lastFallback;

    public string Stage { get; set; } = "Starting";
    public string? ActionId { get; set; }
    public string LastError => string.Join("；", faults.Values.Select(f => f.Summary));
    public string ErrorExcept(string scope) => string.Join("；", faults.Where(f => f.Key != scope).Select(f => f.Value.Summary));
    public string? IncidentId => incidentId;

    public void BeginCycle()
    {
        cycle++;
        ActionId = null;
        if (started) return;
        started = true;
        try { previous = environment.ReadLastDiagnostic(); }
        catch (Exception e) { Fault("PreviousLogRead", "上次诊断日志读取失败", e); }
        Write("SessionStarted", "监测进程开始；未结束的动作不能据此认定成功。", new()
        {
            PreviousRunId = previous?.RunId, PreviousIncidentId = previous?.IncidentId, PreviousSequence = previous?.Sequence,
            Reason = previous is null ? "PreviousObservationUnavailable" : "ObservationInterrupted"
        });
    }

    public void SetNetwork(string key)
    {
        if (networkId == key) return;
        EndIncident("IncidentSuspended", "目标网络配置变化，结束本段观测；未确认恢复。", "TargetChanged");
        networkId = key;
        if (previous?.NetworkId == key && previous.IncidentId is not null &&
            previous.EventCode is not ("IncidentRecovered" or "IncidentSuspended"))
        {
            incidentId = previous.IncidentId;
            incidentStarted = clock.GetTimestamp();
            Write("IncidentResumed", "关联上次进程未结束的故障；进程间空白不计作已观测耗时。", new()
            {
                PreviousRunId = previous.RunId, PreviousSequence = previous.Sequence, Reason = "ObservationInterrupted"
            });
        }
        previous = null;
    }

    public void Observe(bool online, DiagnosticData data)
    {
        if (!online && incidentId is null)
        {
            incidentId = Guid.NewGuid().ToString("N");
            incidentStarted = clock.GetTimestamp();
            lastDecision = "";
            Write("IncidentStarted", "首次观测到目标网络外网未通过；不等同于物理断线时间。", data);
        }
        if (online) EndIncident("IncidentRecovered", "外网内容检查通过；恢复来源未确认。", "RecoverySourceUnconfirmed", data);
    }

    public void Suspend(string reason) => EndIncident("IncidentSuspended", "目标网络观测暂停；未确认恢复。", reason);

    private void EndIncident(string code, string detail, string reason, DiagnosticData? data = null)
    {
        if (incidentId is null) return;
        Write(code, detail, (data ?? new()) with { Reason = reason, ElapsedMilliseconds = clock.GetElapsedTime(incidentStarted).TotalMilliseconds });
        incidentId = null;
        lastDecision = "";
    }

    public void Decision(DiagnosticData data, StatusSnapshot snapshot)
    {
        // Ignore timing fluctuations; state changes and a periodic sample retain useful evidence.
        var key = $"{data.Stage}/{data.Error?.Type}/{data.Error?.NativeError}/{data.Condition}/{data.WifiState}/{data.Reason}/{data.Outcome}/{data.Enabled}/{data.HasCredentials}/{data.AllowActiveDisconnect}/{data.AuthenticationState}/{data.AuthenticationRecordSource}/{data.AuthenticationFailure}/{data.AuthenticationAttempts}/{data.DisconnectAttempts}/{data.DisconnectState}/" +
            string.Join(';', data.Probes?.Select(p => $"{p.Target}/{p.Failure}/{p.HttpStatus}/{p.Error?.SocketError}/{p.Error?.NativeError}") ?? []) +
            $"/{data.Authentication?.Stage}/{data.Authentication?.Failure}/{data.Authentication?.HttpStatus}/{LastError}";
        var interval = incidentId is null && data.Error is null ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30);
        if (key == lastDecision && clock.GetElapsedTime(lastDecisionAt) < interval) return;
        lastDecision = key;
        lastDecisionAt = clock.GetTimestamp();
        Write("RecoveryDecision", data.Outcome == "None" ? "本轮未执行恢复动作；见决策条件与阻止原因。" : "本轮决定执行恢复动作。", data, snapshot);
    }

    public void Fault(string scope, string summary, Exception? error = null)
    {
        var safe = summary + (error is null ? "" : "：" + error.GetType().Name);
        var diagnostic = error is null ? null : DiagnosticError.From(error);
        var signature = $"{safe}/{diagnostic?.HResult}/{diagnostic?.HttpError}/{diagnostic?.SocketError}/{diagnostic?.NativeError}";
        if (faults.GetValueOrDefault(scope).Signature == signature) return;
        faults[scope] = (safe, signature);
        Write("OperationFailed", safe, new() { Stage = scope, Outcome = "Failed", Error = diagnostic, ActionId = ActionId });
    }

    public void Recovered(string scope)
    {
        if (!faults.Remove(scope)) return;
        Write("OperationRecovered", "该操作本轮成功，先前异常不再作为当前错误。", new() { Stage = scope, Outcome = "Recovered" });
    }

    public bool Write(string code, string detail, DiagnosticData? data = null, StatusSnapshot? snapshot = null, string? displayAction = null, double? seconds = null)
    {
        if (lostEvents > 0)
        {
            var gap = Entry("LoggingGap", "日志恢复写入；此前事件未写入，存在明确证据缺口。", new() { LostEvents = lostEvents }, null);
            try { environment.WriteDiagnostic(gap); lostEvents = 0; faults.Remove("LogWrite"); }
            catch (Exception e) { WriteFailed(e); }
        }
        var entry = Entry(code, detail, data, snapshot) with { Action = displayAction ?? DisplayAction(code), Seconds = seconds };
        try { environment.WriteDiagnostic(entry); }
        catch (Exception e) { lostEvents++; WriteFailed(e); }
        return lostEvents == 0;
    }

    private ActivityLog Entry(string code, string detail, DiagnosticData? data, StatusSnapshot? snapshot) =>
        ActivityLog.Create(clock.GetUtcNow(), DisplayAction(code), detail, null, snapshot) with
        {
            SchemaVersion = 2, EventCode = code, RunId = runId, IncidentId = incidentId, NetworkId = networkId,
            BuildId = buildId, Sequence = ++sequence, Cycle = cycle,
            MonotonicMilliseconds = Math.Round(clock.GetElapsedTime(origin).TotalMilliseconds, 2), Diagnostics = data,
            AuthenticationStage = data?.Authentication?.Stage.ToString() ?? snapshot?.AuthenticationStage,
            AuthenticationFailure = data?.Authentication?.Failure.ToString() ?? snapshot?.AuthenticationFailure,
            AuthenticationHttpStatus = data?.Authentication?.HttpStatus ?? snapshot?.AuthenticationHttpStatus,
            AuthenticationPersistentState = data?.AuthenticationState ?? snapshot?.AuthenticationPersistentState,
            CredentialRequestStarted = data?.Authentication?.CredentialRequestStarted ?? snapshot?.CredentialRequestStarted
        };

    private static string DisplayAction(string code) => code switch
    {
        "SessionStarted" => "服务观测开始", "SessionStopped" => "服务观测停止", "IncidentStarted" => "发现网络异常",
        "IncidentRecovered" => "外网恢复验证通过", "IncidentResumed" => "继续追踪历史故障", "IncidentSuspended" => "故障观测暂停",
        "RecoveryDecision" => "恢复决策", "RecoveryActionStarted" => "恢复动作开始", "RecoveryActionCompleted" => "恢复动作结束",
        "PortalStep" => "认证步骤", "WifiRequestResult" => "Wi-Fi 请求结果", "SafetyStateWriteStarted" => "安全状态写入开始",
        "SafetyStateWritten" => "安全状态写入完成", "OperationFailed" => "操作异常", "OperationRecovered" => "操作恢复正常",
        "CycleInterrupted" => "检测轮次中断", "LoggingGap" => "日志证据缺口",
        "SubmissionAuthorized" => "认证提交获准", "SubmissionNotAuthorized" => "认证提交被阻止",
        "SubmissionRechecking" => "提交结果复检", "SubmissionResolved" => "提交结果消解", "SubmissionUnresolved" => "提交结果待人工",
        "PortalLayoutChanged" => "门户疑似改版", "BrowserFallbackRequested" => "浏览器兜底请求",
        "DisconnectRecheck" => "断线前重新核验", _ => "诊断事件"
    };

    private void WriteFailed(Exception error)
    {
        faults["LogWrite"] = ("日志写入失败：" + error.GetType().Name, error.HResult.ToString());
        if (lastFallback is { } last && clock.GetElapsedTime(last) < TimeSpan.FromMinutes(1)) return;
        lastFallback = clock.GetTimestamp();
        try
        {
            environment.WriteDiagnosticFallback($"AutoNet diagnostic log unavailable; run={runId}; incident={incidentId}; sequence={sequence}; lost={lostEvents}; error={error.GetType().Name}; hresult={error.HResult}");
            faults.Remove("LogFallback");
        }
        catch (Exception e) { faults["LogFallback"] = ("独立日志告警失败：" + e.GetType().Name, e.HResult.ToString()); }
    }
}
