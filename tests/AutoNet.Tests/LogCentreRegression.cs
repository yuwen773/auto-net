using System.Text;
using System.Text.Json;
using AutoNet.Core;

internal static class LogCentreRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("日志尾部读取：按记录而非按行截断且最新在前", () =>
        {
            var entries = Enumerable.Range(0, 500)
                .Select(i => new ActivityLog { Time = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero).AddSeconds(i), Action = "重连校园 Wi-Fi" })
                .ToArray();
            var path = WriteTemp(entries);
            try
            {
                var records = LogTailReader.Read(path, 300);
                Require(records.Count == 300, $"应返回 300 条，实际 {records.Count}");
                // Newest first, and the newest is the last one written.
                Require(records[0].Time == entries[^1].Time, "第一条必须是最新记录");
                Require(records[^1].Time == entries[200].Time, "最后一条必须是原文件索引 200 的记录");
                for (var i = 1; i < records.Count; i++) Require(records[i].Time <= records[i - 1].Time, "必须按时间倒序");
            }
            finally { File.Delete(path); }
            return Task.CompletedTask;
        }),
        ("日志尾部读取：BOM、CRLF 与被截断的末行都不产生残缺记录", () =>
        {
            var body = "{\"time\":\"2026-10-10T00:00:00Z\",\"action\":\"恢复联网\"}\r\n"
                + "{\"time\":\"2026-10-10T00:00:01Z\",\"action\":\"重连校园 Wi-Fi\"}\r\n";
            var full = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(body)).ToArray();

            Require(LogTailReader.Split(full, startsAtFileStart: true, endsWithNewline: true).Count == 2, "BOM 与 CRLF 应解析为两条完整记录");
            // Reading a window that starts mid-record must drop that fragment.
            Require(LogTailReader.Split(full, startsAtFileStart: false, endsWithNewline: true).Count == 1, "窗口起始处的残行必须丢弃");
            // A file still being appended to may end mid-record.
            var half = Encoding.UTF8.GetBytes(body + "{\"time\":\"2026-10-10T00:00:02Z\",\"act");
            Require(LogTailReader.Split(half, startsAtFileStart: true, endsWithNewline: false).Count == 2, "未写完的末行必须丢弃");
            return Task.CompletedTask;
        }),
        ("日志尾部读取：多字节字符被窗口切断时不产生乱码记录", () =>
        {
            var text = "{\"action\":\"重连校园 Wi-Fi\"}\n{\"action\":\"恢复联网\"}\n";
            var bytes = Encoding.UTF8.GetBytes(text);
            // Cut in the middle of a 3-byte CJK character.
            for (var cut = 1; cut < bytes.Length; cut++)
            {
                var window = bytes.AsSpan(cut);
                var lines = LogTailReader.Split(window, startsAtFileStart: false, endsWithNewline: true);
                foreach (var line in lines)
                    Require(!line.Contains('�'), $"cut={cut} 产生了替换字符：{line}");
                if (cut < Array.IndexOf(bytes, (byte)'\n')) Require(lines.Count == 1 && lines[0].Contains("恢复联网"), "截断第一行不能丢弃完整的第二行");
            }
            return Task.CompletedTask;
        }),
        ("日志尾部读取：损坏行被跳过且不影响同批其他记录", () =>
        {
            var records = LogTailReader.Parse(["{\"action\":\"恢复联网\"}", "{not json", "", "{\"action\":\"重连校园 Wi-Fi\"}"]);
            Require(records.Count == 2, $"应跳过损坏行，实际 {records.Count}");
            Require(records[0].Action == "恢复联网" && records[1].Action == "重连校园 Wi-Fi");
            return Task.CompletedTask;
        }),
        ("日志尾部读取：读取量受限不会随文件大小增长", () =>
        {
            var line = JsonSerializer.Serialize(new ActivityLog { Time = DateTimeOffset.UtcNow, Action = "重连校园 Wi-Fi", Detail = new string('x', 400) }) + "\n";
            var path = Path.Combine(Path.GetTempPath(), $"autonet-tail-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(path, string.Concat(Enumerable.Repeat(line, 20_000)));   // ~9 MB, far past any window
            try
            {
                var before = GC.GetTotalAllocatedBytes(precise: true);
                var records = LogTailReader.Read(path);
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                Require(records.Count == 300, $"应返回 300 条，实际 {records.Count}");
                // A whole-file read of ~9 MB would allocate far more than this ceiling.
                Require(allocated < 6 * 1024 * 1024, $"读取分配 {allocated / 1024 / 1024} MB，超过尾部读取上限");
            }
            finally { File.Delete(path); }
            return Task.CompletedTask;
        }),
        ("脱敏摘要：只输出白名单字段且不携带任何自由文本", () =>
        {
            var hostile = new ActivityLog
            {
                Time = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero),
                Action = "fixture-secret-action",
                Detail = "网关登录失败 https://login.csust.edu.cn:802/eportal/portal/login?user_account=fixture&user_password=fixture-secret",
                LastError = "fixture-secret-error",
                Wifi = "fixture-secret-wifi",
                Direct = "fixture-secret-direct",
                Portal = "fixture-secret-portal",
                SystemPath = "fixture-secret-path",
                Uu = "fixture-secret-uu",
                AuthenticationStage = "fixture-secret-stage",
                AuthenticationFailure = "fixture-secret-failure",
                Seconds = 4.74, Attempts = 2, DisconnectAttempts = 1
            };
            var summary = LogSummary.Format([hostile]);
            Require(!summary.Contains("fixture-secret"), "脱敏摘要泄露了 fixture-secret");
            foreach (var forbidden in new[] { "user_password", "user_account", "login.csust.edu.cn", "http" })
                Require(!summary.Contains(forbidden), $"脱敏摘要泄露了 {forbidden}");
            // Allowlisted values that survive must still be the controlled ones, not the injected ones.
            Require(summary.Contains("其他事件") && summary.Contains("观测耗时 4.74 秒") && summary.Contains("自动动作 2 次"));
            return Task.CompletedTask;
        }),
        ("脱敏摘要：受控字段正常输出且旧版转义日志同样适用", () =>
        {
            var entry = new ActivityLog
            {
                Time = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero),
                Action = "重连校园 Wi-Fi", Wifi = "已连接", Direct = "外网可达", Portal = "已认证",
                Seconds = 7.91, Attempts = 3, DisconnectAttempts = 1,
                AuthenticationStage = nameof(PortalStage.Submission),
                AuthenticationFailure = nameof(PortalFailure.BindingRejected),
                Detail = "这一段自由文本不会出现在摘要中"
            };
            var summary = LogSummary.Format([entry]);
            foreach (var expected in new[] { "重连校园 Wi-Fi", "Wi-Fi 已连接", "直连 外网可达", "认证 已认证",
                "观测耗时 7.91 秒", "自动动作 3 次", "主动断线 1 次", "阶段 Submission", "分类 BindingRejected" })
                Require(summary.Contains(expected), $"摘要缺少 {expected}");
            Require(!summary.Contains("这一段自由文本"), "摘要不得包含 Detail 自由文本");
            Require(LogSummary.Notice(500, 300).Contains("300") && LogSummary.Notice(500, 300).Contains("500"));
            return Task.CompletedTask;
        }),
        ("脱敏摘要：None 分类不输出，未知枚举一律折叠", () =>
        {
            var entry = new ActivityLog
            {
                Action = "恢复联网", AuthenticationFailure = nameof(PortalFailure.None),
                AuthenticationStage = "SomethingFromTheFuture", AuthenticationPersistentState = "Unknown"
            };
            var summary = LogSummary.Format([entry]);
            Require(!summary.Contains("NothingHere") && !summary.Contains("分类 None"));
            Require(!summary.Contains("SomethingFromTheFuture"), "未知阶段必须折叠");
            Require(summary.Contains("恢复联网"), "受控动作仍应输出");
            return Task.CompletedTask;
        })
    ];

    private static string WriteTemp(ActivityLog[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"autonet-tail-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, string.Join("", entries.Select(e => JsonSerializer.Serialize(e, ActivityLog.Json) + "\n")));
        return path;
    }

    private static void Require(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
}
