using System.Text.Json;
using AutoNet.Core;

internal static class LogRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("日志：新记录保留中文和动作、下次重试、失败信息", () =>
        {
            var now = new DateTimeOffset(2026, 10, 10, 16, 39, 0, TimeSpan.FromHours(8));
            var entry = ActivityLog.Create(now, "重连校园 Wi-Fi", "持续离线，准备重新连接。", null,
                new StatusSnapshot { Wifi = "已连接", Direct = "外网不可达", Portal = "未知", Attempts = 1,
                    OutageSince = now.AddSeconds(-10), NextAttempt = now.AddSeconds(60), ActionStartedAt = now,
                    ActionFinishedAt = now.AddSeconds(1), LastError = "认证查询超时" });
            var json = JsonSerializer.Serialize(entry, ActivityLog.Json);
            Require(json.Contains("重连校园") && !json.Contains("\\u91CD") && !json.Contains('\n'));
            var text = ActivityLog.ReadableHistory(json);
            Require(text.Contains("本轮自动动作：1 次") && text.Contains("下次尝试") && text.Contains("认证查询超时") && text.Contains(now.ToLocalTime().ToString("HH:mm:ss")));
            return Task.CompletedTask;
        }),
        ("日志：旧版转义记录可读且不误报为自动修复", () =>
        {
            const string old = "{\"time\":\"2026-10-10T16:43:39+08:00\",\"action\":\"\\u6062\\u590d\\u8054\\u7f51\",\"detail\":\"观测恢复耗时超过 15 秒\",\"seconds\":269.48,\"unknown\":\"fixture-secret\"}";
            var text = ActivityLog.ReadableHistory(old);
            Require(text.Contains("恢复联网") && text.Contains("269.48 秒") && text.Contains("不能据此认定") && !text.Contains("fixture-secret"));
            Require(ActivityLog.ReadableHistory(old.Replace("观测恢复耗时超过 15 秒", "自动认证已暂停，请重新保存凭据")).Contains("不要仅为解除暂停"));
            return Task.CompletedTask;
        }),
        ("日志：部分损坏不遮蔽正常事件，最近事件优先且限制条数", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var lines = Enumerable.Range(0, 305).Select(i => JsonSerializer.Serialize(new ActivityLog
                { Time = now.AddSeconds(i), Action = "事件" + i, Detail = "记录" }, ActivityLog.Json));
            var text = ActivityLog.ReadableHistory(string.Join('\n', lines) + "\n{unfinished");
            Require(text.Contains("记录不完整") && text.Contains("【事件304】") && !text.Contains("【事件0】"));
            Require(text.IndexOf("【事件304】", StringComparison.Ordinal) < text.IndexOf("【事件303】", StringComparison.Ordinal));
            Require(ActivityLog.ReadableHistory("").Contains("尚无日志"));
            return Task.CompletedTask;
        })
    ];

    private static void Require(bool condition) { if (!condition) throw new Exception("日志行为不符合预期"); }
}
