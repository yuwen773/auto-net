using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace AutoNet.Core;

public sealed record ActivityLog
{
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
    public DateTimeOffset? OutageSince { get; init; }
    public DateTimeOffset? NextAttempt { get; init; }
    public DateTimeOffset? ActionStartedAt { get; init; }
    public DateTimeOffset? ActionFinishedAt { get; init; }
    public DateTimeOffset? AuthenticationAcceptedAt { get; init; }
    public DateTimeOffset? RecoveredAt { get; init; }
    public string? AuthenticationStage { get; init; }
    public string? AuthenticationFailure { get; init; }
    public int? AuthenticationHttpStatus { get; init; }
    public bool? CredentialRequestStarted { get; init; }
    public string? LastError { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static ActivityLog Create(DateTimeOffset time, string action, string detail, double? seconds, StatusSnapshot? status) => new()
    {
        Time = time, Action = action, Detail = detail, Seconds = seconds, Wifi = status?.Wifi, Direct = status?.Direct,
        Portal = status?.Portal, SystemPath = status?.Tunnel, Uu = status?.Uu, Attempts = status?.Attempts,
        OutageSince = status?.OutageSince, NextAttempt = status?.NextAttempt,
        ActionStartedAt = status?.ActionStartedAt, ActionFinishedAt = status?.ActionFinishedAt,
        AuthenticationAcceptedAt = status?.AuthenticationAcceptedAt, RecoveredAt = status?.LastRecoveryAt,
        AuthenticationStage = status?.AuthenticationStage, AuthenticationFailure = status?.AuthenticationFailure,
        AuthenticationHttpStatus = status?.AuthenticationHttpStatus, CredentialRequestStarted = status?.CredentialRequestStarted,
        LastError = status?.LastError
    };

    public string ToReadableText()
    {
        var text = new StringBuilder();
        text.AppendLine($"{Time.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}  【{Action}】");
        text.AppendLine(Detail);
        if (Detail.Contains("请重新保存凭据", StringComparison.Ordinal)) text.AppendLine("说明：这是旧版处理提示。请先检查认证诊断，不要仅为解除暂停而重新保存密码。");
        if (Action == "恢复联网") text.AppendLine("说明：旧版仅记录外网重新可达，未记录恢复来源，不能据此认定是程序自动修复。");
        if (Wifi is not null) text.AppendLine($"网络：Wi-Fi {Wifi}；{Direct ?? "外网状态未记录"}；认证 {Portal ?? "未记录"}");
        if (Attempts is { } attempts) text.AppendLine($"本轮自动动作：{attempts} 次");
        if (OutageSince is { } outage) text.AppendLine($"首次发现故障：{outage.ToLocalTime():HH:mm:ss}");
        if (NextAttempt is { } next) text.AppendLine($"下次尝试：{next.ToLocalTime():HH:mm:ss}（期间继续检测）");
        if (ActionStartedAt is { } started) text.AppendLine($"最近动作：{started.ToLocalTime():HH:mm:ss} → {(ActionFinishedAt is { } finished ? finished.ToLocalTime().ToString("HH:mm:ss") : "进行中")}");
        if (AuthenticationAcceptedAt is { } accepted) text.AppendLine($"认证接口接受：{accepted.ToLocalTime():HH:mm:ss}（仍以外网检查为准）");
        if (Seconds is { } seconds) text.AppendLine($"观测离线时长：{seconds.ToString("0.##", CultureInfo.InvariantCulture)} 秒（从发现故障到外网重新可达）");
        if (AuthenticationStage is not null and not "Unknown") text.AppendLine($"认证诊断：{AuthenticationStage} / {AuthenticationFailure}" +
            (AuthenticationHttpStatus is { } code ? $"，HTTP {code}" : "") + (CredentialRequestStarted == true ? "；已发起凭据请求" : "；未发送凭据"));
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
