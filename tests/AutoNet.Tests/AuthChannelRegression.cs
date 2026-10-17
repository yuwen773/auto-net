using AutoNet.Core;
using AutoNet.Tray.Avalonia;

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
        ("内嵌通道回归：自动降级判定四态——仅 Automatic 且结构信号与阈值同时成立", () => Sync(() =>
        {
            Require(RecoveryPolicy.ShouldRequestAuthChannel(AuthChannelModes.Automatic, true, PortalFailure.InteractionRequired, 3),
                "自动模式+信号+阈值应发起");
            Require(!RecoveryPolicy.ShouldRequestAuthChannel(AuthChannelModes.ManualOnly, true, PortalFailure.InteractionRequired, 3),
                "仅手动模式不得自动发起");
            Require(!RecoveryPolicy.ShouldRequestAuthChannel(AuthChannelModes.Off, true, PortalFailure.InteractionRequired, 3),
                "关闭模式不得自动发起");
            Require(!RecoveryPolicy.ShouldRequestAuthChannel(null, true, PortalFailure.InteractionRequired, 3),
                "未知模式不得自动发起");
            Require(!RecoveryPolicy.ShouldRequestAuthChannel(AuthChannelModes.Automatic, false, PortalFailure.InteractionRequired, 3),
                "无结构信号不得发起——正常时期协议认证路径不变");
            Require(!RecoveryPolicy.ShouldRequestAuthChannel(AuthChannelModes.Automatic, true, PortalFailure.InteractionRequired, 2),
                "未达阈值不得发起");
            Require(!RecoveryPolicy.ShouldRequestAuthChannel(AuthChannelModes.Automatic, true, PortalFailure.CredentialsRejected, 3),
                "非交互要求失败形态不得发起");
        })),
        ("内嵌通道回归：设置三态校验——词表之外拒绝", () => Sync(() =>
        {
            foreach (var mode in new[] { AuthChannelModes.Automatic, AuthChannelModes.ManualOnly, AuthChannelModes.Off })
            {
                new Settings { InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "CSUST", AuthChannelMode = mode }.Validate();
            }
            Require(ThrowsArgument(
                new Settings { InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "CSUST", AuthChannelMode = "Always" }), "词表外模式未拒绝");
            Require(ThrowsArgument(
                new Settings { InterfaceId = "00000000-0000-0000-0000-000000000001", WifiProfile = "CSUST", AuthChannelMode = "" }), "空模式未拒绝");
            Require(AuthChannelModes.IsKnown(AuthChannelModes.Automatic) && !AuthChannelModes.IsKnown("Always"), "IsKnown 语义不符");
        })),
        ("内嵌通道回归：发起器消费授予并逐字转写提权命令，同授予不重复发起", async () =>
        {
            var started = new List<string>();
            var launcher = new AutoNet.Tray.Avalonia.AuthChannelLauncher(
                () => Task.FromResult<StatusSnapshot?>(Grant()),
                resultPath =>
                {
                    Require(resultPath.EndsWith("auth-channel-result.json", StringComparison.Ordinal), "结果路径不符约定：" + resultPath);
                    started.Add(resultPath);
                    return new System.Diagnostics.Process();
                });
            var first = await launcher.ObserveAsync(Grant());
            Require(first == AutoNet.Tray.Avalonia.AuthChannelLauncher.ObserveOutcome.Launched, "首次授予应发起");
            Require(started.Count == 1, "首次授予应恰好启动一次");
            var second = await launcher.ObserveAsync(Grant());
            Require(second == AutoNet.Tray.Avalonia.AuthChannelLauncher.ObserveOutcome.AlreadyConsumed && started.Count == 1,
                "同一授予不得重复发起");
        }),
        ("内嵌通道回归：UAC 拒绝即不再发起、请求消失即不发起、无信号路径零动作", async () =>
        {
            var declined = new AutoNet.Tray.Avalonia.AuthChannelLauncher(
                () => Task.FromResult<StatusSnapshot?>(Grant()),
                _ => null);
            var outcome = await declined.ObserveAsync(Grant());
            Require(outcome == AutoNet.Tray.Avalonia.AuthChannelLauncher.ObserveOutcome.UacDeclined, "UAC 拒绝应返回取消语义");
            var again = await declined.ObserveAsync(Grant());
            Require(again == AutoNet.Tray.Avalonia.AuthChannelLauncher.ObserveOutcome.AlreadyConsumed, "拒绝后不得自动重试");
            var quiet = new AutoNet.Tray.Avalonia.AuthChannelLauncher(
                () => Task.FromResult<StatusSnapshot?>(Grant() with { AuthChannelRequested = false }),
                _ => throw new InvalidOperationException("无授予不应发起"));
            Require(await quiet.ObserveAsync(Grant() with { AuthChannelRequested = false })
                == AutoNet.Tray.Avalonia.AuthChannelLauncher.ObserveOutcome.NoGrant, "无授予不得发起");
            Require(await quiet.ObserveAsync(Grant() with { AuthChannelNotBefore = DateTimeOffset.UtcNow.AddHours(1) })
                == AutoNet.Tray.Avalonia.AuthChannelLauncher.ObserveOutcome.UntrustedState, "授予时间戳变化不得发起（发起前重验）");
        }),
        ("内嵌通道回归：结果文件回读双核对且结论经映射进入投影", async () =>
        {
            var dir = Directory.CreateTempSubdirectory("autonet-launcher-reg");
            try
            {
                var resultPath = Path.Combine(dir.FullName, "auth-channel-result.json");
                var launchAt = DateTimeOffset.UtcNow.AddSeconds(-3);
                AutoNet.Tray.Avalonia.AuthChannelLauncher? launcher = null;
                launcher = new AutoNet.Tray.Avalonia.AuthChannelLauncher(
                    () => Task.FromResult<StatusSnapshot?>(Grant()),
                    path =>
                    {
                        File.WriteAllText(path, Form("LoggedIn", DateTimeOffset.UtcNow, "submit-once"));
                        return new System.Diagnostics.Process();
                    });
                // 发起：ObserveAsync 内部以「当前时刻」记发起——回执时刻晚于它即可通过双核对。
                var outcome = await launcher.ObserveAsync(Grant());
                Require(outcome == AutoNet.Tray.Avalonia.AuthChannelLauncher.ObserveOutcome.Launched, "回执用例应成功发起");
                var after = await launcher.ObserveAsync(Grant());
                Require(after == AutoNet.Tray.Avalonia.AuthChannelLauncher.ObserveOutcome.AlreadyConsumed, "回读轮不应重复发起");
                Require(launcher.Describe().LastConclusion == LoginResult.Accepted.ToString(),
                    "LoggedIn 应映射为 Accepted 进入投影：" + launcher.Describe().LastConclusion);
                Require(!File.Exists(resultPath) || !File.ReadAllText(resultPath).Contains("LoggedIn"), "已消费的结果文件应被清理");
            }
            finally { dir.Delete(recursive: true); }
        }),
        ("内嵌通道回归：关闭态手动与自动一律拒绝", () => Sync(() =>
        {
            var offReading = Grant() with { AuthChannelMode = AuthChannelModes.Off };
            var launcher = new AutoNet.Tray.Avalonia.AuthChannelLauncher(
                () => Task.FromResult<StatusSnapshot?>(offReading),
                _ => throw new InvalidOperationException("关闭态不应发起"));
            Require(launcher.LaunchManually(offReading) == AutoNet.Tray.Avalonia.AuthChannelLauncher.ObserveOutcome.ModeOff,
                "关闭态手动入口应拒绝");
            var outcome = launcher.ObserveAsync(offReading).GetAwaiter().GetResult();
            Require(outcome == AutoNet.Tray.Avalonia.AuthChannelLauncher.ObserveOutcome.ModeOff,
                "关闭态即使出现异常授予，托盘兜底也应拒绝发起");
        })),
        ("内嵌通道回归：通道无法运行即回退浏览器兜底并受控呈现，绝不静默", async () =>
        {
            var resultPath = Path.Combine(Path.GetTempPath(), "autonet-reg-fallback-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                NotificationAlert? presented = null;
                var fallbackInvoked = 0;
                var grant = Grant() with { AuthChannelNotBefore = DateTimeOffset.UtcNow.AddMinutes(1) };
                var launcher = new AutoNet.Tray.Avalonia.AuthChannelLauncher(
                    () => Task.FromResult<StatusSnapshot?>(grant),
                    path =>
                    {
                        // 模拟通道运行失败（如 WebView2 Runtime 缺失）：失败结果文件由 Main 落盘。
                        File.WriteAllText(path, Form("Unknown", DateTimeOffset.UtcNow, "error=WebView2RuntimeNotFoundException"));
                        return new System.Diagnostics.Process();
                    },
                    alert => presented = alert,
                    () => { fallbackInvoked++; return Task.CompletedTask; });
                await launcher.ObserveAsync(grant);
                var after = await launcher.ObserveAsync(grant with { });
                Require(fallbackInvoked == 1, "失败应恰好回退浏览器兜底一次（实际 " + fallbackInvoked + "）");
                Require(presented is { } alert && alert.Code == "AuthChannelNeedsHuman" && alert.Body.Contains("回退"),
                    "回退必须受控呈现而非静默");
                Require(launcher.Describe().LastConclusion == "Unknown", "失败结论应如实投影");
            }
            finally { try { File.Delete(resultPath); } catch { } }
        }),
        ("内嵌通道回归：结论呈现走受控词表且每次结果只呈现一次", async () =>
        {
            var presented = new List<string>();
            var grant = Grant() with { AuthChannelNotBefore = DateTimeOffset.UtcNow.AddMinutes(2) };
            var launcher = new AutoNet.Tray.Avalonia.AuthChannelLauncher(
                () => Task.FromResult<StatusSnapshot?>(grant),
                path =>
                {
                    File.WriteAllText(path, Form("NeedsHuman", DateTimeOffset.UtcNow, "reason=captcha"));
                    return new System.Diagnostics.Process();
                },
                alert => presented.Add(alert.Code),
                null);
            await launcher.ObserveAsync(grant);
            await launcher.ObserveAsync(grant);
            await launcher.ObserveAsync(grant);
            Require(presented.SequenceEqual(["AuthChannelNeedsHuman"]),
                "需人工应呈现一次且码正确：" + string.Join(",", presented));
        }),
        ("内嵌通道回归：通道事件写入结构化日志且可被分类器识别", () => Sync(() =>
        {
            var dir = Directory.CreateTempSubdirectory("autonet-channel-log");
            try
            {
                AuthChannelLogWriter.Append("AuthChannelNeedsHuman", "window-shown=true", dir.FullName);
                AuthChannelLogWriter.Append("AuthChannelResult", "conclusion=LoggedIn", dir.FullName);
                var log = File.ReadAllText(Path.Combine(dir.FullName, "autonet-" + DateTimeOffset.Now.ToLocalTime().ToString("yyyy-MM-dd") + ".jsonl"));
                var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Require(lines.Length == 2, "两事件应各占一行");
                foreach (var line in lines)
                {
                    var entry = System.Text.Json.JsonSerializer.Deserialize<ActivityLog>(line, ActivityLog.Json)!;
                    Require(entry.SchemaVersion == 2 && entry.EventCode is "AuthChannelNeedsHuman" or "AuthChannelResult",
                        "日志行应保留 v2 结构与事件码");
                    Require(LogEventClassifier.Classify(entry) == LogEventCategory.Authentication, "通道事件应归认证分类");
                }
            }
            finally { dir.Delete(recursive: true); }
        })),
    ];

    private static StatusSnapshot Grant() => new()
    {
        AuthChannelRequested = true,
        AuthChannelNotBefore = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero),
        AuthChannelMode = AuthChannelModes.Automatic,
        PortalLayoutSuspected = true
    };

    private static string Form(string conclusion, DateTimeOffset at, string detail) =>
        "{\"conclusion\":\"" + conclusion + "\",\"detectedAt\":\"" + at.ToString("o") + "\",\"detail\":\""
        + detail.Replace("\"", "\\\"") + "\"}";

    private static bool ThrowsFormat(string json)
    {
        try { AuthChannelResultFile.Parse(json); return false; }
        catch (FormatException) { return true; }
        catch (System.Text.Json.JsonException) { return true; }
    }

    private static bool ThrowsArgument(Settings settings)
    {
        try { settings.Validate(); return false; }
        catch (ArgumentException) { return true; }
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
