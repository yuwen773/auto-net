using AutoNet.Core;
using AutoNet.Tray.Avalonia;

internal static class PortalOpenRegression
{
    private static readonly DateTimeOffset Base = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    private const string Url = "https://login.csust.edu.cn/";

    /// <summary>会话夹具：临时记录文件 + 可拨动时钟 + 可替换快照 + 启动记录。重读与评估共用同一夹具。</summary>
    private sealed class Session
    {
        public readonly string Root = Directory.CreateTempSubdirectory("AutoNet-portalopen-").FullName;
        public readonly string StorePath;
        public readonly List<string> Launched = [];
        public readonly List<string> Errors = [];
        public DateTimeOffset Now = Base;
        public StatusSnapshot Snapshot = Online();
        public Func<string, string?> Launch { get; }

        public Session(bool failingLaunch = false)
        {
            StorePath = Path.Combine(Root, PortalOpenStore.FileName);
            Launch = url =>
            {
                Launched.Add(url);
                if (failingLaunch) { Errors.Add("StubbedException"); return "StubbedException"; }
                return null;
            };
            Cleanup.Register(Root);
        }

        public Func<DateTimeOffset> Clock => () => Now;

        public StatusReading Reading() => Trusted(Snapshot);

        public Func<CancellationToken, Task<StatusReading>> Reread => _ => Task.FromResult(Reading());

        public PortalPageOpener NewOpener() => new(new PortalOpenStore(StorePath), Reread, Launch, Clock);
    }

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("开页：首次有效授予立即打开允许根页且不带凭据（故事61-63）", () =>
        {
            var session = new Session();
            var opener = session.NewOpener();
            session.Snapshot = Grant(Base.AddMinutes(10));
            var outcome = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(outcome == PortalOpenOutcome.Opened, "首次授予应立即开页");
            Require(session.Launched.Count == 1 && session.Launched[0] == Url,
                "应恰好启动一次且 URL 逐字为允许根页：" + string.Join(",", session.Launched));
            Require(!session.Launched[0].Contains('?') && !session.Launched[0].Contains('@'),
                "URL 不得携带查询或 userinfo（故事 63）");
            return Task.CompletedTask;
        }),
        ("开页：同一授予会话内与跨重启都只自动开一次（故事64）", () =>
        {
            var session = new Session();
            var opener = session.NewOpener();
            session.Snapshot = Grant(Base.AddMinutes(10));
            opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            var second = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(second == PortalOpenOutcome.AlreadyConsumed && session.Launched.Count == 1, "同一授予不应重复自动开页");
            // 新会话（新 store + 新 opener，同一持久文件）：跨托盘重启不重开。
            var restarted = session.NewOpener();
            var outcome = restarted.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(outcome == PortalOpenOutcome.AlreadyConsumed && session.Launched.Count == 1, "重启后同一授予不应再次自动开页");
            return Task.CompletedTask;
        }),
        ("开页：服务换发授予不绕本地冷却；冷却满足后新授予恰好开一次（故事65、ADR-0008）", () =>
        {
            var session = new Session();
            var opener = session.NewOpener();
            session.Snapshot = Grant(Base.AddMinutes(10));
            opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            session.Snapshot = Grant(Base.AddMinutes(20));
            var deferred = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(deferred == PortalOpenOutcome.CooldownActive && session.Launched.Count == 1, "换发授予应被本地冷却拦下");
            session.Now = Base.AddMinutes(2); // 冷却未满
            var still = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(still == PortalOpenOutcome.CooldownActive, "冷却未满时不得开页");
            session.Now = Base.AddMinutes(11); // 冷却已满
            var opened = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(opened == PortalOpenOutcome.Opened && session.Launched.Count == 2, "冷却满足后新授予应恰好开一次");
            var again = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(again == PortalOpenOutcome.AlreadyConsumed && session.Launched.Count == 2, "同一授予开过一次后不再开");
            return Task.CompletedTask;
        }),
        ("开页：冷却到期本身不循环重开（故事67）", () =>
        {
            var session = new Session();
            var opener = session.NewOpener();
            session.Snapshot = Grant(Base.AddMinutes(10));
            opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            session.Snapshot = Grant(Base.AddMinutes(10)); // 请求与授予未变
            session.Now = Base.AddMinutes(30); // 冷却早已到期
            var outcome = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(outcome == PortalOpenOutcome.AlreadyConsumed && session.Launched.Count == 1, "冷却到期不得循环重开同一授予");
            return Task.CompletedTask;
        }),
        ("开页：手动开页计入消费与冷却（故事66）", () =>
        {
            var session = new Session();
            var opener = session.NewOpener();
            session.Snapshot = Grant(Base.AddMinutes(10));
            var manual = opener.OpenManuallyAsync().GetAwaiter().GetResult();
            Require(manual == PortalOpenOutcome.Opened && session.Launched.Count == 1, "手动开页应直接执行");
            var auto = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(auto == PortalOpenOutcome.AlreadyConsumed, "手动开页应消费当前授予");
            // 手动打开后本地冷却生效：换发授予同样被拦下。
            session.Snapshot = Grant(Base.AddMinutes(20));
            var deferred = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(deferred == PortalOpenOutcome.CooldownActive, "手动开页应计入本地冷却");
            return Task.CompletedTask;
        }),
        ("开页：请求消失或不可信时不开页且不消费（故事68）", () =>
        {
            var session = new Session();
            var opener = session.NewOpener();
            session.Snapshot = Online();
            var gone = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(gone == PortalOpenOutcome.NotEligible && session.Launched.Count == 0, "请求消失不应开页");
            session.Snapshot = Grant(Base.AddMinutes(10));
            var untrusted = opener.EvaluateAsync(Untrusted(session.Snapshot)).GetAwaiter().GetResult();
            Require(untrusted == PortalOpenOutcome.NotEligible && session.Launched.Count == 0, "不可信读取不应开页");
            // 消费记录未被污染：冷却基线推进后授予仍可正常开页。
            session.Now = Base.AddMinutes(30);
            var opened = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(opened == PortalOpenOutcome.Opened && session.Launched.Count == 1, "失效读取不得污染消费记录");
            return Task.CompletedTask;
        }),
        ("开页：启动失败受控且不无限重试，手动重试仍可用（故事69）", () =>
        {
            var session = new Session(failingLaunch: true);
            var opener = session.NewOpener();
            session.Snapshot = Grant(Base.AddMinutes(10));
            var failed = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(failed == PortalOpenOutcome.LaunchFailed, "启动失败应返回受控结果");
            Require(session.Errors is ["StubbedException"], "应恰好一次受控错误类型");
            Require(opener.LastFailure is { } failure && failure.ErrorKind == "StubbedException", "应记录会话内受控失败");
            var retried = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(retried == PortalOpenOutcome.AlreadyConsumed && session.Errors.Count == 1, "轮询不得自动重试失败的浏览器启动");
            var manual = opener.OpenManuallyAsync().GetAwaiter().GetResult();
            Require(manual == PortalOpenOutcome.LaunchFailed && session.Errors.Count == 2, "手动重试入口应保持可用");
            return Task.CompletedTask;
        }),
        ("开页：消费记录保存失败时拒绝自动开页（决策11）", () =>
        {
            // 记录路径的父级是一个同名文件：保存必然失败（受控、离线确定）。
            var root = Directory.CreateTempSubdirectory("AutoNet-portalopen-").FullName;
            Cleanup.Register(root);
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "not a directory");
            var storePath = Path.Combine(blocked, PortalOpenStore.FileName);
            var launched = new List<string>();
            var opener = new PortalPageOpener(
                new PortalOpenStore(storePath),
                _ => Task.FromResult(Trusted(Grant(Base.AddMinutes(10)))),
                url => { launched.Add(url); return null; },
                () => Base);
            var outcome = opener.EvaluateAsync(Trusted(Grant(Base.AddMinutes(10)))).GetAwaiter().GetResult();
            Require(outcome == PortalOpenOutcome.SaveFailed && launched.Count == 0, "保存失败时不得自动开页");
            Require(opener.SaveFailurePending, "应标记保存失败状态供概览降级");
            var manual = opener.OpenManuallyAsync().GetAwaiter().GetResult();
            Require(manual == PortalOpenOutcome.SaveFailed && launched.Count == 0, "保存失败时手动开页同样不得执行");
            return Task.CompletedTask;
        }),
        ("开页：时间异常不得成为提前重复开页的理由（决策11）", () =>
        {
            var session = new Session();
            var opener = session.NewOpener();
            session.Snapshot = Grant(Base.AddMinutes(10));
            opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            session.Now = Base.AddMinutes(-5); // 时钟回拨：now 早于上次开页时刻
            session.Snapshot = Grant(Base.AddMinutes(20));
            var outcome = opener.EvaluateAsync(session.Reading()).GetAwaiter().GetResult();
            Require(outcome == PortalOpenOutcome.CooldownActive && session.Launched.Count == 1, "时钟回拨不得提前开页");
            return Task.CompletedTask;
        }),
        ("开页：记录按用户隔离、QA 重定向、与通知记录分开且损坏回退为空（故事70）", () =>
        {
            Require(PortalOpenStore.FileName != NotificationConsumptionStore.FileName, "开页与通知消费记录必须是两个文件");
            Require(PortalOpenStore.DefaultPath().EndsWith(PortalOpenStore.FileName, StringComparison.Ordinal)
                && PortalOpenStore.DefaultPath().Contains("AutoNet", StringComparison.Ordinal), "生产路径应在用户本地配置目录下");
            var qa = new QaEnvironment(QaMode.PortalOpen, Path.Combine(Path.GetTempPath(), "AutoNet-qa-fixture"));
            Require(PortalOpenStore.Create(qa).Load().Grants.Count == 0, "QA 激活时路径应指向证据目录");
            // 损坏文件回退为空记录（绝不抛出）；合法记录 roundtrip 按相同键读回。
            var root = Directory.CreateTempSubdirectory("AutoNet-portalopen-").FullName;
            Cleanup.Register(root);
            var record = Path.Combine(root, PortalOpenStore.FileName);
            File.WriteAllText(record, "{{{ not json");
            Require(new PortalOpenStore(record).Load().Grants.Count == 0, "损坏记录应回退为空");
            var store = new PortalOpenStore(record);
            var key = Base.AddMinutes(10).ToString("O");
            Require(store.TrySave(new Dictionary<string, DateTimeOffset> { [key] = Base }, Base), "合法记录应可保存");
            var loaded = store.Load();
            Require(loaded.Grants.Count == 1 && loaded.Grants.ContainsKey(key) && loaded.LastOpenAt == Base, "记录应按原键读回");
            // 任意非时刻键不得持久化（受控形状校验；调用方载荷中的合法键不受影响）。
            Require(store.TrySave(new Dictionary<string, DateTimeOffset> { [key] = Base, ["not-a-timestamp"] = Base }, Base),
                "含非法键的保存仍应成功（非法键被写入侧过滤）");
            var filtered = store.Load();
            Require(filtered.Grants.Count == 1 && filtered.Grants.ContainsKey(key), "非法键不得持久化，合法键保持不变");
            return Task.CompletedTask;
        })
    ];

    private static StatusSnapshot Grant(DateTimeOffset notBefore) => Online() with
    {
        BrowserFallbackRequested = true,
        BrowserFallbackNotBefore = notBefore
    };

    private static StatusSnapshot Online() => new()
    {
        UpdatedAt = Base,
        Enabled = true,
        State = "校园网在线",
        Wifi = "已连接",
        Direct = "外网可达",
        Portal = "未查询（外网检查已通过）",
        Tunnel = "系统路径可达，出口未核验",
        Uu = "未核验"
    };

    private static StatusReading Trusted(StatusSnapshot snapshot) =>
        new(snapshot, NetworkDisplayMap.Map(snapshot, TimeSpan.Zero), true, true, TimeSpan.Zero, Base);

    private static StatusReading Untrusted(StatusSnapshot snapshot) =>
        new(snapshot, NetworkDisplayMap.Map(snapshot, TimeSpan.FromHours(2)), true, false, TimeSpan.FromHours(2), Base);

    /// <summary>把临时目录登记到进程退出清理（测试宿主无生命周期钩子时的兜底）。</summary>
    private static class Cleanup
    {
        private static readonly List<string> Roots = [];
        private static bool registered;

        public static void Register(string root)
        {
            lock (Roots)
            {
                Roots.Add(root);
                if (registered) return;
                registered = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    lock (Roots)
                        foreach (var path in Roots)
                            try { Directory.Delete(path, true); } catch (Exception) { }
                };
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
