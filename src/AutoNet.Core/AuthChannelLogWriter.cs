using System.Text.Json;

namespace AutoNet.Core;

/// <summary>
/// 内嵌认证通道的结构化日志写入（工单 #25 / ADR-0009）：通道进程把发起、可见化、
/// 结论三事件按服务同款 schema（ActivityLog v2）追加到既有日志目录——日志页与诊断包
/// 自动纳入，词表分类已在 LogEventClassifier 注册（Authentication）。追加为单行小写，
/// 与服务的读取共享（FileShare.ReadWrite）兼容；写入失败静默（日志不反噬通道结论）。
/// </summary>
public static class AuthChannelLogWriter
{
    public static void Append(string eventCode, string detail, string? directory = null)
    {
        try
        {
            var logsDir = directory ?? Storage.Logs;
            Directory.CreateDirectory(logsDir);
            var entry = new ActivityLog
            {
                Time = DateTimeOffset.Now,
                EventCode = eventCode,
                Action = detail,
                SchemaVersion = 2,
                RunId = Guid.NewGuid().ToString("N")[..12],
            };
            var path = Path.Combine(logsDir, $"autonet-{entry.Time.ToLocalTime():yyyy-MM-dd}.jsonl");
            File.AppendAllText(path, JsonSerializer.Serialize(entry, ActivityLog.Json) + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
