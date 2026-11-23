using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AutoNet.Core;

/// <summary>
/// P0 跨语言黄金夹具导出器（ADR-0022）。只调用纯策略与只读存储解释，使用隔离临时目录，
/// 不读写真实 ProgramData、不接触凭据或网络。输出必须可重复：同一提交导出字节应一致。
/// </summary>
internal static class GoldenFixtureExport
{
    public const string BaselineCommit = "43c3b0e4b23f0b3f0c080de8945664add7182a1d";
    private const long Base = 1767225600000; // 2026-01-01T00:00:00Z

    private static readonly JsonSerializerOptions Output = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("黄金夹具导出可重复且默认不含本机密文", () =>
        {
            var first = TempDirectory();
            var second = TempDirectory();
            try
            {
                Require(Export(first, false) == 0, "首次导出失败");
                Require(Export(second, false) == 0, "第二次导出失败");
                foreach (var name in new[] { "recovery-policy.json", "authentication-block.json", "submission-budget.json", "status-log-projection.json", "manifest.json" })
                    Require(File.ReadAllText(Path.Combine(first, name)) == File.ReadAllText(Path.Combine(second, name)),
                        "同一提交的导出不确定：" + name);
                Require(!File.Exists(Path.Combine(first, "dpapi-credential.json")), "未请求时仍写出了本机密文");
                Require(!File.ReadAllText(Path.Combine(first, "recovery-policy.json")).Contains("fixture-password", StringComparison.Ordinal),
                    "恢复夹具意外包含测试密码");
            }
            finally { TryDelete(first); TryDelete(second); }
            return Task.CompletedTask;
        }),
        ("黄金夹具覆盖恢复策略、历史状态与提交额度三类判定", () =>
        {
            var directory = TempDirectory();
            try
            {
                Require(Export(directory, false) == 0, "导出失败");
                using var recovery = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "recovery-policy.json")));
                var actions = recovery.RootElement.GetProperty("cases").EnumerateArray()
                    .SelectMany(item => item.GetProperty("steps").EnumerateArray())
                    .Select(step => step.TryGetProperty("action", out var action) ? action.GetString() : null)
                    .Where(action => action is not null).ToHashSet(StringComparer.Ordinal);
                foreach (var action in new[] { "None", "ReconnectWifi", "Authenticate", "DisconnectWifi" })
                    Require(actions.Contains(action), "恢复夹具缺少动作：" + action);
                var reasons = recovery.RootElement.GetProperty("cases").EnumerateArray()
                    .SelectMany(item => item.GetProperty("steps").EnumerateArray())
                    .Select(step => step.GetProperty("expect").GetProperty("reason").GetString()).ToHashSet(StringComparer.Ordinal);
                foreach (var reason in new[] { "DisconnectBudgetExhausted", "DisconnectCompletionUnconfirmed", "InsufficientFailureSamples", "Online", "RecoveryDisabled" })
                    Require(reasons.Contains(reason), "恢复夹具缺少判定原因：" + reason);
                Require(recovery.RootElement.GetProperty("cases").GetArrayLength() >= 12, "恢复夹具用例不足");

                using var authentication = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "authentication-block.json")));
                var kinds = authentication.RootElement.GetProperty("cases").EnumerateArray()
                    .Select(item => item.GetProperty("expect").GetProperty("kind").GetString()).ToHashSet(StringComparer.Ordinal);
                foreach (var kind in new[] { "LegacyFingerprintShape", "LegacyRecord", "MissingFields", "UnsupportedVersion", "InvalidState", "InvalidJson", "NonObject", "CurrentRecord", "InconsistentState" })
                    Require(kinds.Contains(kind), "历史状态夹具缺少记录形态：" + kind);
                Require(authentication.RootElement.GetProperty("cases").GetArrayLength() >= 30, "历史状态夹具用例不足");

                using var budget = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "submission-budget.json")));
                var outcomes = budget.RootElement.GetProperty("cases").EnumerateArray()
                    .Select(item => item.GetProperty("expect").GetProperty("outcome").GetString()).ToHashSet(StringComparer.Ordinal);
                foreach (var outcome in new[] { "Missing", "Valid", "Invalid" })
                    Require(outcomes.Contains(outcome), "提交额度夹具缺少判定：" + outcome);
                Require(budget.RootElement.GetProperty("cases").GetArrayLength() >= 15, "提交额度夹具用例不足");
            }
            finally { TryDelete(directory); }
            return Task.CompletedTask;
        })
    ];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static int Export(string directory, bool includeDpapi)
    {
        Directory.CreateDirectory(directory);
        var recovery = BuildRecovery();
        var auth = BuildAuthentication();
        var budget = BuildSubmissionBudget();
        var files = new List<ManifestFile>
        {
            new("recovery-policy.json", "autonet.golden.recovery/1", recovery.Cases.Count),
            new("authentication-block.json", "autonet.golden.authentication-block/1", auth.Cases.Count),
            new("submission-budget.json", "autonet.golden.submission-budget/1", budget.Cases.Count),
            new("status-log-projection.json", "autonet.golden.projection/1", 2)
        };
        Write(directory, "recovery-policy.json", recovery);
        Write(directory, "authentication-block.json", auth);
        Write(directory, "submission-budget.json", budget);
        Write(directory, "status-log-projection.json", BuildProjection());
        if (includeDpapi)
        {
            Write(directory, "dpapi-credential.json", BuildDpapi());
            files.Add(new("dpapi-credential.json", "autonet.golden.dpapi/1", 1));
        }
        Write(directory, "manifest.json", new ManifestDocument(
            "autonet.golden.manifest/1", BaselineCommit,
            "tests/AutoNet.Tests/bin/Release/net10.0-windows/AutoNet.Tests.dll --export-golden-fixtures <dir> [--dpapi]",
            files,
            [
                "install-lifecycle.json 为手工维护，来源是 scripts/install.ps1；qa-crosslang-parity.ps1 会逐项核对。",
                "status-log-projection.json 由 C# 的 StatusSnapshot/ActivityLog 序列化结果组成，并刻意包含敏感哨兵；Rust 投影不得带出这些哨兵。",
                "dpapi-credential.json 只在显式传入 --dpapi 时重写；密文随机且绑定本机，其他机器的解密用例必须跳过。",
                "所有时间均为 Unix 毫秒；C# 的 DateTimeOffset.MinValue 不出现，缺省时刻用 null 表示。"
            ]));
        return 0;
    }

    private static void Write<T>(string directory, string name, T value) =>
        // 固定 LF：JsonSerializer 的缩进使用 Environment.NewLine，混入 CRLF 会让
        // .gitattributes 归一化后的仓库副本与 CI 现场导出无法逐字节一致。
        File.WriteAllText(Path.Combine(directory, name),
            JsonSerializer.Serialize(value, Output).Replace("\r\n", "\n") + "\n");

    // ---------- 恢复策略 ----------

    private sealed record RecoveryFixture(string Schema, RecoveryConstants Constants, IReadOnlyList<RecoveryCase> Cases);
    private sealed record RecoveryConstants(
        int FastRetrySeconds, int FastRetryWindowSeconds, int BackoffCeilingSeconds,
        int DisconnectCooldownSeconds, int DisconnectAttemptCap, int StalledRoundThreshold,
        int StalledObservationSeconds, int OnlineResetSeconds, int AuthenticationAttemptCap);
    private sealed record RecoveryCase(string Name, IReadOnlyList<RecoveryStep> Steps);
    private sealed record RecoveryStep(
        string Op, long Now, string? Wifi, bool? DirectOnline, string? Portal,
        bool? Enabled, bool? HasCredentials, bool? AllowActiveDisconnect,
        int? BudgetAttempts, string? BudgetState, long? BudgetCompletedAt, long? DeferUntil,
        bool? CredentialsBlocked, string? Action, RecoveryExpect Expect);
    private sealed record RecoveryExpect(
        string Reason, int Attempts, int DisconnectAttempts, long? NextAttempt,
        string NextAttemptAction, long? OutageSince, int FailedRounds, double ObservationSeconds,
        bool CredentialsBlocked);

    private static RecoveryFixture BuildRecovery()
    {
        var cases = new List<RecoveryCase>();
        void Add(string name, Action<RecoveryRun> build)
        {
            var run = new RecoveryRun();
            build(run);
            cases.Add(new RecoveryCase(name, run.Steps));
        }

        Add("联网稳定时不执行任何动作", run =>
            run.Observe(0, WifiState.Ready, true, PortalState.Unknown, true, true, true));

        Add("断连优先重连，快重试 2 秒，第 4 次后 60 秒封顶", run =>
        {
            run.Observe(0, WifiState.Disconnected, false, PortalState.Unknown, true, true, true);
            run.Attempted(0);
            run.Observe(1, WifiState.Disconnected, false, PortalState.Unknown, true, true, true);
            run.Observe(2, WifiState.Disconnected, false, PortalState.Unknown, true, true, true);
            run.Attempted(2);
            run.Observe(4, WifiState.Disconnected, false, PortalState.Unknown, true, true, true);
            run.Attempted(4);
            run.Observe(24, WifiState.Disconnected, false, PortalState.Unknown, true, true, true);
            run.Attempted(24);
            run.Observe(84, WifiState.Disconnected, false, PortalState.Unknown, true, true, true);
            run.Attempted(84);
            run.Observe(144, WifiState.Disconnected, false, PortalState.Unknown, true, true, true);
        });

        Add("确认需要认证时立即提交，冷却期不重复", run =>
        {
            run.Observe(0, WifiState.Ready, false, PortalState.NeedsLogin, true, true, true);
            run.Attempted(0);
            run.Observe(1, WifiState.Ready, false, PortalState.NeedsLogin, true, true, true);
            run.Observe(2, WifiState.Ready, false, PortalState.NeedsLogin, true, true, true);
        });

        Add("已认证但上游故障不提交密码", run =>
            run.Observe(0, WifiState.Ready, false, PortalState.LoggedIn, true, true, true));

        Add("门户未知时连续 3 轮且满 30 秒才主动断线", run =>
        {
            run.Observe(0, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.Observe(10, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.Observe(30, WifiState.Ready, false, PortalState.Unknown, true, true, true);
        });

        Add("主动断线完成前保持未确认，完成后按 120 秒冷却累计 3 次上限", run =>
        {
            run.Observe(0, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.Observe(30, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.Observe(60, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.Attempted(60);
            run.Observe(61, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.DisconnectCompleted(65, 1);
            run.Observe(185, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.Attempted(185);
            run.DisconnectCompleted(190, 2);
            run.Observe(310, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.Attempted(310);
            run.DisconnectCompleted(315, 3);
            run.Observe(430, WifiState.Ready, false, PortalState.Unknown, true, true, true);
        });

        Add("恢复在线连续满 60 秒才重置断线额度", run =>
        {
            run.RestoreBudget(0, 2, "Completed", 100);
            run.Observe(0, WifiState.Ready, true, PortalState.Unknown, true, true, true);
            run.Observe(60, WifiState.Ready, true, PortalState.Unknown, true, true, true);
        });

        Add("重启后未确认的断线请求保持阻断", run =>
        {
            run.RestoreBudget(0, 1, "RequestPending", 0);
            run.Observe(0, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.Observe(30, WifiState.Ready, false, PortalState.Unknown, true, true, true);
        });

        Add("恢复禁用、凭据缺失与暂停时都不动作", run =>
        {
            run.Observe(0, WifiState.Ready, false, PortalState.NeedsLogin, false, true, true);
            run.Observe(0, WifiState.Ready, false, PortalState.NeedsLogin, true, false, true);
            run.SetBlocked(true);
            run.Observe(0, WifiState.Ready, false, PortalState.NeedsLogin, true, true, true);
        });

        Add("关闭主动断线后门户未知不触发断线", run =>
            run.Observe(0, WifiState.Ready, false, PortalState.Unknown, true, true, false));

        Add("认证退避可被显式推迟", run =>
        {
            run.Observe(0, WifiState.Ready, false, PortalState.NeedsLogin, true, true, true);
            run.Attempted(0);
            run.DeferAuthentication(120);
            run.Observe(2, WifiState.Ready, false, PortalState.NeedsLogin, true, true, true);
            run.Observe(120, WifiState.Ready, false, PortalState.NeedsLogin, true, true, true);
        });

        Add("观察中断清空连续失败采样", run =>
        {
            run.Observe(0, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.Observe(10, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.InterruptObservation();
            run.Observe(40, WifiState.Ready, false, PortalState.Unknown, true, true, true);
        });

        Add("撤销未发出的断线预留后重新可请求", run =>
        {
            run.Observe(0, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.Observe(30, WifiState.Ready, false, PortalState.Unknown, true, true, true);
            run.Attempted(30);
            run.CancelUnissuedReservation();
            run.Observe(31, WifiState.Ready, false, PortalState.Unknown, true, true, true);
        });

        return new RecoveryFixture("autonet.golden.recovery/1", new RecoveryConstants(
            RecoveryPolicy.FastRetrySeconds, RecoveryPolicy.FastRetryWindowSeconds, RecoveryPolicy.BackoffCeilingSeconds,
            RecoveryPolicy.DisconnectCooldownSeconds, RecoveryPolicy.DisconnectAttemptCap, RecoveryPolicy.StalledRoundThreshold,
            RecoveryPolicy.StalledObservationSeconds, RecoveryPolicy.OnlineResetSeconds, RecoveryPolicy.AuthenticationAttemptCap), cases);
    }

    private sealed class RecoveryRun
    {
        private readonly RecoveryPolicy policy = new();
        public List<RecoveryStep> Steps { get; } = [];

        public void Observe(int offset, WifiState wifi, bool direct, PortalState portal, bool enabled, bool credentials, bool allowDisconnect)
        {
            var now = Base + offset * 1000L;
            var action = policy.Decide(new Observation(wifi, direct, portal), At(now), enabled, credentials, allowDisconnect);
            Steps.Add(new RecoveryStep("observe", now, wifi.ToString(), direct, portal.ToString(), enabled, credentials, allowDisconnect,
                null, null, null, null, null, action.ToString(), Snapshot(now)));
        }

        public void Attempted(int offset)
        {
            var now = Base + offset * 1000L;
            policy.Attempted(At(now));
            Steps.Add(Mutation("attempted", now));
        }

        public void RestoreBudget(int offset, int attempts, string state, int completedOffset)
        {
            var now = Base + offset * 1000L;
            policy.RestoreDisconnectBudget(new DisconnectBudget(attempts, At(Base + completedOffset * 1000L))
            {
                State = state == "RequestPending" ? DisconnectRequestState.RequestPending : DisconnectRequestState.Completed,
                CompletedAt = At(Base + completedOffset * 1000L)
            }, At(now));
            Steps.Add(Mutation("restoreDisconnectBudget", now) with { BudgetAttempts = attempts, BudgetState = state, BudgetCompletedAt = Base + completedOffset * 1000L });
        }

        public void DisconnectCompleted(int offset, int attempts)
        {
            var now = Base + offset * 1000L;
            policy.DisconnectCompleted(new DisconnectBudget(attempts, At(now)) { CompletedAt = At(now) }, At(now));
            Steps.Add(Mutation("disconnectCompleted", now));
        }

        public void CancelUnissuedReservation()
        {
            policy.CancelUnissuedDisconnectReservation();
            Steps.Add(Mutation("cancelUnissuedDisconnectReservation", Base));
        }

        public void InterruptObservation()
        {
            policy.InterruptObservation();
            Steps.Add(Mutation("interruptObservation", Base));
        }

        public void DeferAuthentication(int offset)
        {
            policy.DeferAuthenticationUntil(At(Base + offset * 1000L));
            Steps.Add(Mutation("deferAuthentication", Base + offset * 1000L) with { DeferUntil = Base + offset * 1000L });
        }

        public void SetBlocked(bool blocked)
        {
            policy.CredentialsBlocked = blocked;
            Steps.Add(Mutation("setCredentialsBlocked", Base) with { CredentialsBlocked = blocked });
        }

        private RecoveryStep Mutation(string op, long now) => new(op, now, null, null, null, null, null, null,
            null, null, null, null, null, null, Snapshot(now));

        private RecoveryExpect Snapshot(long now) => new(
            policy.DecisionReason, policy.Attempts, policy.DisconnectAttempts,
            Milliseconds(policy.NextAttempt), policy.NextAttemptAction.ToString(),
            Milliseconds(policy.OutageSince), policy.FailedRounds,
            Math.Round(policy.ObservationSeconds(At(now)), 3), policy.CredentialsBlocked);
    }

    private static DateTimeOffset At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
    private static long? Milliseconds(DateTimeOffset value) => value == DateTimeOffset.MinValue ? null : value.ToUnixTimeMilliseconds();
    private static long? Milliseconds(DateTimeOffset? value) => value is { } actual ? Milliseconds(actual) : null;

    // ---------- 历史认证状态 ----------

    private sealed record AuthenticationFixture(string Schema, IReadOnlyList<AuthenticationCase> Cases);
    private sealed record AuthenticationCase(string Name, string? Content, AuthenticationExpect Expect);
    private sealed record AuthenticationExpect(
        string Kind, bool? BlocksSubmissions, string? State, string? Failure, int? Attempts, int? RetryDelaySeconds);

    private static AuthenticationFixture BuildAuthentication()
    {
        const string revision = "11111111-1111-1111-1111-111111111111";
        var fingerprint = new string('A', 64);
        string FailureOnly(PortalFailure failure) => JsonSerializer.Serialize(new { Failure = (int)failure }, Storage.Json);
        string Full(int version = 1, int state = 0, int failure = 0, int attempts = 1, int retryDelay = 0,
            string retryNotBefore = "0001-01-01T00:00:00+00:00", int fallbackCause = 0) =>
            "{\"Version\":" + version + ",\"State\":" + state + ",\"Failure\":" + failure
            + ",\"Attempts\":" + attempts + ",\"RetryDelaySeconds\":" + retryDelay
            + ",\"RetryNotBefore\":\"" + retryNotBefore + "\",\"Revision\":\"" + revision
            + "\",\"ChannelFallbackCause\":" + fallbackCause + "}";

        var inputs = new (string Name, string? Content)[]
        {
            ("文件不存在时返回空", null),
            ("64 位大写十六进制指纹按人工暂停", fingerprint),
            ("64 位小写十六进制指纹按人工暂停", fingerprint.ToLowerInvariant()),
            ("64 位全零指纹按人工暂停", new string('0', 64)),
            ("失败-only 记录保留原始失败分类", FailureOnly(PortalFailure.CredentialsRejected)),
            ("失败-only 绑定拒绝保留分类", FailureOnly(PortalFailure.BindingRejected)),
            ("失败-only 需要人工交互保留分类", FailureOnly(PortalFailure.InteractionRequired)),
            ("失败-only 缺网关参数保留分类", FailureOnly(PortalFailure.MissingGatewayParameters)),
            ("失败-only 安全写入失败保留分类", FailureOnly(PortalFailure.SafetyPersistence)),
            ("失败-only 无原因仍按暂停", FailureOnly(PortalFailure.None)),
            ("失败-only 字符串枚举无法解释仍阻断", "{\"Failure\":\"CredentialsRejected\"}"),
            ("失败-only 越界失败值仍阻断", "{\"Failure\":99}"),
            ("缺 Revision 的当前记录按缺字段阻断", "{\"Version\":1,\"State\":1,\"Failure\":0,\"Attempts\":1,\"RetryDelaySeconds\":0,\"RetryNotBefore\":\"0001-01-01T00:00:00+00:00\"}"),
            ("Revision 不是 GUID 时按无效 JSON 阻断", Full().Replace("\"" + revision + "\"", "\"not-a-guid\"")),
            ("Revision 带花括号时按无效 JSON 阻断", Full().Replace("\"" + revision + "\"", "\"{" + revision + "}\"")),
            ("只有版本字段按缺字段阻断", "{\"Version\":1}"),
            ("重复版本字段按缺字段阻断", "{\"Version\":1,\"Version\":1}"),
            ("未知版本按不支持阻断", Full(version: 99)),
            ("未知状态按无效阻断", Full(state: 42)),
            ("尝试次数越界按无效阻断", Full(attempts: 4)),
            ("提交待确认但次数为零按无效阻断", Full(state: 1, attempts: 0)),
            ("破损 JSON 按无效阻断", "{broken"),
            ("空文件按无效阻断", ""),
            ("状态写成字符串按无效阻断", "{\"Version\":1,\"State\":\"Ready\",\"Failure\":0,\"Attempts\":1,\"RetryDelaySeconds\":0,\"RetryNotBefore\":\"0001-01-01T00:00:00+00:00\",\"Revision\":\"" + revision + "\"}"),
            ("次数写成字符串按无效阻断", "{\"Version\":1,\"State\":1,\"Failure\":0,\"Attempts\":\"1\",\"RetryDelaySeconds\":0,\"RetryNotBefore\":\"0001-01-01T00:00:00+00:00\",\"Revision\":\"" + revision + "\"}"),
            ("数组按非对象阻断", "[]"),
            ("数字按非对象阻断", "0"),
            ("字符串按非对象阻断", "\"ready\""),
            ("当前就绪记录不阻断", Full(state: 0)),
            ("提交待确认记录不授予提交", Full(state: 1)),
            ("冷却记录允许重试且保留退避", Full(state: 2, failure: 13, attempts: 2, retryDelay: 60, retryNotBefore: "2026-01-01T00:01:00+00:00")),
            ("凭据拒绝终态阻断", Full(state: 4, failure: 15)),
            ("设备绑定拒绝终态阻断", Full(state: 3, failure: 19)),
            ("需要人工交互终态阻断", Full(state: 5, failure: 16, attempts: 3)),
            ("交互终态可带结构类降级原因", Full(state: 5, failure: 16, attempts: 3, fallbackCause: 18)),
            ("非交互态带降级原因按不一致阻断", Full(state: 3, failure: 19, attempts: 1, fallbackCause: 18)),
            ("冷却记录缺退避时刻按无效阻断", Full(state: 2, failure: 13, attempts: 2))
        };

        var cases = new List<AuthenticationCase>();
        foreach (var (name, content) in inputs)
        {
            var directory = TempDirectory();
            try
            {
                var store = new SafetyStateStore(directory);
                if (content is not null) File.WriteAllText(Path.Combine(directory, "auth-block.txt"), content);
                var block = store.LoadAuthenticationBlock();
                cases.Add(new AuthenticationCase(name, content, new AuthenticationExpect(
                    store.LastAuthenticationRecordKind.ToString(),
                    block?.BlocksSubmissions, block?.State.ToString(), block?.Failure.ToString(),
                    block?.Attempts, block?.RetryDelaySeconds)));
            }
            finally { TryDelete(directory); }
        }
        return new AuthenticationFixture("autonet.golden.authentication-block/1", cases);
    }

    // ---------- 提交额度 ----------

    private sealed record SubmissionBudgetFixture(string Schema, IReadOnlyList<SubmissionBudgetCase> Cases);
    private sealed record SubmissionBudgetCase(string Name, string? Content, SubmissionBudgetExpect Expect);
    private sealed record SubmissionBudgetExpect(
        string Outcome, string? State, int? Attempts, long? ReservedAt, long? NextPossibleAt, bool? Exhausted);

    private static SubmissionBudgetFixture BuildSubmissionBudget()
    {
        const string revision = "22222222-2222-2222-2222-222222222222";
        const string never = "0001-01-01T00:00:00+00:00";
        string Record(int version, string state, int attempts, string reservedAt, string nextPossibleAt, string extra = "") =>
            "{\"Version\":" + version + ",\"State\":\"" + state + "\",\"Attempts\":" + attempts
            + ",\"ReservedAt\":\"" + reservedAt + "\",\"NextPossibleAt\":\"" + nextPossibleAt
            + "\",\"Revision\":\"" + revision + "\"" + extra + "}";

        var inputs = new (string Name, string? Content)[]
        {
            ("文件不存在表示没有额度记录", null),
            ("已重置的零次记录有效", Record(1, "Concluded", 0, never, never)),
            ("预留中的一次记录有效", Record(1, "Reserved", 1, "2026-01-01T00:00:00+00:00", "2026-01-01T00:02:00+00:00")),
            ("可能已发出的两次记录有效", Record(1, "PossiblySent", 2, "2026-01-01T00:00:00+00:00", "2026-01-01T00:02:00+00:00")),
            ("已结束但保留次数的记录有效", Record(1, "Concluded", 2, "2026-01-01T00:00:00+00:00", "2026-01-01T00:02:00+00:00")),
            ("三次记录有效且视为耗尽", Record(1, "PossiblySent", 3, "2026-01-01T00:00:00+00:00", "2026-01-01T00:02:00+00:00")),
            ("未知版本无效", Record(2, "Reserved", 1, "2026-01-01T00:00:00+00:00", "2026-01-01T00:02:00+00:00")),
            ("未知状态字符串无效", Record(1, "Done", 1, "2026-01-01T00:00:00+00:00", "2026-01-01T00:02:00+00:00")),
            ("次数越界无效", Record(1, "Reserved", 4, "2026-01-01T00:00:00+00:00", "2026-01-01T00:02:00+00:00")),
            ("零次但非结束态无效", Record(1, "Reserved", 0, never, never)),
            ("零次但带时刻无效", Record(1, "Concluded", 0, "2026-01-01T00:00:00+00:00", "2026-01-01T00:02:00+00:00")),
            ("有次数但缺预留时刻无效", Record(1, "Reserved", 1, never, "2026-01-01T00:02:00+00:00")),
            ("间隔不足 120 秒无效", Record(1, "Reserved", 1, "2026-01-01T00:00:00+00:00", "2026-01-01T00:01:00+00:00")),
            ("多余字段无效", Record(1, "Reserved", 1, "2026-01-01T00:00:00+00:00", "2026-01-01T00:02:00+00:00", ",\"Extra\":1")),
            ("重复字段无效", Record(1, "Reserved", 1, "2026-01-01T00:00:00+00:00", "2026-01-01T00:02:00+00:00", ",\"Attempts\":1")),
            ("缺字段无效", "{\"Version\":1,\"State\":\"Reserved\"}"),
            ("Revision 不是 GUID 无效", Record(1, "Reserved", 1, "2026-01-01T00:00:00+00:00", "2026-01-01T00:02:00+00:00").Replace("\"" + revision + "\"", "\"not-a-guid\"")),
            ("Revision 带花括号无效", Record(1, "Reserved", 1, "2026-01-01T00:00:00+00:00", "2026-01-01T00:02:00+00:00").Replace("\"" + revision + "\"", "\"{" + revision + "}\"")),
            ("非对象无效", "[]"),
            ("破损 JSON 无效", "{broken")
        };

        var cases = new List<SubmissionBudgetCase>();
        foreach (var (name, content) in inputs)
        {
            var directory = TempDirectory();
            try
            {
                var store = new SafetyStateStore(directory);
                if (content is not null) File.WriteAllText(Path.Combine(directory, "submission-budget.json"), content);
                SubmissionBudgetExpect expect;
                try
                {
                    var budget = store.LoadSubmissionBudget();
                    expect = budget is null
                        ? new SubmissionBudgetExpect("Missing", null, null, null, null, null)
                        : new SubmissionBudgetExpect("Valid", budget.State.ToString(), budget.Attempts,
                            Milliseconds(budget.ReservedAt), Milliseconds(budget.NextPossibleAt), budget.Exhausted);
                }
                catch (InvalidDataException)
                {
                    expect = new SubmissionBudgetExpect("Invalid", null, null, null, null, null);
                }
                cases.Add(new SubmissionBudgetCase(name, content, expect));
            }
            finally { TryDelete(directory); }
        }
        return new SubmissionBudgetFixture("autonet.golden.submission-budget/1", cases);
    }

    private sealed record ProjectionFixture(
        string Schema, JsonElement Status, JsonElement Log, ProjectionExpect Expect);
    private sealed record ProjectionExpect(
        string State, long UpdatedAtMillis, string Wifi, string Direct, string Portal, string Tunnel,
        string UuClass, string SlowestStage, string SubmissionBudgetCode, string WifiLinkCode,
        string NotificationCode, string AuthenticationStage, int SubmissionAttempts,
        double CycleMilliseconds, string LogAction, string LogEventCode,
        IReadOnlyList<string> SensitiveMarkers);

    private static ProjectionFixture BuildProjection()
    {
        // 状态与日志都刻意填入敏感哨兵；Rust 投影若带出任何一个，测试即失败。
        var status = new StatusSnapshot
        {
            UpdatedAt = At(Base),
            State = "校园网在线",
            Detail = "SECRET-STATUS-DETAIL",
            Enabled = true,
            Username = "SECRET-ACCOUNT",
            InterfaceId = "SECRET-INTERFACE",
            WifiProfile = "SECRET-PROFILE",
            ProxyUrlDisplay = "SECRET-PROXY",
            UuEndpointDisplay = "SECRET-UU",
            Wifi = "已连接",
            Direct = "外网可达",
            Portal = "未查询（外网检查已通过）",
            Tunnel = "系统路径可达（路由样本推断为 Wi-Fi，出口未核验）",
            Uu = "UU 服务运行（3 条已建立连接）",
            Attempts = 1,
            DisconnectBudgetMax = 3,
            DisconnectCooldownSeconds = 120,
            DisconnectResetAfterSeconds = 60,
            NextAttemptAction = "None",
            CycleMilliseconds = 180,
            SlowestStage = "InternetProbe",
            SlowestStageMilliseconds = 150,
            AuthenticationStage = "Status",
            AuthenticationFailure = "None",
            AuthenticationPersistentState = "Ready",
            CredentialsAvailable = true,
            SettingsAvailable = true,
            SubmissionBudgetCode = "Available",
            SubmissionRetryCode = "Authorized",
            SubmissionAttempts = 1,
            SubmissionAttemptMax = 3,
            SubmissionBudgetExhausted = false,
            SubmissionCooldownSeconds = 120,
            WifiLinkCode = "Ready",
            LastError = "SECRET-STATUS-ERROR",
            Notification = new StatusNotification(NotificationCodes.InternetRecovered, At(Base))
        };
        var entry = new ActivityLog
        {
            Time = At(Base),
            Action = "恢复决策",
            Detail = "SECRET-LOG-DETAIL",
            Wifi = "已连接",
            Portal = "需认证",
            Direct = "外网不可达或未知",
            Attempts = 2,
            DisconnectAttempts = 0,
            AuthenticationStage = "Status",
            EventCode = "RecoveryDecision",
            NetworkId = "SECRET-NETWORK",
            LastError = "SECRET-LOG-ERROR"
        };
        using var statusDocument = JsonDocument.Parse(JsonSerializer.Serialize(status, Storage.Json));
        using var logDocument = JsonDocument.Parse(JsonSerializer.Serialize(entry, ActivityLog.Json));
        return new ProjectionFixture(
            "autonet.golden.projection/1",
            statusDocument.RootElement.Clone(),
            logDocument.RootElement.Clone(),
            new ProjectionExpect(
                "校园网在线", Base, "已连接", "外网可达", "未查询（外网检查已通过）",
                "系统路径可达（路由样本推断为 Wi-Fi，出口未核验）", "running", "InternetProbe",
                "Available", "Ready", NotificationCodes.InternetRecovered, "Status", 1, 180,
                "恢复决策", "RecoveryDecision",
                [
                    "SECRET-STATUS-DETAIL", "SECRET-STATUS-ERROR", "SECRET-ACCOUNT", "SECRET-INTERFACE",
                    "SECRET-PROFILE", "SECRET-PROXY", "SECRET-UU", "SECRET-LOG-DETAIL", "SECRET-NETWORK",
                    "SECRET-LOG-ERROR"
                ]));
    }

    private sealed record DpapiFixture(
        string Schema, string Scope, string? Entropy, string CiphertextBase64,
        string PlaintextUtf8Sha256, int PlaintextBytes, string Note);
    private sealed record ManifestDocument(
        string Schema, string SourceBaselineCommit, string Generator,
        IReadOnlyList<ManifestFile> Fixtures, IReadOnlyList<string> Notes);
    private sealed record ManifestFile(string File, string Schema, int Cases);

    private static DpapiFixture BuildDpapi()
    {
        // 刻意使用明显假的测试凭据；夹具只保存密文与明文摘要，不保存可读密码。
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Credentials("fixture-user", "fixture-password-not-real"));
        try
        {
            var cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
            return new DpapiFixture("autonet.golden.dpapi/1", "LocalMachine", null,
                Convert.ToBase64String(cipher),
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length,
                "同机只读解密用例需设置 AUTONET_DPAPI_SAME_MACHINE=1；其他机器必须跳过，不得据此判断兼容性。");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "autonet-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
