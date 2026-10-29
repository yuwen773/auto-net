using AutoNet.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Globalization;

internal static class SubmissionBudgetRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("提交额度：首次持久预留独立于认证暂停且重读保留次数与时刻", () => Run(directory =>
        {
            var store = new SafetyStateStore(directory);
            var clock = new ManualClock();
            var now = clock.GetUtcNow();
            var block = new AuthenticationBlock(PortalFailure.CredentialsRejected) { Revision = Guid.NewGuid() };
            store.BlockAuthentication(block);
            Require(store.LoadSubmissionBudget() is null, "缺失额度不是初始空记录");
            var result = store.TryReserveSubmissionBudget(null, now);
            Require(result.Outcome == SubmissionReservationOutcome.Reserved, "首次预留未成功");
            var budget = result.Budget;
            Require(budget is { Version: 1, State: SubmissionBudgetState.Reserved, Attempts: 1 } &&
                budget.ReservedAt == now && budget.NextPossibleAt == now.AddSeconds(120) && budget.Revision != Guid.Empty,
                "预留次数、时刻或修订不符");
            Require(new SafetyStateStore(directory).LoadSubmissionBudget() == budget, "重新读取丢失预留");
            Require(store.LoadAuthenticationBlock() == block, "提交额度修改了认证安全记录");
        })),
        ("提交额度：修订比较转换仅推进状态且旧修订不覆盖并发结果", () => Run(directory =>
        {
            var store = new SafetyStateStore(directory);
            var reserved = store.TryReserveSubmissionBudget(null, new ManualClock().GetUtcNow()).Budget!;
            Require(store.TryTransitionSubmissionBudget(reserved, SubmissionBudgetState.PossiblySent), "预留不能推进为可能已发送");
            var sent = store.LoadSubmissionBudget()!;
            Require(sent.State == SubmissionBudgetState.PossiblySent && sent.Revision != reserved.Revision &&
                sent.Attempts == reserved.Attempts && sent.ReservedAt == reserved.ReservedAt && sent.NextPossibleAt == reserved.NextPossibleAt,
                "状态推进修改了已用次数或时间");
            Require(!store.TryTransitionSubmissionBudget(reserved, SubmissionBudgetState.Concluded), "旧修订覆盖了新状态");
            Require(store.LoadSubmissionBudget() == sent, "旧修订拒绝后仍发生写入");
            Require(store.TryTransitionSubmissionBudget(sent, SubmissionBudgetState.Concluded), "明确结束不能持久化");
            var concluded = store.LoadSubmissionBudget()!;
            Require(!store.TryTransitionSubmissionBudget(concluded, SubmissionBudgetState.PossiblySent), "结束状态被反向放宽");
            Require(store.LoadSubmissionBudget() == concluded, "非法转换修改持久状态");
        })),
        ("提交额度：活动预留不重复计数且119.999秒拒绝120秒允许三次封顶", () => Run(directory =>
        {
            var store = new SafetyStateStore(directory);
            var clock = new ManualClock();
            var first = store.TryReserveSubmissionBudget(null, clock.GetUtcNow()).Budget!;
            Require(store.TryReserveSubmissionBudget(first, clock.GetUtcNow().AddMinutes(5)).Outcome == SubmissionReservationOutcome.InFlight,
                "活动预留被第二次计数");
            Require(store.TryTransitionSubmissionBudget(first, SubmissionBudgetState.PossiblySent), "标记可能已发送失败");
            var sent = store.LoadSubmissionBudget();
            clock.Advance(TimeSpan.FromMilliseconds(119999));
            Require(store.TryReserveSubmissionBudget(sent, clock.GetUtcNow()).Outcome == SubmissionReservationOutcome.Waiting, "不足120秒获得提交额度");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var second = store.TryReserveSubmissionBudget(sent, clock.GetUtcNow());
            Require(second.Outcome == SubmissionReservationOutcome.Reserved && second.Budget?.Attempts == 2, "120秒未获得第二次额度");
            Require(store.TryTransitionSubmissionBudget(second.Budget!, SubmissionBudgetState.PossiblySent), "第二次标记失败");
            clock.Advance(TimeSpan.FromSeconds(120));
            var third = store.TryReserveSubmissionBudget(store.LoadSubmissionBudget(), clock.GetUtcNow());
            Require(third.Outcome == SubmissionReservationOutcome.Reserved && third.Budget?.Attempts == 3, "第三次额度不符");
            Require(store.TryTransitionSubmissionBudget(third.Budget!, SubmissionBudgetState.PossiblySent), "第三次标记失败");
            clock.Advance(TimeSpan.FromHours(1));
            var before = store.LoadSubmissionBudget();
            Require(store.TryReserveSubmissionBudget(before, clock.GetUtcNow()).Outcome == SubmissionReservationOutcome.Exhausted, "第四次预留被允许");
            Require(store.LoadSubmissionBudget() == before, "拒绝后仍改变额度");
        })),
        ("提交额度：墙钟回拨返回保守等待且不改写预留时间", () => Run(directory =>
        {
            var store = new SafetyStateStore(directory);
            var clock = new ManualClock();
            var first = store.TryReserveSubmissionBudget(null, clock.GetUtcNow()).Budget!;
            Require(store.TryTransitionSubmissionBudget(first, SubmissionBudgetState.PossiblySent), "准备状态失败");
            var sent = store.LoadSubmissionBudget();
            clock.Advance(TimeSpan.FromSeconds(1), TimeSpan.FromHours(-1));
            Require(store.TryReserveSubmissionBudget(sent, clock.GetUtcNow()).Outcome == SubmissionReservationOutcome.WaitClock,
                "回拨未返回时钟保守等待");
            clock.Advance(TimeSpan.FromSeconds(120));
            Require(store.TryReserveSubmissionBudget(sent, clock.GetUtcNow()).Outcome == SubmissionReservationOutcome.WaitClock,
                "仍早于持久时刻时缩短了等待");
            Require(store.LoadSubmissionBudget() == sent, "回拨改写了持久时刻或次数");
        })),
        ("提交额度：损坏缺字段未知版本重复与不一致记录均拒绝预留并保留原字节", () => Run(directory =>
        {
            const string valid = """
                {"Version":1,"State":"PossiblySent","Attempts":1,"ReservedAt":"2026-10-28T00:00:00Z","NextPossibleAt":"2026-10-28T00:02:00Z","Revision":"11111111-2222-3333-4444-555555555555"}
                """;
            var cases = new List<string> { "{broken", "null", "[]", "{}" };
            foreach (var name in new[] { "Version", "State", "Attempts", "ReservedAt", "NextPossibleAt", "Revision" })
            {
                var node = JsonNode.Parse(valid)!.AsObject(); node.Remove(name); cases.Add(node.ToJsonString());
            }
            foreach (var (field, value) in new (string, object?)[]
            {
                ("Version", 2), ("Version", null), ("State", "Unknown"), ("State", 0), ("State", "reserved"),
                ("Attempts", -1), ("Attempts", 4), ("Attempts", 0), ("Attempts", "1"),
                ("Revision", Guid.Empty.ToString()), ("ReservedAt", "0001-01-01T00:00:00Z"),
                ("NextPossibleAt", "2026-10-28T00:01:59Z"), ("NextPossibleAt", null), ("Password", "FIXTURE-ONLY")
            })
            {
                var node = JsonNode.Parse(valid)!.AsObject(); node[field] = JsonSerializer.SerializeToNode(value); cases.Add(node.ToJsonString());
            }
            cases.Add(valid.Replace("\"Version\":1", "\"Version\":1,\"Version\":1"));
            cases.Add(valid.Replace("\"Version\":1", "\"Version\":1,\"version\":1"));
            var path = Path.Combine(directory, "submission-budget.json");
            var store = new SafetyStateStore(directory);
            foreach (var text in cases)
            {
                File.WriteAllText(path, text);
                var original = File.ReadAllBytes(path);
                var blocked = false;
                try { store.LoadSubmissionBudget(); }
                catch (InvalidDataException) { blocked = true; }
                Require(blocked, "无效记录读取未保守拒绝");
                Require(store.TryReserveSubmissionBudget(null, new ManualClock().GetUtcNow()).Outcome == SubmissionReservationOutcome.InvalidState,
                    "无效记录未保守拒绝预留");
                Require(File.ReadAllBytes(path).SequenceEqual(original), "无效记录被改写或自动初始化");
            }
        })),
        ("提交额度：共享锁竞争与原子替换失败均拒绝预留且旧记录不变", () => Run(directory =>
        {
            var store = new SafetyStateStore(directory);
            var clock = new ManualClock();
            var first = store.TryReserveSubmissionBudget(null, clock.GetUtcNow()).Budget!;
            Require(store.TryTransitionSubmissionBudget(first, SubmissionBudgetState.PossiblySent), "准备持久状态失败");
            var before = store.LoadSubmissionBudget();
            using (var held = new FileStream(Path.Combine(directory, "auth-state.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Require(store.TryReserveSubmissionBudget(before, clock.GetUtcNow()).Outcome == SubmissionReservationOutcome.StorageUnavailable,
                    "锁未取得仍返回可提交");
                var readFailed = false;
                try { store.LoadSubmissionBudget(); } catch (IOException) { readFailed = true; }
                Require(readFailed, "读取绕过了共享独占锁");
                var transitionFailed = false;
                try { store.TryTransitionSubmissionBudget(before!, SubmissionBudgetState.Concluded); } catch (IOException) { transitionFailed = true; }
                Require(transitionFailed, "转换绕过了共享独占锁");
            }
            clock.Advance(TimeSpan.FromSeconds(120));
            var path = Path.Combine(directory, "submission-budget.json");
            var original = File.ReadAllBytes(path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Require(store.TryReserveSubmissionBudget(before, clock.GetUtcNow()).Outcome == SubmissionReservationOutcome.StorageUnavailable,
                    "持久替换失败仍返回预留成功");
            Require(File.ReadAllBytes(path).SequenceEqual(original) && store.LoadSubmissionBudget() == before, "写失败覆盖原记录");
            Require(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "原子写失败遗留临时记录");
            var retry = store.TryReserveSubmissionBudget(before, clock.GetUtcNow());
            Require(retry.Outcome == SubmissionReservationOutcome.Reserved && retry.Budget?.Attempts == 2, "存储恢复后重复扣额或无法预留");
        })),
        ("提交额度：两个独立进程同时预留仅一次成功且进程退出后活动状态保留", CrossProcessAsync),
        ("提交额度：不可信时刻不能生成无法读取的预留记录", () => Run(directory =>
        {
            var store = new SafetyStateStore(directory);
            foreach (var now in new[] { default(DateTimeOffset), DateTimeOffset.MaxValue, DateTimeOffset.MaxValue.AddSeconds(-119) })
            {
                Require(store.TryReserveSubmissionBudget(null, now).Outcome == SubmissionReservationOutcome.InvalidState, "无效时刻获得了预留");
                Require(store.LoadSubmissionBudget() is null, "无效时刻留下不可读记录");
            }
        })),
        ("提交额度：零次结束记录可作为新起点且文件仅包含六项非秘密字段", () => Run(directory =>
        {
            var path = Path.Combine(directory, "submission-budget.json");
            File.WriteAllText(path, """
                {"Version":1,"State":"Concluded","Attempts":0,"ReservedAt":"0001-01-01T00:00:00Z","NextPossibleAt":"0001-01-01T00:00:00Z","Revision":"11111111-2222-3333-4444-555555555555"}
                """);
            var store = new SafetyStateStore(directory);
            var initial = store.LoadSubmissionBudget();
            var now = new ManualClock().GetUtcNow().ToOffset(TimeSpan.FromHours(8));
            var result = store.TryReserveSubmissionBudget(initial, now);
            Require(result.Outcome == SubmissionReservationOutcome.Reserved && result.Budget?.Attempts == 1, "合法零次记录不能预留");
            Require(result.Budget!.ReservedAt.Offset == TimeSpan.Zero && result.Budget.ReservedAt == now, "持久时刻未规范为同一UTC瞬间");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var fields = json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
            Require(fields.SequenceEqual(new[] { "Attempts", "NextPossibleAt", "ReservedAt", "Revision", "State", "Version" }),
                "提交额度包含规格以外的凭据或门户字段");
            var current = store.LoadSubmissionBudget()!;
            Require(!store.TryTransitionSubmissionBudget(current with { Attempts = 2 }, SubmissionBudgetState.PossiblySent),
                "仅靠相同Revision接受了被改写的预期记录");
            Require(!store.TryTransitionSubmissionBudget(current, (SubmissionBudgetState)99), "未知转换状态被持久化");
            Require(store.LoadSubmissionBudget() == current, "非法转换污染了可读记录");
        }))
    ];

    private static Task Run(Action<string> action)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "submission-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
        return Task.CompletedTask;
    }

    private static async Task CrossProcessAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "submission-budget-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var eventName = @"Local\AutoNet.SubmissionBudget.Test." + Guid.NewGuid().ToString("N");
        using var gate = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var now = new ManualClock().GetUtcNow();
        var children = new List<Process>();
        try
        {
            children.Add(StartPeer(directory, "reserve", now, eventName));
            children.Add(StartPeer(directory, "reserve", now, eventName));
            var ready = await Task.WhenAll(children.Select(child => child.StandardOutput.ReadLineAsync())).WaitAsync(TimeSpan.FromSeconds(10));
            Require(ready.All(line => line == "READY"), "竞争进程未准备完成");
            var outputs = children.Select(child => child.StandardOutput.ReadToEndAsync()).ToArray();
            gate.Set();
            await Task.WhenAll(children.Select(child => child.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(10));
            var outcomes = await Task.WhenAll(outputs);
            Require(children.All(child => child.ExitCode == 0) && outcomes.Count(text => text.Trim() == "Reserved") == 1,
                "两个进程竞争没有恰好一个成功");
            Require(outcomes.All(text => text.Trim() is "Reserved" or "StateChanged" or "StorageUnavailable"), "竞争失败未保守拒绝");
            var record = new SafetyStateStore(directory).LoadSubmissionBudget();
            Require(record is { State: SubmissionBudgetState.Reserved, Attempts: 1 }, "竞争后计数或活动状态错误");
            var original = File.ReadAllBytes(Path.Combine(directory, "submission-budget.json"));
            var restarted = StartPeer(directory, "restart", now.AddSeconds(1), "-");
            children.Add(restarted);
            var result = await restarted.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await restarted.WaitForExitAsync();
            Require(restarted.ExitCode == 0 && result.Trim() == "Reserved:1:InFlight", "进程退出后免费获得新额度");
            Require(File.ReadAllBytes(Path.Combine(directory, "submission-budget.json")).SequenceEqual(original), "重启读取改写了活动记录");
        }
        finally
        {
            foreach (var child in children)
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                child.Dispose();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Process StartPeer(string directory, string mode, DateTimeOffset now, string eventName)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(SubmissionBudgetRegression).Assembly.Location);
        foreach (var argument in new[] { "--submission-budget-test-peer", directory, mode, now.ToString("O", CultureInfo.InvariantCulture), eventName })
            info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new Exception("无法启动提交额度验证进程");
    }

    public static int RunPeer(string[] args)
    {
        try
        {
            var store = new SafetyStateStore(args[1]);
            var now = DateTimeOffset.ParseExact(args[3], "O", CultureInfo.InvariantCulture);
            if (args[2] == "reserve")
            {
                using var gate = EventWaitHandle.OpenExisting(args[4]);
                Console.WriteLine("READY");
                Console.Out.Flush();
                if (!gate.WaitOne(TimeSpan.FromSeconds(10))) return 2;
                Console.WriteLine(store.TryReserveSubmissionBudget(null, now).Outcome);
            }
            else
            {
                var record = store.LoadSubmissionBudget()!;
                Console.WriteLine($"{record.State}:{record.Attempts}:{store.TryReserveSubmissionBudget(record, now).Outcome}");
            }
            return 0;
        }
        catch { return 2; }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
