using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AutoNet.Core;
using AutoNet.Tray.Avalonia;

internal static class SettingsHandoffRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("凭据交接：双方核验后仅保存一次且返回受控结果", SuccessAsync),
        ("凭据交接：发送端发现接收进程不符时拒发且零写入", ReceiverMismatchAsync),
        ("凭据交接：接收端拒绝发送方PID时间SID会话路径与令牌不符且零写入", SenderMismatchAsync),
        ("凭据交接：真实管道断开ACL继承且仅授权指定SID并禁止创建实例", PipeAclAsync),
        ("凭据交接：完成后的关联在发送和接收两端均不能重放", ReplayAsync),
        ("凭据交接：连接和提交确认前超时零写入且残留关联不可复用", TimeoutAsync),
        ("凭据交接：启动等待已耗尽期限时不得重新获得交接时间", ExpiredAsync),
        ("凭据交接：提交确认后回执超时如实报告结果未知且不重试", LostReceiptAsync),
        ("凭据交接：关联不符或损坏载荷拒绝且零写入", InvalidPayloadAsync),
        ("凭据交接：保存启动只传关联元数据且UAC拒绝不建立交接", ElevationRejectedAsync),
        ("凭据交接：独立同账户进程核验并复用设置保存规则且成功路径无明文泄漏", CrossProcessAsync),
        ("凭据交接：提权接收入口拒绝混合参数无效关联与旧进程身份", ReceiverEntryAsync),
        ("凭据交接：保存异常不回显载荷或伪报零写入", SaveExceptionAsync),
        ("凭据交接：旧载荷与确认帧跨接收进程重放被拒且旧存储不变", CrossReceiverReplayAsync)
    ];

    private static async Task SuccessAsync()
    {
        using var process = Process.GetCurrentProcess();
        var peer = HandoffPeer.Capture(process);
        var exchange = new SettingsHandoff(TimeSpan.FromSeconds(5));
        var writes = 0;
        var receive = SettingsHandoff.ReceiveAsync(exchange.Request, peer, draft =>
        {
            Require(draft.Username == "HANDOFF-TEST-USER" && draft.Password == "HANDOFF-TEST-SECRET", "交接内容不符");
            Require(draft.KeepProxyUrl && draft.KeepUuEndpoint && draft.AllowFirstTimeSetup,
                "无投影保留地址标记在管道交接中丢失");
            writes++;
            return new(SettingsSaveOutcome.Saved, ElevatedSettings.SavedMessage);
        }, TimeSpan.FromSeconds(5));
        var send = await exchange.SendAsync(Draft() with
        { KeepProxyUrl = true, KeepUuEndpoint = true, AllowFirstTimeSetup = true }, peer);
        Require(send.Outcome == HandoffOutcome.Completed && send.SaveOutcome == SettingsSaveOutcome.Saved, "发送端未收到保存结果");
        Require((await receive).Outcome == HandoffOutcome.Completed && writes == 1, "接收端未恰好保存一次");
    }

    private static SettingsDraft Draft() => new(SettingsFixture.Profile, "HANDOFF-TEST-USER", "HANDOFF-TEST-SECRET", "", "", true, true, true);

    private static async Task ReceiverMismatchAsync()
    {
        using var process = Process.GetCurrentProcess();
        var peer = HandoffPeer.Capture(process);
        var exchange = new SettingsHandoff(TimeSpan.FromSeconds(2));
        var writes = 0;
        var receive = SettingsHandoff.ReceiveAsync(exchange.Request, peer, _ =>
        {
            writes++;
            return new(SettingsSaveOutcome.Saved, ElevatedSettings.SavedMessage);
        }, TimeSpan.FromSeconds(2));
        var result = await exchange.SendAsync(Draft(), peer with { ProcessId = peer.ProcessId + 1 });
        await receive;
        Require(result.Outcome == HandoffOutcome.Rejected && writes == 0, "接收进程不符仍发送或写入");
    }

    private static async Task SenderMismatchAsync()
    {
        using var process = Process.GetCurrentProcess();
        var peer = HandoffPeer.Capture(process);
        foreach (var wrong in new[]
        {
            peer with { ProcessId = peer.ProcessId + 1 }, peer with { StartedAt = peer.StartedAt + 1 },
            peer with { UserSid = "S-1-5-18" }, peer with { SessionId = peer.SessionId + 1 },
            peer with { ImagePath = peer.ImagePath + ".other" }, peer with { Elevated = !peer.Elevated }
        })
        {
            var exchange = new SettingsHandoff(TimeSpan.FromSeconds(2));
            var writes = 0;
            var receive = SettingsHandoff.ReceiveAsync(exchange.Request, wrong, _ =>
            {
                writes++;
                return new(SettingsSaveOutcome.Saved, ElevatedSettings.SavedMessage);
            }, TimeSpan.FromSeconds(2));
            var send = await exchange.SendAsync(Draft(), peer);
            Require((await receive).Outcome == HandoffOutcome.Rejected && writes == 0, "发送方身份不符仍写入");
            Require(send.Outcome == HandoffOutcome.Rejected, "发送方未收到受控拒绝");
        }
    }

    private static async Task PipeAclAsync()
    {
        using var process = Process.GetCurrentProcess();
        var peer = HandoffPeer.Capture(process);
        var exchange = new SettingsHandoff(TimeSpan.FromSeconds(2));
        var writes = 0;
        var receive = SettingsHandoff.ReceiveAsync(exchange.Request, peer, _ =>
        {
            writes++;
            return new(SettingsSaveOutcome.Saved, ElevatedSettings.SavedMessage);
        }, TimeSpan.FromSeconds(2));
        using (var pipe = new NamedPipeClientStream(".", SettingsHandoff.PipeName(exchange.Request),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None))
        {
            await pipe.ConnectAsync(1000);
            var acl = pipe.GetAccessControl();
            Require(acl.AreAccessRulesProtected, "管道ACL未显式断开继承");
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
            Require(rules.Length > 0 && rules.All(rule => rule.IdentityReference.Value == peer.UserSid &&
                !rule.IsInherited && rule.AccessControlType == AccessControlType.Allow &&
                (rule.PipeAccessRights & PipeAccessRights.CreateNewInstance) == 0), "管道授权了非指定SID或实例创建权限");
        }
        await receive;
        Require(writes == 0, "仅检查ACL产生保存");
    }

    private static async Task ReplayAsync()
    {
        using var process = Process.GetCurrentProcess();
        var peer = HandoffPeer.Capture(process);
        var exchange = new SettingsHandoff(TimeSpan.FromMilliseconds(500));
        var writes = 0;
        SettingsSaveResult Save(SettingsDraft _) { writes++; return new(SettingsSaveOutcome.Saved, ElevatedSettings.SavedMessage); }
        var receive = SettingsHandoff.ReceiveAsync(exchange.Request, peer, Save, TimeSpan.FromSeconds(2));
        Require((await exchange.SendAsync(Draft(), peer)).Outcome == HandoffOutcome.Completed, "首次交接失败");
        await receive;
        Require((await exchange.SendAsync(Draft(), peer)).Outcome == HandoffOutcome.Rejected, "发送端重用已完成关联");
        Require((await SettingsHandoff.ReceiveAsync(exchange.Request, peer, Save, TimeSpan.FromMilliseconds(100))).Outcome == HandoffOutcome.Rejected,
            "接收端重新开放已消费关联");
        Require(writes == 1, "重放造成重复写入");
    }

    private static async Task TimeoutAsync()
    {
        using var process = Process.GetCurrentProcess();
        var peer = HandoffPeer.Capture(process);
        var missing = new SettingsHandoff(TimeSpan.FromMilliseconds(150));
        Require((await missing.SendAsync(Draft(), peer)).Outcome == HandoffOutcome.TimedOut, "未连接超时未受控返回");
        Require((await missing.SendAsync(Draft(), peer)).Outcome == HandoffOutcome.Rejected, "超时发送关联可复用");

        foreach (var stage in new[] { "connect", "payload", "commit" })
        {
            var exchange = new SettingsHandoff(TimeSpan.FromSeconds(2));
            var writes = 0;
            SettingsSaveResult Save(SettingsDraft _) { writes++; return new(SettingsSaveOutcome.Saved, ElevatedSettings.SavedMessage); }
            var receive = SettingsHandoff.ReceiveAsync(exchange.Request, peer, Save, TimeSpan.FromMilliseconds(350));
            if (stage == "connect") Require((await receive).Outcome == HandoffOutcome.TimedOut, "等待连接未超时");
            else
            {
                using var pipe = Client(exchange.Request);
                await pipe.ConnectAsync(1000);
                await pipe.WriteAsync(exchange.Request.Id.ToByteArray());
                await pipe.ReadExactlyAsync(new byte[16]);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(Draft());
                await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length));
                if (stage == "commit")
                {
                    await pipe.WriteAsync(bytes);
                    await pipe.ReadExactlyAsync(new byte[16]);
                }
                Require((await receive).Outcome == HandoffOutcome.TimedOut, "不完整交接未超时");
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }
            Require(writes == 0, "超时仍调用了设置保存");
            Require((await SettingsHandoff.ReceiveAsync(exchange.Request, peer, Save, TimeSpan.FromMilliseconds(100))).Outcome == HandoffOutcome.Rejected,
                "超时接收关联可复用");
        }
    }

    private static NamedPipeClientStream Client(HandoffRequest request) => new(".", SettingsHandoff.PipeName(request),
        PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, PipeOptions.Asynchronous,
        TokenImpersonationLevel.Identification, HandleInheritability.None);

    private static async Task ExpiredAsync()
    {
        using var process = Process.GetCurrentProcess();
        var peer = HandoffPeer.Capture(process);
        var exchange = new SettingsHandoff(TimeSpan.FromMilliseconds(100));
        await Task.Delay(150);
        var writes = 0;
        var receive = SettingsHandoff.ReceiveAsync(exchange.Request, peer, _ =>
        {
            writes++;
            return new(SettingsSaveOutcome.Saved, ElevatedSettings.SavedMessage);
        }, TimeSpan.FromMilliseconds(350));
        Require((await exchange.SendAsync(Draft(), peer)).Outcome == HandoffOutcome.TimedOut, "过期关联获得了新期限");
        await receive;
        Require(writes == 0, "启动等待超时后仍写入");
    }

    private static async Task LostReceiptAsync()
    {
        using var process = Process.GetCurrentProcess();
        var peer = HandoffPeer.Capture(process);
        var exchange = new SettingsHandoff(TimeSpan.FromMilliseconds(500));
        var writes = 0;
        var receive = SettingsHandoff.ReceiveAsync(exchange.Request, peer, _ =>
        {
            writes++;
            Thread.Sleep(800);
            return new(SettingsSaveOutcome.Saved, ElevatedSettings.SavedMessage);
        }, TimeSpan.FromSeconds(3));
        var result = await exchange.SendAsync(Draft(), peer);
        Require(result.Outcome == HandoffOutcome.OutcomeUnknown, "已授权写入后的回执超时被误报为零写入");
        Require((await receive).SaveOutcome == SettingsSaveOutcome.Saved && writes == 1, "接收端未保留实际保存结果");
        Require((await exchange.SendAsync(Draft(), peer)).Outcome == HandoffOutcome.Rejected && writes == 1, "回执丢失导致自动重试");
    }

    private static async Task InvalidPayloadAsync()
    {
        using var process = Process.GetCurrentProcess();
        var peer = HandoffPeer.Capture(process);
        foreach (var data in new[] { "wrong-id", "oversized", "{", "{}", "null", "{\"Username\":null}" })
        {
            var exchange = new SettingsHandoff(TimeSpan.FromSeconds(2));
            var writes = 0;
            var receive = SettingsHandoff.ReceiveAsync(exchange.Request, peer, _ =>
            {
                writes++;
                return new(SettingsSaveOutcome.Saved, ElevatedSettings.SavedMessage);
            }, TimeSpan.FromSeconds(2));
            using var pipe = Client(exchange.Request);
            await pipe.ConnectAsync(1000);
            await pipe.WriteAsync((data == "wrong-id" ? Guid.NewGuid() : exchange.Request.Id).ToByteArray());
            if (data != "wrong-id")
            {
                await pipe.ReadExactlyAsync(new byte[16]);
                var bytes = System.Text.Encoding.UTF8.GetBytes(data);
                await pipe.WriteAsync(BitConverter.GetBytes(data == "oversized" ? int.MaxValue : bytes.Length));
                if (data != "oversized") await pipe.WriteAsync(bytes);
            }
            Require((await receive).Outcome == HandoffOutcome.Rejected && writes == 0, "损坏载荷未受控拒绝");
        }
    }

    private static async Task ElevationRejectedAsync()
    {
        ProcessStartInfo? captured = null;
        var result = await SettingsHandoffLauncher.SaveAsync(Draft(), info =>
        {
            captured = info;
            throw new System.ComponentModel.Win32Exception(1223);
        });
        Require(result.Outcome == HandoffOutcome.Cancelled, "UAC拒绝未作为取消处理");
        Require(captured is { UseShellExecute: true, Verb: "runas" } && captured.ArgumentList.Count == 4,
            "保存未使用独立提权关联入口");
        var launchText = string.Join(" ", captured!.ArgumentList) + captured.Arguments;
        Require(!launchText.Contains("HANDOFF-TEST-USER") && !launchText.Contains("HANDOFF-TEST-SECRET"), "凭据进入命令行");
        Require(captured.Environment.Values.All(value => value is null || !value.Contains("HANDOFF-TEST-SECRET")), "密码进入环境变量");
    }

    private static async Task CrossProcessAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var exchange = new SettingsHandoff(TimeSpan.FromSeconds(10));
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = directory
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(typeof(SettingsHandoffRegression).Assembly.Location);
            info.ArgumentList.Add("--handoff-test-peer");
            info.ArgumentList.Add(JsonSerializer.Serialize(exchange.Request));
            using var child = Process.Start(info) ?? throw new Exception("无法启动隔离交接进程");
            try
            {
                var peer = HandoffPeer.Capture(child);
                Require(peer.ProcessId != Environment.ProcessId && peer.UserSid == HandoffPeer.Capture(Environment.ProcessId).UserSid,
                    "未形成独立同账户进程");
                var outputTask = child.StandardOutput.ReadToEndAsync();
                var errorTask = child.StandardError.ReadToEndAsync();
                var sent = await exchange.SendAsync(Draft(), peer);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
                var output = await outputTask;
                var error = await errorTask;
                Require(child.ExitCode == 0 && sent.SaveOutcome == SettingsSaveOutcome.Saved && output.Trim() == "SAVED-ONCE;SIDE-CHANNELS-CLEAN",
                    "独立进程交接或保存规则失败");
                Require(!output.Contains("HANDOFF-TEST-SECRET") && !error.Contains("HANDOFF-TEST-SECRET"), "进程输出泄漏密码");
                Require(!string.Join(" ", info.ArgumentList).Contains("HANDOFF-TEST-SECRET"), "启动参数泄漏密码");
                foreach (var file in Directory.EnumerateFiles(directory))
                    Require(!SettingsFixture.ContainsUtf8(File.ReadAllBytes(file), "HANDOFF-TEST-SECRET"), "隔离文件出现密码明文");
                Require(SettingsFixture.ReadCredentials(Path.Combine(directory, "credentials.dpapi"))?.Password == "HANDOFF-TEST-SECRET",
                    "DPAPI保存内容不符");
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(directory, "settings.json")));
                Require(settings?.WifiProfile == SettingsFixture.Profile.Name, "设置保存结果不符");
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public static async Task<int> RunPeerAsync(string[] args)
    {
        try
        {
            var request = JsonSerializer.Deserialize<HandoffRequest>(args[1])!;
            var sender = HandoffPeer.Capture(request.SenderProcessId);
            var directory = Environment.CurrentDirectory;
            var writes = 0;
            var editor = new ElevatedSettings(() => null, () => null, () => [SettingsFixture.Profile],
                credentials => SettingsFixture.WriteCredentials(Path.Combine(directory, "credentials.dpapi"), credentials),
                settings => Storage.AtomicWrite(Path.Combine(directory, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(settings)));
            var result = await SettingsHandoff.ReceiveAsync(request, sender, draft =>
            {
                if (draft.Password != "HANDOFF-TEST-SECRET" || draft.Username != "HANDOFF-TEST-USER")
                    return new(SettingsSaveOutcome.InvalidSettings, "测试内容不符");
                writes++;
                return editor.Save(draft);
            }, TimeSpan.FromSeconds(10));
            var clean = !Environment.CommandLine.Contains("HANDOFF-TEST-SECRET") &&
                Environment.GetEnvironmentVariables().Values.Cast<string>().All(value => !value.Contains("HANDOFF-TEST-SECRET"));
            if (result.SaveOutcome != SettingsSaveOutcome.Saved || writes != 1 || !clean) return 1;
            Console.WriteLine("SAVED-ONCE;SIDE-CHANNELS-CLEAN");
            return 0;
        }
        catch { return 2; } // 夹具不回显任何收到的载荷或异常文本。
    }

    private static async Task ReceiverEntryAsync()
    {
        var peer = HandoffPeer.Capture(Environment.ProcessId);
        var command = SettingsHandoffLauncher.Command;
        foreach (var args in new[]
        {
            new[] { command }, new[] { command, "bad", "0", "0" },
            new[] { command, Guid.NewGuid().ToString("N"), "-1", "-1" },
            new[] { command, Guid.NewGuid().ToString("N"), peer.ProcessId.ToString(), (peer.StartedAt + 1).ToString() },
            new[] { command, Guid.NewGuid().ToString("N"), peer.ProcessId.ToString(), peer.StartedAt.ToString(), "--settings" }
        })
            Require(await SettingsHandoffLauncher.RunReceiverAsync(args) == 2, "非法接收入口未在任何保存前拒绝");
    }

    private static async Task SaveExceptionAsync()
    {
        var peer = HandoffPeer.Capture(Environment.ProcessId);
        var exchange = new SettingsHandoff(TimeSpan.FromSeconds(2));
        var writes = 0;
        var receive = SettingsHandoff.ReceiveAsync(exchange.Request, peer, draft =>
        {
            writes++;
            throw new IOException(draft.Password);
        }, TimeSpan.FromSeconds(2));
        var sent = await exchange.SendAsync(Draft(), peer);
        Exception? escaped = null;
        HandoffResult? received = null;
        try { received = await receive; }
        catch (Exception e) { escaped = e; }
        Require(escaped is null && received?.Outcome == HandoffOutcome.OutcomeUnknown && sent.Outcome == HandoffOutcome.OutcomeUnknown,
            "保存异常逃逸或已发生的写入被隐藏");
        Require(writes == 1, "异常后重复保存");
    }

    private static async Task CrossReceiverReplayAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "handoff-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var request = new SettingsHandoff(TimeSpan.FromSeconds(10)).Request;
        var payload = JsonSerializer.SerializeToUtf8Bytes(Draft());
        byte[]? oldConfirmation = null;
        byte[]? credentialsBefore = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true, WorkingDirectory = directory
                };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    info.ArgumentList.Add(typeof(SettingsHandoffRegression).Assembly.Location);
                info.ArgumentList.Add("--handoff-test-peer");
                info.ArgumentList.Add(JsonSerializer.Serialize(request));
                using var child = Process.Start(info) ?? throw new Exception("无法启动重放验证进程");
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var pipe = Client(request);
                    await pipe.ConnectAsync(deadline.Token);
                    await pipe.WriteAsync(request.Id.ToByteArray(), deadline.Token);
                    var challenge = new byte[16];
                    await pipe.ReadExactlyAsync(challenge, deadline.Token);
                    oldConfirmation ??= challenge;
                    await pipe.WriteAsync(BitConverter.GetBytes(payload.Length), deadline.Token);
                    await pipe.WriteAsync(payload, deadline.Token);
                    await pipe.ReadExactlyAsync(new byte[16], deadline.Token);
                    await pipe.WriteAsync(oldConfirmation, deadline.Token);
                    var response = new byte[1];
                    try { await pipe.ReadExactlyAsync(response, deadline.Token); }
                    catch (EndOfStreamException) when (attempt == 1) { }
                    await child.WaitForExitAsync(deadline.Token);
                    if (attempt == 0)
                    {
                        Require(child.ExitCode == 0 && response[0] == (byte)SettingsSaveOutcome.Saved, "首次消息交接未成功");
                        credentialsBefore = File.ReadAllBytes(Path.Combine(directory, "credentials.dpapi"));
                    }
                    else
                    {
                        Require(child.ExitCode != 0, "另一接收进程接受了旧确认帧");
                        Require(File.ReadAllBytes(Path.Combine(directory, "credentials.dpapi")).SequenceEqual(credentialsBefore!),
                            "跨接收进程重放改写了原凭据");
                    }
                }
                finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload);
            Directory.Delete(directory, recursive: true);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
