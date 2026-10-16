using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AutoNet.Core;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 解除认证暂停回归（迁移工单 #5 / T08 / 故事 42、43）：
/// 入口前提（仅存在待解除阻断时可继续）、用户确认、只读复核、修订核对、持久审计后解除的完整序列，
/// 以及失败路径的“保留暂停 + 受控说明”。同时核验全程不加载、不提交凭据，
/// 且拒绝 UAC 时没有设置、认证阻断或服务副作用。
/// 受控 UI 证据由 scripts/qa-avalonia-unblock.ps1（unblock 模式）提供。
/// </summary>
internal static class UnblockRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("解除回归：无待解除标记时不确认不解除且零写入", async () =>
        {
            var fixture = new Fixture(seedBlock: false);
            try
            {
                var result = await fixture.Build(confirm: true).RunAsync();
                Require(result.Outcome == UnblockOutcome.NoBlock, "没有标记时必须拒绝凭空解除：" + result.Outcome);
                Require(result.Stages.Count == 0, "无标记时不得进入任何阶段：" + string.Join(",", result.Stages));
                Require(!fixture.ConfirmAsked, "无标记时不得弹确认对话框");
                Require(!fixture.RecheckCalled, "无标记时不得发起只读复核");
                Require(fixture.Releases == 0, "无标记时不得调用解除");
                Require(!fixture.BlockFileExists, "无标记时不得创建标记文件");
                Require(fixture.AuditLines == 0, "无标记时不得写审计行");
                Require(result.RequiresNotice && result.Message.Contains("没有需要人工解除"), "结果文案不符：" + result.Message);
            }
            finally { fixture.Dispose(); }
        }),
        ("解除回归：用户取消确认时只走确认阶段且保留暂停", async () =>
        {
            var fixture = new Fixture();
            try
            {
                var result = await fixture.Build(confirm: false).RunAsync();
                Require(result.Outcome == UnblockOutcome.Declined, "取消后结果应为 Declined：" + result.Outcome);
                Require(result.Stages.SequenceEqual([UnblockStage.Confirm]), "取消后阶段序列应只有确认：" + string.Join(",", result.Stages));
                Require(fixture.ConfirmAsked, "取消场景必须真的经过确认点");
                Require(!fixture.RecheckCalled, "取消后不得发起只读复核");
                Require(fixture.Releases == 0, "取消后不得调用解除");
                Require(fixture.BlockFileExists, "取消后认证暂停必须保留");
                Require(fixture.AuditLines == 0, "取消后不得写审计行");
                Require(!result.RequiresNotice, "用户自己取消不需要再弹受控说明");
            }
            finally { fixture.Dispose(); }
        }),
        ("解除回归：前置复核未通过时保留暂停并给出受控说明", async () =>
        {
            var fixture = new Fixture();
            try
            {
                var result = await fixture.Build(confirm: true, recheck: Fixed(
                    new PortalAttempt(LoginResult.Rejected, PortalStage.Submission, PortalFailure.BindingRejected))).RunAsync();
                Require(result.Outcome == UnblockOutcome.RecheckFailed, "复核未通过应为 RecheckFailed：" + result.Outcome);
                Require(result.Stages.SequenceEqual([UnblockStage.Confirm, UnblockStage.Recheck]),
                    "复核未通过时不得进入修订核对：" + string.Join(",", result.Stages));
                Require(fixture.Releases == 0, "复核未通过不得调用解除");
                Require(fixture.BlockFileExists, "复核未通过必须保留认证暂停");
                Require(fixture.AuditLines == 0, "复核未通过不得写审计行（没有发生解除）");
                Require(result.RequiresNotice, "复核未通过必须给出受控说明");
                Require(result.Message.Contains("前置复核未通过，保留认证暂停"), "受控说明缺少保留暂停的结论：" + result.Message);
                Require(result.Message.Contains("BindingRejected"), "受控说明应给出受控失败分类：" + result.Message);
                Require(UnblockAuthentication.HeadingFor(result.Outcome) == "前置复核未通过，保留认证暂停", "标题术语不符");
            }
            finally { fixture.Dispose(); }
        }),
        ("解除回归：诊断期间设置或校园 Wi-Fi 变化时中止并保留暂停", async () =>
        {
            foreach (var (name, mutate) in new (string, Action<Fixture>)[]
            {
                ("设置变化", fixture => fixture.Settings = (fixture.Settings ?? ValidSettings()) with { WifiProfile = "OTHER-AP" }),
                ("网卡索引变化", fixture => fixture.LinkIndex = 9),
                ("IPv4 变化", fixture => fixture.LinkAddress = "10.0.0.99"),
                ("Wi-Fi 未就绪", fixture => fixture.LinkReady = false)
            })
            {
                var fixture = new Fixture();
                try
                {
                    var flow = fixture.Build(confirm: true);
                    // 复核期间制造变化：诊断一被调用就改写事实，随后修订核对必须发现差异。
                    fixture.DuringRecheck = () => mutate(fixture);
                    var result = await flow.RunAsync();
                    Require(result.Outcome == UnblockOutcome.RevisionChanged, name + "：应中止，实际 " + result.Outcome);
                    Require(result.Stages.SequenceEqual([UnblockStage.Confirm, UnblockStage.Recheck, UnblockStage.RevisionCheck]),
                        name + "：阶段序列不符：" + string.Join(",", result.Stages));
                    Require(!result.Stages.Contains(UnblockStage.Audit), name + "：中止后不得进入审计解除");
                    Require(fixture.Releases == 0, name + "：中止后不得调用解除");
                    Require(fixture.BlockFileExists, name + "：中止后必须保留认证暂停");
                    Require(fixture.AuditLines == 0, name + "：中止后不得写审计行");
                    Require(result.Message.Contains("已变化") || result.Message.Contains("重新诊断"), name + "：说明未指向重新诊断：" + result.Message);
                }
                finally { fixture.Dispose(); }
            }
        }),
        ("解除回归：成功路径按确认→复核→修订核对→审计顺序解除", async () =>
        {
            var fixture = new Fixture();
            try
            {
                var result = await fixture.Build(confirm: true).RunAsync();
                Require(result.Released, "完整序列应解除：" + result.Outcome + " / " + result.Message);
                Require(result.Stages.SequenceEqual(
                    [UnblockStage.Confirm, UnblockStage.Recheck, UnblockStage.RevisionCheck, UnblockStage.Audit]),
                    "阶段顺序不符：" + string.Join(",", result.Stages));
                Require(fixture.ConfirmAsked && fixture.RecheckCalled && fixture.Releases == 1, "四段序列未全部执行");
                Require(!fixture.BlockFileExists, "解除后标记文件应被删除");
                Require(fixture.AuditLines == 1, "解除应留下恰好一行审计：" + fixture.AuditLines);
                var audit = fixture.AuditEntries.Single();
                Require(audit.Action == "人工确认解除认证暂停", "审计行缺少解除动作：" + audit.Action);
                Require(audit.State == nameof(AuthenticationState.BindingRejected) &&
                    audit.Failure == nameof(PortalFailure.BindingRejected),
                    "审计行缺少解除前的状态事实：" + audit.State + "/" + audit.Failure);
                Require(!result.RequiresNotice, "成功保持既有静默语义");
            }
            finally { fixture.Dispose(); }
        }),
        ("解除回归：缺少设置或校园 Wi-Fi 未就绪时受控失败且保留暂停", async () =>
        {
            var missing = new Fixture { Settings = null };
            try
            {
                var result = await missing.Build(confirm: true).RunAsync();
                Require(result.Outcome == UnblockOutcome.NotReady, "缺设置应为 NotReady：" + result.Outcome);
                Require(result.Stages.SequenceEqual([UnblockStage.Confirm, UnblockStage.Recheck]), "阶段序列不符");
                Require(missing.Releases == 0 && missing.BlockFileExists && missing.AuditLines == 0, "缺设置时不得解除");
                Require(result.Message.Contains("请先保存校园网设置"), "缺设置说明不符：" + result.Message);
            }
            finally { missing.Dispose(); }

            var offline = new Fixture { LinkReady = false };
            try
            {
                var result = await offline.Build(confirm: true).RunAsync();
                Require(result.Outcome == UnblockOutcome.NotReady, "Wi-Fi 未就绪应为 NotReady：" + result.Outcome);
                Require(offline.Releases == 0 && offline.BlockFileExists && offline.AuditLines == 0, "Wi-Fi 未就绪时不得解除");
                Require(result.Message.Contains("校园 Wi-Fi"), "Wi-Fi 未就绪说明不符：" + result.Message);
            }
            finally { offline.Dispose(); }
        }),
        ("解除回归：全程只走只读诊断且不加载、不提交凭据", async () =>
        {
            var fixtureHandler = new ReadOnlyPortalFixture();
            using var http = new HttpClient(fixtureHandler);
            var portal = new DrcomPortal(http, new Uri("https://login.csust.edu.cn"));
            var fixture = new Fixture();
            try
            {
                var flow = fixture.Build(confirm: true, recheck: (settings, link, ct) => portal.DiagnoseAsync(ct));
                var result = await flow.RunAsync();

                // 复核走的是与生产路径同一个 DrcomPortal.DiagnoseAsync（无凭据重载）。
                Require(fixtureHandler.RequestPaths.Count > 0, "只读复核必须真的发起过只读请求");
                Require(!fixtureHandler.RequestPaths.Contains(ReadOnlyPortalFixture.CredentialBearingRequest),
                    "解除流程出现了携带凭据的请求：" + string.Join(",", fixtureHandler.RequestPaths));
                Require(!fixtureHandler.RequestPaths.Contains("/eportal/portal/login"),
                    "解除流程不得请求认证提交端点");
                Require(result.Released, "只读复核通过后应正常解除：" + result.Outcome);

                // 凭据面：解除流程既不改写也不新建凭据文件（文件是夹具预先放好的合成 DPAPI 密文）。
                fixture.VerifyCredentialUntouched();
                Require(fixture.CredentialFileUntouched, "解除流程改写了凭据文件");
                Require(fixture.CredentialFilesCreatedByFlow == 0, "解除流程新建了凭据文件");
            }
            finally { fixture.Dispose(); }
        }),
        ("解除回归：拒绝 UAC 时没有设置、认证阻断或服务副作用", () => Sync(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-unblock-uac-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var settingsPath = Path.Combine(directory, "settings.json");
                var blockPath = Path.Combine(directory, "auth-block.txt");
                var auditPath = Path.Combine(directory, "auth-release-audit.jsonl");
                Storage.AtomicWrite(settingsPath, JsonSerializer.Serialize(ValidSettings(), Storage.Json));
                var safety = new SafetyStateStore(directory);
                safety.BlockAuthentication(new AuthenticationBlock(PortalFailure.BindingRejected));

                var settingsBefore = File.ReadAllBytes(settingsPath);
                var blockBefore = File.ReadAllBytes(blockPath);

                // 注入的启动器模拟用户在 UAC 上选择“否”（Win32 1223）。
                var attempts = 0;
                var launched = Elevation.Launch(ElevatedOperationKind.UnblockAuthentication, _ =>
                {
                    attempts++;
                    throw new System.ComponentModel.Win32Exception(1223);
                });

                Require(!launched, "拒绝 UAC 时不得报告已发起提权");
                Require(attempts == 1, "拒绝 UAC 后不得重试提权：" + attempts);
                Require(File.ReadAllBytes(settingsPath).SequenceEqual(settingsBefore), "拒绝 UAC 改动了设置");
                Require(File.ReadAllBytes(blockPath).SequenceEqual(blockBefore), "拒绝 UAC 改动了认证阻断");
                Require(!File.Exists(auditPath), "拒绝 UAC 写出了审计行");
                Require(safety.LoadAuthenticationBlock() is { BlocksSubmissions: true }, "拒绝 UAC 后认证暂停未保留");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("解除回归：审计写入失败时保留认证暂停且不谎报成功", async () =>
        {
            var fixture = new Fixture();
            try
            {
                // 审计目标改成目录：追加写入必然失败，解除流程必须受控失败而非谎报解除。
                Directory.CreateDirectory(Path.Combine(fixture.Directory, "auth-release-audit.jsonl"));
                var failed = false;
                UnblockResult result;
                try { result = await fixture.Build(confirm: true).RunAsync(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; result = null!; }

                // 审计写入失败必须抛出而不是静默成功：异常路径没有 UnblockResult，绝不可能谎报已解除。
                Require(failed, "审计写入失败必须抛出而不是静默成功");
                Require(fixture.Releases == 1, "解除动作应已尝试（由存储层保证审计先于删除）");
                Require(fixture.BlockFileExists, "审计失败后必须保留认证暂停");
                Require(fixture.AuditLines == 0, "审计失败不应留下半行审计");
                Require(result is null || !result.Released, "审计失败不得报告已解除");
            }
            finally { fixture.Dispose(); }
        }),
        ("解除回归：托盘与概览的解除入口同源且仅在存在待解除阻断时可用", () => Sync(() =>
        {
            // 托盘菜单保留既有条目顺序与逐字标签（与 WinForms TrayContext 的“解除认证暂停…”一致）。
            var entries = TrayMenuModel.Entries;
            var unblock = entries.Single(entry => entry.Action == TrayMenuAction.UnblockAuthentication);
            Require(unblock.Label == "解除认证暂停…", "托盘解除入口标签不符：" + unblock.Label);
            var positions = entries.Select((entry, index) => (entry, index))
                .Where(pair => pair.entry.Action is TrayMenuAction.UnblockAuthentication or TrayMenuAction.Exit)
                .ToDictionary(pair => pair.entry.Action!.Value, pair => pair.index);
            Require(positions[TrayMenuAction.UnblockAuthentication] < positions[TrayMenuAction.Exit],
                "解除入口必须排在退出之前");
            Require(entries.Any(entry => entry.Action == TrayMenuAction.Settings),
                "托盘必须保留独立提权的设置入口");
            Require(entries.Count(entry => entry.Action == TrayMenuAction.UnblockAuthentication) == 1,
                "解除入口不得重复出现");

            // 入口门槛与概览按钮共用同一投影：可信且确有认证暂停时才可用。
            var suspended = Reading(Online() with
            {
                CredentialsBlocked = true,
                AuthenticationFailure = "CredentialsRejected"
            }, TimeSpan.Zero);
            Require(OverviewModel.UnblockEntry(suspended) is { Visible: true, Enabled: true },
                "存在待解除阻断时托盘入口应可用");

            // 状态不可信：可见但禁用——不能证明存在待解除阻断，也不能依据陈旧信息操作。
            var stale = Reading(Online() with { CredentialsBlocked = true }, TimeSpan.FromMinutes(1));
            var staleEntry = OverviewModel.UnblockEntry(stale);
            Require(staleEntry.Visible && !staleEntry.Enabled, "过期状态的托盘入口应为可见但禁用");
            Require(OverviewModel.UnblockEntry(null) == OverviewModel.UnconfirmedUnblock, "未取得状态时不得可用");
            Require(!OverviewModel.UnblockEntry(Reading(Online(), TimeSpan.Zero)).Visible,
                "没有待解除阻断时托盘入口不得提供");

            // 点击菜单等价于走概览按钮的同一条路径：动作前重读并核对可信状态。
            var launched = new List<ElevatedOperationKind>();
            var actions = new OverviewActions(_ => Task.FromResult(suspended), launched.Add);
            Require(actions.RequestUnblockAsync().GetAwaiter().GetResult(), "可信暂停状态应发起解除");
            Require(launched.SequenceEqual([ElevatedOperationKind.UnblockAuthentication]), "解除入口发起了非预期的操作");

            var refused = new OverviewActions(_ => Task.FromResult(stale), launched.Add);
            Require(!refused.RequestUnblockAsync().GetAwaiter().GetResult(), "过期状态不得从托盘发起解除");
            Require(launched.Count == 1, "过期状态仍发起了解除");
        })),
        ("解除回归：提权入口逐字沿用既有启动契约且不受普通互斥锁拦截", () => Sync(() =>
        {
            Require(ElevatedOperationKind.UnblockAuthentication.CommandLineArgument() == "--unblock-auth",
                "解除操作的启动参数必须与既有 WinForms 托盘逐字一致");
            Require(ElevatedOperationKind.UnblockAuthentication.DisplayName() == "解除认证暂停");

            // 已有普通托盘实例运行时，解除入口仍必须升级到提权路径而不是被互斥锁拦下（故事 8）。
            var request = StartupRequest.Parse(["--unblock-auth"]);
            Require(request.ElevatedOperation == ElevatedOperationKind.UnblockAuthentication);
            var denied = StartupRouter.Decide(request, isAdministrator: false, anotherInstanceRunning: true);
            Require(denied.Kind == StartupActionKind.RequestElevation, "解除入口被普通互斥锁拦截：" + denied.Kind);
            var allowed = StartupRouter.Decide(request, isAdministrator: true, anotherInstanceRunning: true);
            Require(allowed.Kind == StartupActionKind.RunElevatedOperation &&
                allowed.Operation == ElevatedOperationKind.UnblockAuthentication);

            // 启动参数解析仍然保持“一次启动只允许一个管理员操作”。
            var conflicted = false;
            try { StartupRequest.Parse(["--unblock-auth", "--settings"]); }
            catch (ArgumentException) { conflicted = true; }
            Require(conflicted, "同一次启动必须只允许一个管理员操作");
        })),
        ("解除回归：确认文案先说明后果且不泄露凭据", () => Sync(() =>
        {
            Require(UnblockAuthentication.ConfirmHeading == "解除认证暂停");
            foreach (var required in new[] { "请先核实", "解除后", "再次提交当前账号密码", "会重新暂停", "确认已核实" })
                Require(UnblockAuthentication.ConfirmBody.Contains(required), "确认文案缺少：" + required);
            foreach (var forbidden in new[] { "user_password", "phpsessid", "http://", "https://" })
                Require(!UnblockAuthentication.ConfirmBody.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    "确认文案泄露了 " + forbidden);
        }))
    ];

    /// <summary>把一个固定的只读复核结论包装成注入委托，便于逐条断言失败路径。</summary>
    private static Func<Settings, WifiLink, CancellationToken, Task<PortalAttempt>> Fixed(PortalAttempt attempt) =>
        (_, _, _) => Task.FromResult(attempt);

    private static StatusReading Reading(StatusSnapshot? snapshot, TimeSpan age)
    {
        var valid = StatusSnapshotReader.IsValid(snapshot);
        return new StatusReading(snapshot, NetworkDisplayMap.Map(snapshot, age), valid,
            valid && NetworkDisplayMap.IsFresh(age), age, DateTimeOffset.UtcNow);
    }

    private static StatusSnapshot Online() => new()
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Enabled = true,
        State = "校园网在线",
        Wifi = "已连接",
        Direct = "外网可达",
        Portal = "未查询（外网检查已通过）",
        Tunnel = "系统路径可达，出口未核验",
        Uu = "未核验"
    };

    private static Task Sync(Action action) { action(); return Task.CompletedTask; }

    private static void Require(bool condition, string message = "断言失败")
    {
        if (!condition) throw new Exception(message);
    }

    private static Settings ValidSettings() => new()
    {
        InterfaceId = Guid.Parse("11111111-2222-3333-4444-555555555555").ToString(),
        WifiProfile = "TEST-AP-1",
        PortalOrigin = "https://login.csust.edu.cn"
    };

    /// <summary>
    /// 隔离夹具：每个用例一个临时目录，用真实 <see cref="SafetyStateStore"/> 复现受限目录语义
    /// （安全状态文件、审计 jsonl、锁文件），因此“审计先落盘再删除标记”的顺序由生产代码保证。
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "autonet-unblock-" + Guid.NewGuid().ToString("N"));
        private readonly SafetyStateStore safety;

        public Settings? Settings { get; set; } = ValidSettings();
        public bool LinkReady { get; set; } = true;
        public int LinkIndex { get; set; } = 7;
        public string LinkAddress { get; set; } = "10.0.0.1";
        public Action? DuringRecheck { get; set; }

        public bool ConfirmAsked { get; private set; }
        public bool RecheckCalled { get; private set; }
        public int Releases { get; private set; }
        public int AuditLines { get; private set; }

        /// <summary>审计行的结构化字段：jsonl 每行一个对象且 <c>Storage.Json</c> 会转义非 ASCII，因此按字段断言而非按字面文案。</summary>
        public IReadOnlyList<(string Action, string State, string Failure)> AuditEntries
        {
            get
            {
                if (!File.Exists(AuditPath)) return [];
                var entries = new List<(string, string, string)>();
                foreach (var line in File.ReadAllLines(AuditPath))
                {
                    if (line.Trim().Length == 0) continue;
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    entries.Add((root.GetProperty("Action").GetString() ?? "",
                        root.GetProperty("State").GetString() ?? "",
                        root.GetProperty("Failure").GetString() ?? ""));
                }

                return entries;
            }
        }

        public bool BlockFileExists => File.Exists(BlockPath);

        /// <summary>
        /// 凭据面是否被解除流程改动。夹具预先放好一份本机 DPAPI 加密的合成凭据，
        /// 跑完后逐字节比对，并核对目录里凭据文件数量没有增加。
        /// （“不加载凭据”由 <see cref="UnblockAuthentication"/> 完全没有凭据依赖来保证，
        /// 并由复核请求里零 <c>user_password</c> 的断言佐证——文件探测覆盖的是写入面。）
        /// </summary>
        public bool CredentialFileUntouched { get; private set; } = true;

        /// <summary>解除流程新建的凭据文件数：必须为 0。</summary>
        public int CredentialFilesCreatedByFlow { get; private set; }

        private byte[]? credentialBaseline;
        private int credentialFileCount;

        private string SettingsPath => Path.Combine(Directory, "settings.json");
        private string BlockPath => Path.Combine(Directory, "auth-block.txt");
        private string AuditPath => Path.Combine(Directory, "auth-release-audit.jsonl");
        private string CredentialPath => Path.Combine(Directory, "credentials.dpapi");

        public Fixture(bool seedBlock = true)
        {
            System.IO.Directory.CreateDirectory(Directory);
            safety = new SafetyStateStore(Directory);
            if (seedBlock) safety.BlockAuthentication(new AuthenticationBlock(PortalFailure.BindingRejected));
            Persist();
            credentialBaseline = SeedSyntheticCredential();
        }

        /// <summary>解除跑完后调用：凭据面必须逐字节未变，且没有新增凭据文件。</summary>
        public void VerifyCredentialUntouched()
        {
            var files = System.IO.Directory.GetFiles(Directory, "credentials*");
            CredentialFilesCreatedByFlow = files.Length - credentialFileCount;
            if (credentialBaseline is null) return;
            CredentialFileUntouched = File.Exists(CredentialPath) &&
                File.ReadAllBytes(CredentialPath).SequenceEqual(credentialBaseline);
        }

        // 预先放好一份本机自己加密的合成凭据，只用于证明解除流程不碰凭据面。
        // 账号密码均为占位值，不来自也不同于本机真实配置。返回基线字节供跑完后比对；
        // 拿不到 DPAPI 时返回 null 并跳过该面断言（其余断言仍然有效）。
        private byte[]? SeedSyntheticCredential()
        {
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes("{\"Username\":\"unblock-fixture\",\"Password\":\"fixture-never-real\"}");
                var encrypted = System.Security.Cryptography.ProtectedData.Protect(bytes, null,
                    System.Security.Cryptography.DataProtectionScope.LocalMachine);
                Storage.AtomicWrite(CredentialPath, encrypted);
                credentialFileCount = 1;
                return File.ReadAllBytes(CredentialPath);
            }
            catch (CryptographicException)
            {
                if (File.Exists(CredentialPath)) File.Delete(CredentialPath);
                credentialFileCount = 0;
                return null;
            }
        }

        public UnblockAuthentication Build(
            bool confirm,
            Func<Settings, WifiLink, CancellationToken, Task<PortalAttempt>>? recheck = null)
        {
            Persist();
            return new UnblockAuthentication(
                () => safety.LoadAuthenticationBlock(),
                LoadSettings,
                _ => ReadLink(),
                async (settings, link, ct) =>
                {
                    RecheckCalled = true;
                    var attempt = recheck is null
                        ? new PortalAttempt(LoginResult.Accepted, PortalStage.Complete, PortalFailure.None)
                        : await recheck(settings, link, ct);
                    DuringRecheck?.Invoke();
                    Persist();
                    return attempt;
                },
                block =>
                {
                    Releases++;
                    safety.ClearAuthenticationBlock(block);
                    Refresh();
                },
                _ =>
                {
                    ConfirmAsked = true;
                    return Task.FromResult(confirm);
                });
        }

        private WifiLink ReadLink() => LinkReady
            ? new(true, null, IPAddress.Parse(LinkAddress), LinkIndex, [])
            : new(false, null, null, 0, []);

        private Settings? LoadSettings() => File.Exists(SettingsPath)
            ? JsonSerializer.Deserialize<Settings>(Storage.ReadShared(SettingsPath), Storage.Json) : null;

        // 每次改写设置都落盘，使“诊断期间设置变化”成为真实可观察的文件事实。
        private void Persist()
        {
            if (Settings is null)
            {
                if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
                return;
            }

            Storage.AtomicWrite(SettingsPath, JsonSerializer.Serialize(Settings, Storage.Json));
        }

        private void Refresh()
        {
            AuditLines = File.Exists(AuditPath)
                ? File.ReadAllLines(AuditPath).Count(line => line.Trim().Length > 0) : 0;
        }

        public void Dispose()
        {
            try { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException)
            {
                // 临时目录清理失败不影响用例结论。
            }
        }
    }
}
