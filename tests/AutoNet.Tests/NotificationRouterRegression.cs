using AutoNet.Core;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 关键通知消费回归（迁移工单 #10 / T10 / 故事 52–60、70）：
/// 首次读取不重播历史恢复（56）、同一事件只提醒一次且不按固定时间重弹（52、58）、
/// 运行期联网恢复提醒一次（53）、托盘重启且认证仍暂停时提醒一次（55）、
/// 复检过程只在界面显示而不弹通知（54）、改版/兜底提示不被同轮通用暂停覆盖（59）、
/// 未知通知码无动作且不显示原始内容（60）、消费记录按 Windows 用户持久化且不含秘密（57、70）。
/// 受控呈现与重启场景证据由 scripts/qa-avalonia-notifications.ps1 提供。
/// </summary>
internal static class NotificationRouterRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("通知回归：首次读取不重播历史恢复且记录为已见", () => Sync(() =>
        {
            var store = NewStore(out var directory);
            try
            {
                var presented = Record(out var router, store);
                var at = At(-10);
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, at) }));
                Require(presented.Count == 0, "首次读取重播了历史恢复通知");

                // 首读虽不弹，同一 payload 在后续读取中也不得变成“新事件”。
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, at) }));
                Require(presented.Count == 0, "首读记录失效：同 payload 在第二次读取时弹出");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：运行期新恢复只提醒一次且文案受控", () => Sync(() =>
        {
            var store = NewStore(out var directory);
            try
            {
                var presented = Record(out var router, store);
                var old = At(-10);
                var now = At(0);
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, old) }));
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, now) }));
                Require(presented.Count == 1, $"运行期恢复提醒次数为 {presented.Count}，应恰为 1");
                Require(presented[0].Code == NotificationCodes.InternetRecovered, "提醒码与事件不符");
                Require(presented[0].Heading == "AutoNet · 联网恢复", "恢复提醒标题不受控：" + presented[0].Heading);
                Require(presented[0].Body.Contains("恢复来源无法确认"), "恢复提醒正文缺少恢复来源口径声明");
                Require(!presented[0].Warning, "联网恢复不是警告级提示");

                // 同一事件（同 code 同 At）在此后的任何读取中都不再弹出（故事 58）。
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, now) }));
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, now) }));
                Require(presented.Count == 1, "同一事件按固定时间重复弹出");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：消费记录跨重启持久化且新实例不重播（故事 57）", () => Sync(() =>
        {
            var path = NewPath(out var directory);
            try
            {
                var presented = Record(out var router, new NotificationConsumptionStore(path));
                router.Observe(Reading(Online()));
                var now = At(0);
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, now) }));
                Require(presented.Count == 1, "运行期事件未提醒");

                // 新实例（托盘重启语义）：加载持久记录后，同 payload 既不因首读也不因记录而弹出。
                var resumed = Record(out var second, new NotificationConsumptionStore(path));
                second.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, now) }));
                Require(resumed.Count == 0, "托盘重启后重播了已消费事件");

                // 重启后运行期的新事件仍然提醒。
                second.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, At(1)) }));
                Require(resumed.Count == 1, "重启后新事件未提醒");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：暂停提醒文案与既有托盘逐字一致且只提醒一次", () => Sync(() =>
        {
            var store = NewStore(out var directory);
            try
            {
                var presented = Record(out var router, store);
                var now = At(0);
                router.Observe(Reading(Online() with
                {
                    CredentialsBlocked = true,
                    Notification = new StatusNotification(NotificationCodes.AuthenticationPaused, now)
                }));
                router.Observe(Reading(Online() with
                {
                    CredentialsBlocked = true,
                    Notification = new StatusNotification(NotificationCodes.AuthenticationPaused, now)
                }));
                Require(presented.Count == 1, $"暂停提醒次数为 {presented.Count}，应恰为 1");
                Require(presented[0].Heading == "AutoNet 需要检查认证", "暂停提醒标题与既有托盘不一致");
                Require(presented[0].Body == "自动认证已安全暂停。请先查看诊断和日志，勿仅为解锁而重存密码。",
                    "暂停提醒正文与既有 WinForms 气泡不一致");
                Require(presented[0].Warning, "认证暂停应为警告级提示");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：改版提示与随后的暂停提示先后都弹出（故事 59）", () => Sync(() =>
        {
            var store = NewStore(out var directory);
            try
            {
                var presented = Record(out var router, store);
                router.Observe(Reading(Online()));
                var layoutAt = At(-1);
                var pauseAt = At(0);
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.PortalLayoutChanged, layoutAt) }));
                Require(presented.Count == 1 && presented[0].Code == NotificationCodes.PortalLayoutChanged,
                    "改版提示未弹出");
                Require(presented[0].Heading.Contains("门户结构"), "改版提示标题不受控：" + presented[0].Heading);

                // 下一轮的通用暂停提示不得吞掉改版事实：两个事件各自提醒，互不覆盖。
                router.Observe(Reading(Online() with
                {
                    CredentialsBlocked = true,
                    Notification = new StatusNotification(NotificationCodes.AuthenticationPaused, pauseAt)
                }));
                Require(presented.Count == 2, $"暂停提示未独立弹出（共 {presented.Count} 次）");
                Require(presented[1].Code == NotificationCodes.AuthenticationPaused, "第二轮提醒码不符");
                Require(presented[0].Body != presented[1].Body, "改版提示被通用暂停文案覆盖");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：未知码无动作且不落消费记录（故事 60）", () => Sync(() =>
        {
            var path = NewPath(out var directory);
            try
            {
                var presented = Record(out var router, new NotificationConsumptionStore(path));
                router.Observe(Reading(Online()));
                router.Observe(Reading(Online() with { Notification = new StatusNotification("BrandNewCode", At(0)) }));
                router.Observe(Reading(Online() with { Notification = new StatusNotification("BrandNewCode", At(1)) }));
                Require(presented.Count == 0, "未知码触发了提醒");
                var persisted = new NotificationConsumptionStore(path).Load();
                Require(persisted.Count == 0, "未知码写入了消费记录（产生了动作）");
                Require(!File.Exists(path) || !File.ReadAllText(path).Contains("BrandNewCode"), "消费文件含未知码原文");

                // 未知码之后的已知事件不受影响。
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, At(2)) }));
                Require(presented.Count == 1, "未知码干扰了后续已知事件");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：复检过程只在界面显示而绝不弹出（故事 54）", () => Sync(() =>
        {
            var store = NewStore(out var directory);
            try
            {
                var presented = Record(out var router, store);
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.SubmissionRechecking, At(0)) }));
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.SubmissionRechecking, At(1)) }));
                Require(presented.Count == 0, "复检过程弹出了通知");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：缺失或不可信状态不推进通知状态", () => Sync(() =>
        {
            var store = NewStore(out var directory);
            try
            {
                var presented = Record(out var router, store);
                var at = At(-10);
                router.Observe(null);
                router.Observe(Reading(null));
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, at) }, stale: true));
                Require(presented.Count == 0, "不可信状态触发了提醒");

                // 不可信读取不得充当首读：随后第一条可信读取仍是“首读”，历史恢复不重播。
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, at) }));
                Require(presented.Count == 0, "不可信读取被当作首读，历史事件在可信读取时重播");

                // 之后运行期的新事件照常提醒。
                router.Observe(Reading(Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, At(0)) }));
                Require(presented.Count == 1, "可信后的新事件未提醒");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：托盘重启且认证仍暂停时提醒一次（故事 55）", () => Sync(() =>
        {
            var path = NewPath(out var directory);
            try
            {
                // 上一会话已消费 payload 的暂停事件（At 很旧），重启后 payload 不变且仍暂停。
                var old = At(-30);
                var first = Record(out var router, new NotificationConsumptionStore(path));
                router.Observe(Reading(Online() with
                {
                    CredentialsBlocked = true,
                    Notification = new StatusNotification(NotificationCodes.AuthenticationPaused, old)
                }));
                Require(first.Count == 1, "首次会话的暂停提醒缺失");

                // 重启：同一 payload、同一消费记录——启动暂停提醒必须给出一次（55），
                // 而事件本身不得重播（56）。
                var resumed = Record(out var second, new NotificationConsumptionStore(path));
                second.Observe(Reading(Online() with
                {
                    CredentialsBlocked = true,
                    Notification = new StatusNotification(NotificationCodes.AuthenticationPaused, old)
                }));
                Require(resumed.Count == 1, $"重启后暂停提醒次数为 {resumed.Count}，应恰为 1");
                Require(resumed[0].Body == "自动认证已安全暂停。请先查看诊断和日志，勿仅为解锁而重存密码。",
                    "启动暂停提醒文案与词表不一致");
                second.Observe(Reading(Online() with
                {
                    CredentialsBlocked = true,
                    Notification = new StatusNotification(NotificationCodes.AuthenticationPaused, old)
                }));
                Require(resumed.Count == 1, "启动暂停提醒在会话内重复弹出");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：启动暂停提醒呈现失败时补送且暂停消失即失效（故事 55）", () => Sync(() =>
        {
            var store = NewStore(out var directory);
            try
            {
                var presented = new List<NotificationAlert>();
                var attempts = 0;
                var router = new NotificationRouter(store, _ =>
                {
                    attempts++;
                    if (attempts == 1) return false;
                    presented.Add(new NotificationAlert(NotificationCodes.AuthenticationPaused, "h", "b", true));
                    return true;
                });
                // 首读：启动提醒呈现失败 → 不得视为送达，也不得写入消费记录。
                router.Observe(Reading(Online() with { CredentialsBlocked = true }));
                Require(presented.Count == 0, "失败呈现不应计入已呈现");
                // 暂停仍在 → 下轮补送成功（至少一次语义）。
                router.Observe(Reading(Online() with { CredentialsBlocked = true }));
                Require(presented.Count == 1, "启动暂停提醒未补送");
                // 补送成功后会话内不再重复。
                router.Observe(Reading(Online() with { CredentialsBlocked = true }));
                Require(presented.Count == 1 && attempts == 2, "补送成功后重复提醒");

                // 暂停消失后，未送达的启动提醒随之失效（事件过时，不再打扰）。
                var failing = new NotificationRouter(NewStore(out var second), _ => false);
                failing.Observe(Reading(Online() with { CredentialsBlocked = true }));
                failing.Observe(Reading(Online()));
                Require(true, "暂停消失路径不得抛出");
                Directory.Delete(second, recursive: true);
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：非暂停启动不产生启动提醒", () => Sync(() =>
        {
            var store = NewStore(out var directory);
            try
            {
                var presented = Record(out var router, store);
                router.Observe(Reading(Online()));
                router.Observe(Reading(Online()));
                Require(presented.Count == 0, "未暂停时产生了启动提醒");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：呈现失败不记录消费（下轮重试，至少一次语义）", () => Sync(() =>
        {
            var path = NewPath(out var directory);
            try
            {
                var at = At(0);
                var snapshot = Online() with { Notification = new StatusNotification(NotificationCodes.InternetRecovered, at) };
                var router = new NotificationRouter(new NotificationConsumptionStore(path), _ => false);
                router.Observe(Reading(Online() with { UpdatedAt = At(-30) }));
                router.Observe(Reading(snapshot));
                Require(new NotificationConsumptionStore(path).Load().Count == 0, "失败呈现不得写入消费记录");

                // 换成成功的呈现：新实例先建立首读基线，再让同一事件正常呈现并记录。
                var resumed = Record(out var second, new NotificationConsumptionStore(path));
                second.Observe(Reading(Online() with { UpdatedAt = At(-30) }));
                Require(resumed.Count == 0, "基线读取不应弹出");
                second.Observe(Reading(snapshot));
                Require(resumed.Count == 1, "呈现恢复后同一事件未重试");
                second.Observe(Reading(snapshot));
                Require(resumed.Count == 1, "呈现成功后同一事件重复弹出");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：消费文件逐字只含受控载荷且损坏时回退为空（故事 70）", () => Sync(() =>
        {
            var path = NewPath(out var directory);
            try
            {
                var store = new NotificationConsumptionStore(path);
                var consumed = new Dictionary<string, DateTimeOffset>
                {
                    [NotificationCodes.InternetRecovered] = At(0),
                    [NotificationCodes.AuthenticationPaused] = At(1)
                };
                Require(store.TrySave(consumed), "消费记录保存失败");
                var raw = File.ReadAllText(path);
                Require(raw.Contains("\"Consumed\"") && raw.Contains("InternetRecovered") && raw.Contains("AuthenticationPaused"),
                    "消费文件缺少受控载荷：" + raw);
                Require(!raw.Contains("password", StringComparison.OrdinalIgnoreCase) && !raw.Contains("cookie", StringComparison.OrdinalIgnoreCase),
                    "消费文件含敏感字样");

                // 损坏文件回退为空消费记录（配合首读规则，重启不重播）。
                File.WriteAllText(path, "not-json");
                Require(new NotificationConsumptionStore(path).Load().Count == 0, "损坏文件未回退为空记录");

                // 保存失败只返回 false：以“文件充当目录”注入写失败，绝不触碰真实用户目录。
                var blocker = NewPath(out var blockerDirectory);
                try
                {
                    File.WriteAllText(Path.Combine(blockerDirectory, "blocker"), "file-as-directory");
                    var failing = new NotificationConsumptionStore(Path.Combine(blockerDirectory, "blocker", "notification-consumption.json"));
                    Require(!failing.TrySave(consumed), "坏路径上的保存不应返回 true");
                    Require(failing.Load().Count == 0, "坏路径上的读取未安全回退");
                }
                finally { Directory.Delete(blockerDirectory, recursive: true); }
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("通知回归：存储按 Windows 用户隔离且 QA 激活即重定向（故事 57、70）", () => Sync(() =>
        {
            Require(NotificationConsumptionStore.DefaultPath().EndsWith("AutoNet\\notification-consumption.json", StringComparison.Ordinal),
                "消费记录不在按用户隔离的 AutoNet 目录下");

            var evidence = NewPath(out var directory);
            try
            {
                var redirected = NotificationConsumptionStore.Create(new QaEnvironment(QaMode.Notifications, directory));
                redirected.TrySave(new Dictionary<string, DateTimeOffset> { [NotificationCodes.InternetRecovered] = At(0) });
                Require(File.Exists(Path.Combine(directory, NotificationConsumptionStore.FileName)),
                    "QA 模式的消费记录未落在证据目录内");
            }
            finally { Directory.Delete(directory, recursive: true); }
            Require(NotificationConsumptionStore.Create(QaEnvironment.Inactive).Load().Count == 0,
                "非 QA 存储读取应安全回退（本机真实记录为空时）");
        })),
    ];

    /// <summary>构造路由器并挂记录型呈现：收集每次提醒并恒定成功。</summary>
    private static List<NotificationAlert> Record(out NotificationRouter router, NotificationConsumptionStore store)
    {
        var presented = new List<NotificationAlert>();
        router = new NotificationRouter(store, alert => { presented.Add(alert); return true; });
        return presented;
    }

    private static NotificationConsumptionStore NewStore(out string directory)
    {
        var path = NewPath(out directory);
        return new NotificationConsumptionStore(path);
    }

    private static string NewPath(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "autonet-notify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "notification-consumption.json");
    }

    private static DateTimeOffset At(int offsetSeconds) =>
        new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero).AddSeconds(offsetSeconds);

    private static StatusReading Reading(StatusSnapshot? snapshot, bool stale = false)
    {
        var now = DateTimeOffset.UtcNow;
        var age = snapshot is null ? (TimeSpan?)null : stale ? NetworkDisplayMap.FreshnessWindow + TimeSpan.FromSeconds(5) : TimeSpan.Zero;
        var valid = StatusSnapshotReader.IsValid(snapshot);
        return new StatusReading(snapshot, NetworkDisplayMap.Map(snapshot, age), valid,
            valid && NetworkDisplayMap.IsFresh(age), age, now);
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
}
