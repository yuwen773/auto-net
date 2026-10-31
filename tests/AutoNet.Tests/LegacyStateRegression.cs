using System.Text.Json;
using AutoNet.Core;
using AutoNet.Service;

/// <summary>
/// #36 旧数据兼容与升级回滚：历史认证记录形态、旧记录升级为当前记录，以及回滚后
/// 旧版本读到新记录时的保守行为。全部使用隔离临时目录与假时钟，不接触真实安全状态、
/// 凭据或门户。
/// </summary>
internal static class LegacyStateRegression
{
    // 旧版认证暂停标记曾以 64 位十六进制指纹落盘；该形状必须永远按人工暂停处理。
    private static readonly string LegacyFingerprint = new('A', 64);

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("旧数据兼容：64位指纹记录仍按人工暂停且零提交零额度", () => WithStore((store, directory) =>
        {
            foreach (var fingerprint in new[] { LegacyFingerprint, LegacyFingerprint.ToLowerInvariant(), new string('0', 64) })
            {
                var path = WriteAuthenticationRecord(directory, fingerprint);
                RunBlocked(store);
                Require(store.LastAuthenticationRecordKind == AuthenticationRecordKind.LegacyFingerprintShape, "指纹形状未被识别为历史记录");
                Require(File.ReadAllText(path) == fingerprint, "运行或升级改写了旧指纹证据");
                RequireNoSubmissionQuota(store, directory);
            }
        })),
        ("旧数据兼容：失败-only记录仍按人工暂停且不写额度", () => WithStore((store, directory) =>
        {
            foreach (var failure in new[]
                     {
                         PortalFailure.CredentialsRejected, PortalFailure.BindingRejected,
                         PortalFailure.InteractionRequired, PortalFailure.MissingGatewayParameters,
                         PortalFailure.SafetyPersistence, PortalFailure.None
                     })
            {
                var content = JsonSerializer.Serialize(new { Failure = (int)failure }, Storage.Json);
                var path = WriteAuthenticationRecord(directory, content);
                var legacy = store.LoadAuthenticationBlock();
                Require(legacy is { BlocksSubmissions: true, Failure: var legacyFailure } && legacyFailure == failure,
                    "失败-only记录未按人工暂停处理：" + failure);
                Require(store.LastAuthenticationRecordKind == AuthenticationRecordKind.LegacyRecord, "失败-only记录被误判为其他形态");
                RunBlocked(store);
                Require(File.ReadAllText(path) == content, "运行改写了失败-only记录证据");
                RequireNoSubmissionQuota(store, directory);
            }
            // 字符串枚举与越界失败值都无法被旧格式解释，同样必须保守阻断。
            foreach (var content in new[] { "{\"Failure\":\"CredentialsRejected\"}", "{\"Failure\":99}" })
            {
                WriteAuthenticationRecord(directory, content);
                Require(store.LoadAuthenticationBlock()?.BlocksSubmissions == true, "无法解释的失败值被放行：" + content);
                RunBlocked(store);
                RequireNoSubmissionQuota(store, directory);
            }
        })),
        ("旧数据兼容：缺字段未知版本损坏JSON均保守阻断且不获得新额度", () => WithStore((store, directory) =>
        {
            var missingRevision = JsonSerializer.Serialize(new
            {
                Version = 1, State = 1, Failure = 0, Attempts = 1,
                RetryDelaySeconds = 0, RetryNotBefore = default(DateTimeOffset)
            }, Storage.Json);
            var matrix = new (string Content, AuthenticationRecordKind Kind)[]
            {
                (missingRevision, AuthenticationRecordKind.MissingFields),
                ("{\"Version\":1}", AuthenticationRecordKind.MissingFields),
                ("{\"Version\":1,\"Version\":1}", AuthenticationRecordKind.MissingFields),
                (FullRecord(version: 99), AuthenticationRecordKind.UnsupportedVersion),
                (FullRecord(state: 42), AuthenticationRecordKind.InvalidState),
                (FullRecord(attempts: 4), AuthenticationRecordKind.InvalidState),
                ("{broken", AuthenticationRecordKind.InvalidJson),
                ("", AuthenticationRecordKind.InvalidJson),
                ("{\"Version\":1,\"State\":\"Ready\",\"Failure\":0,\"Attempts\":1,\"RetryDelaySeconds\":0,"
                    + "\"RetryNotBefore\":\"0001-01-01T00:00:00+00:00\",\"Revision\":\"00000000-0000-0000-0000-000000000000\"}",
                    AuthenticationRecordKind.InvalidJson),
                ("{\"Version\":1,\"State\":1,\"Failure\":0,\"Attempts\":\"1\",\"RetryDelaySeconds\":0,"
                    + "\"RetryNotBefore\":\"0001-01-01T00:00:00+00:00\",\"Revision\":\"00000000-0000-0000-0000-000000000000\"}",
                    AuthenticationRecordKind.InvalidJson),
                ("[]", AuthenticationRecordKind.NonObject),
                ("0", AuthenticationRecordKind.NonObject),
                ("\"ready\"", AuthenticationRecordKind.NonObject)
            };
            foreach (var (content, kind) in matrix)
            {
                var path = WriteAuthenticationRecord(directory, content);
                Require(store.LoadAuthenticationBlock()?.BlocksSubmissions == true, "损坏记录被放行：" + content);
                Require(store.LastAuthenticationRecordKind == kind, "记录形态识别不符：" + content + " -> " + store.LastAuthenticationRecordKind);
                RunBlocked(store);
                Require(File.ReadAllText(path) == content, "运行改写了损坏记录证据：" + content);
                RequireNoSubmissionQuota(store, directory);
            }
        })),
        ("旧数据兼容：升级路径旧记录转新记录后阻断保持", () => WithStore((store, directory) =>
        {
            var path = WriteAuthenticationRecord(directory, LegacyFingerprint);
            var legacy = store.LoadAuthenticationBlock();
            Require(legacy?.BlocksSubmissions == true, "旧指纹在升级前未阻断");
            // 外网已恢复时旧记录也不能被当作就绪而写回；升级不得悄悄放宽阻断。
            var online = new MonitorFixture { Http = { Online = true } };
            using (var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(online, store), online.Clock))
                Require(worker.RunOnceAsync(default).GetAwaiter().GetResult().CredentialsBlocked, "外网在线时旧记录被放宽");
            Require(File.ReadAllText(path) == LegacyFingerprint, "在线轮次改写了旧记录");
            // 升级写入失败时旧记录原样保留，阻断不变。
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    store.TryTransitionAuthentication(legacy, new AuthenticationBlock(PortalFailure.BindingRejected) { Attempts = 1, Revision = Guid.NewGuid() });
                    throw new Exception("锁定期间仍完成升级写入");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            Require(store.LoadAuthenticationBlock() == legacy && File.ReadAllText(path) == LegacyFingerprint, "升级写入失败改变了旧记录");
            var upgraded = new AuthenticationBlock(PortalFailure.BindingRejected) { Attempts = 3, Revision = Guid.NewGuid() };
            Require(store.TryTransitionAuthentication(legacy, upgraded), "旧记录无法升级为当前记录");
            Require(store.LoadAuthenticationBlock() == upgraded && upgraded.BlocksSubmissions, "升级后阻断被放宽");
            Require(File.ReadAllText(path) != LegacyFingerprint, "升级未写入当前记录");
            RunBlocked(new SafetyStateStore(directory));
            Require(store.LoadAuthenticationBlock() == upgraded, "升级重启后阻断被改写");
            RequireNoSubmissionQuota(store, directory);

            // 失败-only 记录升级时保留原始失败分类，不得降级为无原因的通用阻断。
            var failureOnly = JsonSerializer.Serialize(new { Failure = (int)PortalFailure.CredentialsRejected }, Storage.Json);
            WriteAuthenticationRecord(directory, failureOnly);
            var legacyFailure = store.LoadAuthenticationBlock();
            Require(legacyFailure is { State: AuthenticationState.CredentialsRejected, BlocksSubmissions: true },
                "失败-only记录升级前丢失原因");
            var upgradedFailure = new AuthenticationBlock(PortalFailure.CredentialsRejected) { Attempts = 1, Revision = Guid.NewGuid() };
            Require(store.TryTransitionAuthentication(legacyFailure, upgradedFailure), "失败-only记录无法升级");
            Require(store.LoadAuthenticationBlock() == upgradedFailure && upgradedFailure.BlocksSubmissions, "失败-only升级后阻断被放宽");
            RequireNoSubmissionQuota(store, directory);
        })),
        ("旧数据兼容：回滚时旧版读到新提交未知记录仍拒绝重复提交", () => WithStore((store, directory) =>
        {
            // 结果未知是唯一可能已发出凭据的中间态；回滚后的旧版本不读独立额度文件，
            // 因此阻断必须完整落在 auth-block.txt 里。
            var fixture = new MonitorFixture { Http = { LoginResult = "unparseable" } };
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            worker.RunOnceAsync(default).GetAwaiter().GetResult();
            var pending = store.LoadAuthenticationBlock();
            var budget = store.LoadSubmissionBudget();
            Require(pending?.State == AuthenticationState.SubmissionPending && budget is { Attempts: 1 }, "夹具未进入提交结果未知");
            var path = Path.Combine(directory, "auth-block.txt");
            var released = ReadAsReleasedV031(File.ReadAllText(path));
            Require(released is { BlocksSubmissions: true, State: AuthenticationState.SubmissionPending } &&
                released.Attempts == pending!.Attempts, "旧版本错误读取了提交未知记录");
        })),
        ("旧数据兼容：回滚时旧版读取新终态与冷却记录不越权", () => WithStore((store, directory) =>
        {
            var path = Path.Combine(directory, "auth-block.txt");
            foreach (var block in new[]
                     {
                         new AuthenticationBlock(PortalFailure.BindingRejected) { Attempts = 1, Revision = Guid.NewGuid() },
                         new AuthenticationBlock(PortalFailure.CredentialsRejected) { Attempts = 2, Revision = Guid.NewGuid() },
                         new AuthenticationBlock(PortalFailure.InteractionRequired)
                             { Attempts = 3, Revision = Guid.NewGuid(), ChannelFallbackCause = PortalFailure.MissingGatewayParameters },
                         new AuthenticationBlock(PortalFailure.SafetyPersistence) { Attempts = 0, Revision = Guid.NewGuid() },
                         new AuthenticationBlock(PortalFailure.None)
                             { State = AuthenticationState.SafetyBlocked, Attempts = 3, Revision = Guid.NewGuid() }
                     })
            {
                store.BlockAuthentication(block);
                var released = ReadAsReleasedV031(File.ReadAllText(path));
                Require(released is { BlocksSubmissions: true } && released.Attempts == block.Attempts,
                    "旧版本放行或改写了新阻断记录：" + block.State + "/" + block.Failure);
            }
            // 冷却记录表示预检失败、尚未发出凭据；旧版本可继续，但不得凭空增加次数。
            store.BlockAuthentication(new(PortalFailure.Timeout)
            {
                State = AuthenticationState.RetryCoolingDown, Attempts = 2, RetryDelaySeconds = 60,
                RetryNotBefore = DateTimeOffset.UtcNow.AddSeconds(60), Revision = Guid.NewGuid()
            });
            var cooling = ReadAsReleasedV031(File.ReadAllText(path));
            Require(cooling is { BlocksSubmissions: false, State: AuthenticationState.RetryCoolingDown, Attempts: 2 },
                "旧版本误读冷却记录或增加额度");
            store.BlockAuthentication(new() { State = AuthenticationState.Ready, Attempts = 2, Revision = Guid.NewGuid() });
            var ready = ReadAsReleasedV031(File.ReadAllText(path));
            Require(ready is { BlocksSubmissions: false, Attempts: 2 }, "旧版本误读就绪记录");
            // 冻结读取器必须与 v0.3.1 一样把无法解析的输入当作阻断，而不是抛异常。
            Require(ReadAsReleasedV031("{broken")?.BlocksSubmissions == true, "旧版本读取损坏记录时未阻断");
            Require(ReadAsReleasedV031("{\"Failure\":\"CredentialsRejected\"}")?.BlocksSubmissions == true,
                "旧版本读取字符串枚举时未阻断");
        })),
        ("旧数据兼容：回滚后遗留耗尽额度不因人工解除获得新额度", () => WithStore((store, directory) =>
        {
            // 模拟新版本已用完三次提交后回滚：旧版本只解除 auth-block，不读也不改独立额度。
            store.BlockAuthentication(new(PortalFailure.CredentialsRejected) { Attempts = 3, Revision = Guid.NewGuid() });
            var now = new ManualClock().GetUtcNow();
            var budget = store.TryReserveSubmissionBudget(null, now).Budget!;
            Require(store.TryTransitionSubmissionBudget(budget, SubmissionBudgetState.PossiblySent), "首次额度推进失败");
            budget = store.TryReserveSubmissionBudget(store.LoadSubmissionBudget(), now.AddMinutes(3)).Budget!;
            Require(store.TryTransitionSubmissionBudget(budget, SubmissionBudgetState.PossiblySent), "第二次额度推进失败");
            budget = store.TryReserveSubmissionBudget(store.LoadSubmissionBudget(), now.AddMinutes(6)).Budget!;
            Require(store.TryTransitionSubmissionBudget(budget, SubmissionBudgetState.Concluded), "第三次额度推进失败");
            var exhausted = store.LoadSubmissionBudget();
            Require(exhausted is { Attempts: 3, State: SubmissionBudgetState.Concluded }, "额度未按三次结束");
            store.ClearAuthenticationBlock(store.LoadAuthenticationBlock());
            Require(store.LoadAuthenticationBlock() is null && store.LoadSubmissionBudget() == exhausted, "解除暂停改动了额度");
            var fixture = new MonitorFixture();
            using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
            fixture.Clock.Advance(TimeSpan.FromHours(1));
            var status = worker.RunOnceAsync(default).GetAwaiter().GetResult();
            Require(LoginRequests(fixture) == 0, "回滚遗留的耗尽额度被重置并再次提交");
            Require(store.LoadSubmissionBudget() == exhausted && !status.CredentialRequestStarted, "耗尽额度未保持");
        }))
    ];

    private static string FullRecord(int version = 1, int state = 0, int failure = 0, int attempts = 1) =>
        JsonSerializer.Serialize(new
        {
            Version = version, State = state, Failure = failure, Attempts = attempts,
            RetryDelaySeconds = 0, RetryNotBefore = default(DateTimeOffset), Revision = Guid.NewGuid()
        }, Storage.Json);

    private static string WriteAuthenticationRecord(string directory, string content)
    {
        var path = Path.Combine(directory, "auth-block.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static void RunBlocked(SafetyStateStore store)
    {
        var fixture = new MonitorFixture();
        using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
        var status = worker.RunOnceAsync(default).GetAwaiter().GetResult();
        Require(status.CredentialsBlocked && !status.CredentialRequestStarted, "历史记录未按人工暂停处理");
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        worker.RunOnceAsync(default).GetAwaiter().GetResult();
        Require(LoginRequests(fixture) == 0, "历史记录存在时发出了凭据提交");
    }

    private static void RequireNoSubmissionQuota(SafetyStateStore store, string directory)
    {
        Require(store.LoadSubmissionBudget() is null, "历史记录凭空获得了提交额度");
        Require(!File.Exists(Path.Combine(directory, "submission-budget.json")), "历史记录写入了提交额度文件");
    }

    /// <summary>
    /// 已发布 v0.3.1（tag <c>v0.3.1</c>，2026-10-17）的认证记录读取规则冻结副本，
    /// 逐行对应 <c>git show v0.3.1:src/AutoNet.Core/SafetyStateStore.cs</c> 的
    /// <c>ReadAuthenticationBlock</c>，仅用于回滚验收：它决定“旧版本读到新记录”时是否阻断。
    /// 字段集合与枚举值固定为旧版本认知；当前版本新增的字段（如
    /// <c>ChannelFallbackCause</c>）会被旧版本忽略，未知版本或未知状态按旧版本语义阻断。
    /// 反序列化复用当前 <see cref="AuthenticationBlock"/> 类型：字段改名会让这里读不到旧字段
    /// 而按缺字段阻断，新增枚举值由 <see cref="V031States"/> / <see cref="V031Failures"/> 快照拒绝。
    /// 无法解析的输入与 v0.3.1 一样捕获 <see cref="JsonException"/> 并返回阻断。
    /// </summary>
    private static AuthenticationBlock? ReadAsReleasedV031(string content)
    {
        if (content.Length == 64 && content.All(Uri.IsHexDigit)) return new();
        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new();
            if (!root.TryGetProperty("Version", out _))
            {
                var legacy = JsonSerializer.Deserialize<AuthenticationBlock>(root, Storage.Json);
                return legacy is not null && V031Failures.Contains((int)legacy.Failure) ? new(legacy.Failure) : new();
            }
            string[] fields = ["Version", "State", "Failure", "Attempts", "RetryDelaySeconds", "RetryNotBefore", "Revision"];
            if (fields.Any(name => root.EnumerateObject().Count(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) != 1))
                return new();
            var block = JsonSerializer.Deserialize<AuthenticationBlock>(root, Storage.Json);
            if (block is null || block.Version != 1 || !V031States.Contains((int)block.State) || !V031Failures.Contains((int)block.Failure) ||
                block.Attempts is < 0 or > 3 || block.State == AuthenticationState.SubmissionPending && block.Attempts == 0 ||
                block.State == AuthenticationState.RetryCoolingDown &&
                    (block.RetryDelaySeconds is < 2 or > 60 || block.RetryNotBefore == default) ||
                block.State != AuthenticationState.RetryCoolingDown && (block.RetryDelaySeconds != 0 || block.RetryNotBefore != default))
                return new();
            var consistent = block.State switch
            {
                AuthenticationState.Ready or AuthenticationState.SubmissionPending => block.Failure == PortalFailure.None,
                AuthenticationState.RetryCoolingDown => block.Attempts > 0 && block.Failure is
                    PortalFailure.None or PortalFailure.HttpStatus or PortalFailure.MissingClientIpv4 or PortalFailure.MissingGatewayParameters or
                    PortalFailure.Timeout or PortalFailure.Transport or PortalFailure.StatusUnknown or PortalFailure.LoginRejected or PortalFailure.InvalidTarget,
                AuthenticationState.BindingRejected => block.Failure == PortalFailure.BindingRejected,
                AuthenticationState.CredentialsRejected => block.Failure == PortalFailure.CredentialsRejected,
                AuthenticationState.InteractionRequired => block.Failure == PortalFailure.InteractionRequired,
                _ => true
            };
            return consistent ? block : new();
        }
        catch (JsonException) { return new(); }
    }

    // v0.3.1 的枚举值快照：未来新增状态或失败分类在旧版本眼中必须仍是未知值。
    private static readonly HashSet<int> V031States = [0, 1, 2, 3, 4, 5, 6];
    private static readonly HashSet<int> V031Failures = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];

    private static int LoginRequests(MonitorFixture fixture) =>
        fixture.Http.Requests.Count(u => u.AbsolutePath == "/eportal/portal/login");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static Task WithStore(Action<SafetyStateStore, string> run)
    {
        AcceptanceRegression.WithStore(run);
        return Task.CompletedTask;
    }
}
