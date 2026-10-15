using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace AutoNet.Core;

public sealed record ActivityLog
{
    public int SchemaVersion { get; init; } = 1;
    public string? EventCode { get; init; }
    public string? RunId { get; init; }
    public string? IncidentId { get; init; }
    public string? NetworkId { get; init; }
    public string? BuildId { get; init; }
    public long? Sequence { get; init; }
    public long? Cycle { get; init; }
    public double? MonotonicMilliseconds { get; init; }
    public DiagnosticData? Diagnostics { get; init; }
    public DateTimeOffset Time { get; init; }
    public string Action { get; init; } = "";
    public string Detail { get; init; } = "";
    public double? Seconds { get; init; }
    public string? Wifi { get; init; }
    public string? Direct { get; init; }
    public string? Portal { get; init; }
    public string? SystemPath { get; init; }
    public string? Uu { get; init; }
    public int? Attempts { get; init; }
    public int? DisconnectAttempts { get; init; }
    public DateTimeOffset? OutageSince { get; init; }
    public DateTimeOffset? NextAttempt { get; init; }
    public DateTimeOffset? ActionStartedAt { get; init; }
    public DateTimeOffset? ActionFinishedAt { get; init; }
    public DateTimeOffset? AuthenticationAcceptedAt { get; init; }
    public DateTimeOffset? RecoveredAt { get; init; }
    public string? AuthenticationStage { get; init; }
    public string? AuthenticationFailure { get; init; }
    public string? AuthenticationPersistentState { get; init; }
    public int? AuthenticationHttpStatus { get; init; }
    public bool? CredentialRequestStarted { get; init; }
    public string? NotificationCode { get; init; }
    public string? LastError { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static ActivityLog Create(DateTimeOffset time, string action, string detail, double? seconds, StatusSnapshot? status) => new()
    {
        Time = time, Action = action, Detail = detail, Seconds = seconds, Wifi = status?.Wifi, Direct = status?.Direct,
        Portal = status?.Portal, SystemPath = status?.Tunnel, Uu = status?.Uu, Attempts = status?.Attempts, DisconnectAttempts = status?.DisconnectAttempts,
        OutageSince = status?.OutageSince, NextAttempt = status?.NextAttempt,
        ActionStartedAt = status?.ActionStartedAt, ActionFinishedAt = status?.ActionFinishedAt,
        AuthenticationAcceptedAt = status?.AuthenticationAcceptedAt, RecoveredAt = status?.LastRecoveryAt,
        AuthenticationStage = status?.AuthenticationStage, AuthenticationFailure = status?.AuthenticationFailure,
        AuthenticationPersistentState = status?.AuthenticationPersistentState,
        AuthenticationHttpStatus = status?.AuthenticationHttpStatus, CredentialRequestStarted = status?.CredentialRequestStarted,
        NotificationCode = status?.Notification?.Code,
        LastError = status?.LastError
    };

    public string ToReadableText()
    {
        var text = new StringBuilder();
        text.AppendLine($"{Time.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}  【{Action}】");
        text.AppendLine(Detail);
        if (EventCode is not null)
        {
            text.AppendLine($"事件：{EventCode}；运行 {RunId}；序号 {Sequence}；检测轮次 {Cycle}");
            if (IncidentId is not null) text.AppendLine($"故障编号：{IncidentId}；网络编号：{NetworkId}");
            if (BuildId is not null) text.AppendLine("程序标识：" + BuildId);
            if (Diagnostics is { } data)
            {
                if (data.Reason is not null) text.AppendLine("决策原因：" + data.Reason);
                if (data.Stage is not null) text.AppendLine($"执行阶段：{data.Stage}；结果：{data.Outcome ?? "尚未完成"}");
                if (data.HttpStatus is { } http) text.AppendLine("HTTP 状态：" + http);
                if (data.NativeError is { } native) text.AppendLine("Windows 原生错误：" + native);
                if (data.ActionId is not null) text.AppendLine("动作编号：" + data.ActionId);
                if (data.ElapsedMilliseconds is { } elapsed) text.AppendLine($"本段单调计时：{elapsed:0.##} ms");
                if (data.Error is { } error) text.AppendLine($"错误：{error.Type}；HRESULT {error.HResult}；HTTP 分类 {error.HttpError}；Socket {error.SocketError}；原生错误 {error.NativeError}\n调用位置：{string.Join(" → ", error.CallSites)}");
                if (data.Probes is { } probes)
                    foreach (var probe in probes)
                    {
                        text.AppendLine($"探测 {probe.Target}：{probe.Failure}；HTTP {probe.HttpStatus}；{probe.ElapsedMilliseconds:0.##} ms；{probe.Error?.HttpError} {probe.Error?.SocketError}");
                        if (probe.Error is { } probeError) text.AppendLine($"探测错误：{probeError.Type}；HRESULT {probeError.HResult}；原生错误 {probeError.NativeError}；调用位置 {string.Join(" → ", probeError.CallSites)}");
                    }
                if (data.Authentication is { } auth)
                {
                    text.AppendLine("认证步骤：" + auth.SafeSummary);
                    if (auth.Evidence is not null) text.AppendLine("参数证据：" + JsonSerializer.Serialize(auth.Evidence, Json));
                }
                if (data.LostEvents is { } lost) text.AppendLine($"日志缺口：有 {lost} 条事件未能写入；不可假设期间没有故障或动作。");
                if (data.Parameters is { } parameters) text.AppendLine("响应及参数证据：" + JsonSerializer.Serialize(parameters, Json));
                if (data.WifiState is not null) text.AppendLine($"链路观测：{data.WifiState}；本机 IPv4 就绪 {data.HasIpv4}；DNS 服务器数 {data.DnsServerCount}");
                if (data.Condition is not null) text.AppendLine("观测条件：" + data.Condition);
                if (data.Enabled is not null) text.AppendLine($"恢复已启用 {data.Enabled}；凭据已保存 {data.HasCredentials}；允许主动断线 {data.AllowActiveDisconnect}");
                if (data.AuthenticationState is not null) text.AppendLine($"认证授权：{data.AuthenticationState} / {data.AuthenticationFailure}；累计尝试 {data.AuthenticationAttempts}/{data.AuthenticationAttemptMax}（含前置检查）");
                if (data.AuthenticationRecordSource is not null) text.AppendLine("暂停记录读取分类：" + data.AuthenticationRecordSource + "（记录格式不代表已知原始故障原因）");
                if (data.DisconnectState is not null) text.AppendLine($"断线授权：{data.DisconnectState}；累计尝试 {data.DisconnectAttempts}/{data.DisconnectAttemptMax}");
                if (data.FailedRounds is not null) text.AppendLine($"连续失败采样：{data.FailedRounds}/{data.RequiredFailureRounds} 轮；观察窗口：{data.ObservationSeconds:0.##}/{data.RequiredObservationSeconds} 秒");
                if (data.NextAttempt is { } retry) text.AppendLine($"下一次允许尝试：{retry.ToLocalTime():HH:mm:ss}");
                if (data.PreviousRunId is not null) text.AppendLine($"前一运行：{data.PreviousRunId}；最后事件序号 {data.PreviousSequence}；故障 {data.PreviousIncidentId}");
            }
        }
        if (Detail.Contains("请重新保存凭据", StringComparison.Ordinal)) text.AppendLine("说明：这是旧版处理提示。请先检查认证诊断，不要仅为解除暂停而重新保存密码。");
        if (Action == "恢复联网") text.AppendLine("说明：旧版仅记录外网重新可达，未记录恢复来源，不能据此认定是程序自动修复。");
        if (Wifi is not null) text.AppendLine($"网络：Wi-Fi {Wifi}；{Direct ?? "外网状态未记录"}；认证 {Portal ?? "未记录"}");
        if (Attempts is { } attempts) text.AppendLine($"本轮自动动作：{attempts} 次");
        if (DisconnectAttempts is { } disconnects) text.AppendLine($"本次故障主动断线：{disconnects}/3 次（连续在线 60 秒重置）");
        if (OutageSince is { } outage) text.AppendLine($"首次发现故障：{outage.ToLocalTime():HH:mm:ss}");
        if (NextAttempt is { } next) text.AppendLine($"下次尝试：{next.ToLocalTime():HH:mm:ss}（期间继续检测）");
        if (ActionStartedAt is { } started) text.AppendLine($"最近动作：{started.ToLocalTime():HH:mm:ss} → {(ActionFinishedAt is { } finished ? finished.ToLocalTime().ToString("HH:mm:ss") : "进行中")}");
        if (AuthenticationAcceptedAt is { } accepted) text.AppendLine($"认证接口接受：{accepted.ToLocalTime():HH:mm:ss}（仍以外网检查为准）");
        if (Seconds is { } seconds) text.AppendLine($"观测离线时长：{seconds.ToString("0.##", CultureInfo.InvariantCulture)} 秒（从发现故障到外网重新可达）");
        if (AuthenticationStage is not null and not "Unknown") text.AppendLine($"认证诊断：{AuthenticationStage} / {AuthenticationFailure}" +
            (AuthenticationHttpStatus is { } code ? $"，HTTP {code}" : "") + (CredentialRequestStarted == true ? "；已发起凭据请求" : "；未发送凭据"));
        if (AuthenticationPersistentState is not null and not "Unknown") text.AppendLine("持久认证状态：" + (AuthenticationPersistentState switch
        {
            "Ready" => "就绪", "SubmissionPending" => "提交结果待确认，暂停自动提交", "RetryCoolingDown" => "持久退避中",
            "BindingRejected" => "设备绑定拒绝", "CredentialsRejected" => "凭据被拒绝", "InteractionRequired" => "需要人工确认",
            _ => "安全暂停"
        }));
        if (NotificationCode is { } notify && NotificationCodes.IsKnown(notify)) text.AppendLine("通知事件：" + notify);
        if (SystemPath is not null) text.AppendLine("系统路径：" + SystemPath);
        if (Uu is not null) text.AppendLine("UU：" + Uu);
        if (!string.IsNullOrEmpty(LastError)) text.AppendLine("最近异常：" + LastError);
        return text.ToString();
    }

    public static string ReadableHistory(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(300).Reverse();
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<ActivityLog>(line, Json);
                if (entry is null || entry.Action == "" || entry.Time == default) throw new JsonException();
                text.AppendLine(entry.ToReadableText());
            }
            catch (JsonException) { text.AppendLine("【记录不完整】一条日志暂时无法读取；可刷新重试。\n"); }
        }
        return text.Length == 0 ? "这一天尚无日志。后台服务运行后会记录状态变化和恢复动作。" : text.ToString();
    }
}
