using AutoNet.Core;

/// <summary>
/// 内嵌认证通道结果契约回归（工单 #21 / ADR-0009）：
/// 结果文件严格读取边界——受控结论词表、时刻必须可解析且不指向未来、
/// 超大文件与伪造内容拒绝、TryLoad 缺失/坏文件语义明确。
/// 填表与页面判定的行为证据由 scripts/qa-authchannel.ps1 的夹具门户页提供。
/// </summary>
internal static class AuthChannelRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("内嵌通道回归：三态结论与未知结论均可解析且保留时刻与说明", () => Sync(() =>
        {
            var at = DateTimeOffset.UtcNow.AddSeconds(-1);
            var ok = AuthChannelResultFile.Parse(Form("LoggedIn", at, "url=portal.fixture/success.html"));
            Require(ok.Conclusion == AuthChannelConclusion.LoggedIn && ok.Detail.Contains("success"), "LoggedIn 解析不符");
            var not = AuthChannelResultFile.Parse(Form("NotLoggedIn", at, "marker=data-portal-error"));
            Require(not.Conclusion == AuthChannelConclusion.NotLoggedIn, "NotLoggedIn 解析不符");
            var human = AuthChannelResultFile.Parse(Form("NeedsHuman", at, "reason=checkbox-rejected"));
            Require(human.Conclusion == AuthChannelConclusion.NeedsHuman && human.Detail.Contains("checkbox"), "NeedsHuman 解析不符");
            var unknown = AuthChannelResultFile.Parse(Form("Unknown", at, ""));
            Require(unknown.Conclusion == AuthChannelConclusion.Unknown && unknown.Detail.Length == 0, "Unknown 解析不符");
        })),
        ("内嵌通道回归：大小写变体与未知结论一律拒绝", () => Sync(() =>
        {
            Require(ThrowsFormat(Form("loggedin", DateTimeOffset.UtcNow, "")), "小写结论未拒绝");
            Require(ThrowsFormat(Form("Logged_In", DateTimeOffset.UtcNow, "")), "下划线结论未拒绝");
            Require(ThrowsFormat(Form("Accepted", DateTimeOffset.UtcNow, "")), "复检消解词汇未拒绝");
        })),
        ("内嵌通道回归：缺失字段、未来时刻与坏 JSON 拒绝", () => Sync(() =>
        {
            Require(ThrowsFormat("{\"conclusion\":\"LoggedIn\"}"), "缺时刻未拒绝");
            Require(ThrowsFormat("{\"detectedAt\":\"" + DateTimeOffset.UtcNow.ToString("o") + "\"}"), "缺结论未拒绝");
            Require(ThrowsFormat("{\"conclusion\":\"LoggedIn\",\"detectedAt\":\"" + DateTimeOffset.UtcNow.AddMinutes(5).ToString("o") + "\"}"), "未来时刻未拒绝");
            Require(ThrowsFormat("{\"conclusion\":123,\"detectedAt\":\"" + DateTimeOffset.UtcNow.ToString("o") + "\"}"), "非字符串结论未拒绝");
            Require(ThrowsFormat("not-json"), "坏 JSON 未拒绝");
            Require(ThrowsFormat("[]"), "非对象根未拒绝");
        })),
        ("内嵌通道回归：TryLoad 对缺失、超大与合法文件语义明确", () => Sync(() =>
        {
            var dir = Directory.CreateTempSubdirectory("autonet-authch-reg");
            try
            {
                Require(!AuthChannelResultFile.TryLoad(Path.Combine(dir.FullName, "nope.json"), out var r1, out var e1)
                    && r1 is null && e1 == "missing", "缺失文件语义不符");
                var big = Path.Combine(dir.FullName, "big.json");
                File.WriteAllText(big, new string('x', 65 * 1024));
                Require(!AuthChannelResultFile.TryLoad(big, out var r2, out var e2) && r2 is null && e2 == "oversized", "超大文件未拒绝");
                var good = Path.Combine(dir.FullName, "good.json");
                File.WriteAllText(good, Form("NeedsHuman", DateTimeOffset.UtcNow.AddSeconds(-1), "reason=captcha"));
                Require(AuthChannelResultFile.TryLoad(good, out var r3, out var e3)
                    && e3.Length == 0 && r3 is not null
                    && r3.Conclusion == AuthChannelConclusion.NeedsHuman
                    && r3.Detail.Contains("captcha"), "合法文件读取不符");
            }
            finally { dir.Delete(recursive: true); }
        })),
        ("内嵌通道回归：Detail 超长截断到受控长度", () => Sync(() =>
        {
            var long_ = new string('x', 900);
            var r = AuthChannelResultFile.Parse(Form("Unknown", DateTimeOffset.UtcNow, long_));
            Require(r.Detail.Length <= 500, "Detail 未截断");
        })),
        ("内嵌通道回归：结论映射到既有凭据提交分类且 Unknown 不伪造", () => Sync(() =>
        {
            Require(AuthChannelReceipt.MapToSubmissionOutcome(AuthChannelConclusion.LoggedIn) == LoginResult.Accepted, "LoggedIn 映射不符");
            Require(AuthChannelReceipt.MapToSubmissionOutcome(AuthChannelConclusion.NotLoggedIn) == LoginResult.RejectedCredentials, "NotLoggedIn 映射不符");
            Require(AuthChannelReceipt.MapToSubmissionOutcome(AuthChannelConclusion.NeedsHuman) == LoginResult.NeedsInteraction, "NeedsHuman 映射不符");
            Require(AuthChannelReceipt.MapToSubmissionOutcome(AuthChannelConclusion.Unknown) is null, "Unknown 不得伪造提交结论");
            Require(Enum.GetValues<LoginResult>().Length == 6, "凭据提交分类被增删——状态种类必须不变");
        })),
        ("内嵌通道回归：回执双核对拒绝路径不符与过期结果", () => Sync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var result = AuthChannelResultFile.Parse(Form("LoggedIn", now, ""));
            Require(AuthChannelReceipt.Validate(result, @"C:\x\result.json", @"C:\x\RESULT.json", now.AddMinutes(-1), out var reason1)
                && reason1.Length == 0, "同一路径不同大小写不应拒绝");
            Require(!AuthChannelReceipt.Validate(result, @"C:\x\other.json", @"C:\x\result.json", now.AddMinutes(-1), out var reason2)
                && reason2 == "path-mismatch", "路径不符未被拒");
            var stale = AuthChannelResultFile.Parse(Form("LoggedIn", now.AddMinutes(-5), ""));
            Require(!AuthChannelReceipt.Validate(stale, @"C:\x\result.json", @"C:\x\result.json", now.AddMinutes(-1), out var reason3)
                && reason3 == "stale-result", "过期结果未被拒");
            Require(!AuthChannelReceipt.Validate(result, @"C:\x\result.json", @"C:\x\result.json", now.AddMinutes(5), out var reason4)
                && reason4 == "stale-result", "时刻早于发起未被拒");
        })),
        ("内嵌通道回归：三事件码归入认证分类且可被日志页筛选", () => Sync(() =>
        {
            foreach (var code in new[] { "AuthChannelStarted", "AuthChannelResult", "AuthChannelNeedsHuman" })
            {
                var entry = new ActivityLog { Time = DateTimeOffset.UtcNow, EventCode = code };
                Require(LogEventClassifier.Classify(entry) == LogEventCategory.Authentication, code + " 未归入认证分类");
            }
        })),
    ];

    private static string Form(string conclusion, DateTimeOffset at, string detail) =>
        "{\"conclusion\":\"" + conclusion + "\",\"detectedAt\":\"" + at.ToString("o") + "\",\"detail\":\""
        + detail.Replace("\"", "\\\"") + "\"}";

    private static bool ThrowsFormat(string json)
    {
        try { AuthChannelResultFile.Parse(json); return false; }
        catch (FormatException) { return true; }
        catch (System.Text.Json.JsonException) { return true; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static async Task Sync(Action body)
    {
        body();
        await Task.CompletedTask;
    }
}
