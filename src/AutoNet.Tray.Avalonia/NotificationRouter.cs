using System.Globalization;
using System.Text.Json;
using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>一次托盘提醒：受控词表的标题、正文与语义级别；载荷绝不含状态原文或异常文本。</summary>
internal sealed record NotificationAlert(string Code, string Heading, string Body, bool Warning);

/// <summary>
/// 通知消费记录（#10 / 故事 57、70）：按 Windows 用户持久化「哪些事件已提醒过」，
/// 托盘重启不造成普通事件反复提醒。存储在当前用户本地配置目录（独立于 ProgramData
/// 受限业务存储），载荷只含通知码与事件时刻——不含账号、密码、Cookie、Token 或任何
/// 状态原文。读取失败（缺失、损坏）一律回退为空记录（配合首读规则仍不会重播历史）；
/// 保存失败只返回 false，绝不因通知记录打扰用户。QA 激活（AUTONET_QA_DIR 存在）即
/// 重定向到证据目录：QA 既不读写本机真实记录，也不因开发者机器上的真实记录产生
/// 不确定的证据（与 <see cref="ThemePreferenceStore"/> 同一约定）。
/// </summary>
internal sealed class NotificationConsumptionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>记录文件名（QA 脚本与本机路径共用同一约定）。</summary>
    public const string FileName = "notification-consumption.json";

    private readonly string path;

    public NotificationConsumptionStore(string path) => this.path = path;

    /// <summary>生产路径：当前 Windows 用户的本地配置目录（按用户隔离，故事 57）。</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoNet",
        FileName);

    /// <summary>按运行环境选择路径：QA 激活时一律使用证据目录，普通模式使用真实用户目录。</summary>
    public static NotificationConsumptionStore Create(QaEnvironment qa) =>
        new(qa.Active ? Path.Combine(qa.Directory, FileName) : DefaultPath());

    /// <summary>读取消费记录；缺失、损坏或形状不符时回退为空（绝不抛出）。</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Load()
    {
        try
        {
            var file = JsonSerializer.Deserialize<ConsumptionFile>(File.ReadAllText(path), Options);
            if (file?.Consumed is not { } entries) return Empty;
            var consumed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var (code, at) in entries)
                if (NotificationCodes.IsKnown(code)
                    && at != default
                    && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment))
                    consumed[code] = moment;
            return consumed;
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    /// <summary>原子保存消费记录；成功返回 true，任何失败只返回 false（通知记录不值得打扰用户）。</summary>
    public bool TrySave(IReadOnlyDictionary<string, DateTimeOffset> consumed)
    {
        var payload = JsonSerializer.Serialize(new ConsumptionFile(ToEntries(consumed)), Options);
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.WriteAllText(temporary, payload);
                    File.Move(temporary, path, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly IReadOnlyDictionary<string, DateTimeOffset> Empty =
        new Dictionary<string, DateTimeOffset>();

    private static Dictionary<string, string> ToEntries(IReadOnlyDictionary<string, DateTimeOffset> consumed)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, at) in consumed)
            if (NotificationCodes.IsKnown(code))
                entries[code] = at.ToString("O");
        return entries;
    }

    /// <summary>记录文件载荷：逐字只含已消费事件映射（通知码 → 事件时刻），不含任何其他字段。</summary>
    internal sealed record ConsumptionFile(Dictionary<string, string> Consumed);
}

/// <summary>
/// 关键通知路由（#10 / T10 / 故事 52–60）：把状态管道的每次读取映射为「是否提醒」的受控决定。
///
/// 服务端语义（MonitorWorker.Notify）：payload 只保留最新一条一次性通知并持久存在到被替代，
/// 事件时刻 <see cref="StatusNotification.At"/> 只在事件发生时前进；每周期最多一条，更具体的
/// 事件（改版、兜底）优先于通用暂停提示（故事 59 的服务端侧保障）。
///
/// 托盘侧规则（按序，全部构造性满足对应故事）：
/// 1. 不可信读取（缺失/损坏/过期）不推进任何状态，也不提醒（不依据陈旧信息说话，故事 15 的通知面）。
/// 2. 首条可信读取是「首读」：payload 携带的一切事件只记录不提醒——历史恢复不重播（故事 56），
///    服务端 payload 持久保留使重启后的旧事件无法与真新事件区分，首读规则给出安全侧。
/// 3. 首条可信读取若认证仍暂停 → 启动暂停提醒（每次托盘会话至少送达一次，故事 55；暂停是
///    当前状态而非一次性事件，与规则 2 的事件重播禁止刻意相反）。呈现失败不视为送达，
///    暂停仍存在时下轮重试；暂停消失后未送达的提醒随之失效。启动提醒刻意不落消费记录：
///    它由会话内状态防重，消费记录反而会让重启后的会话把「已提醒」误判为「已消费事件」。
/// 4. 首读之后，词表内已知码且事件时刻不同于消费记录 → 提醒一次并记录（内存 + 持久，
///    故事 52、53、57）；同一事件（同码同刻）此后不再提醒，服务端不重发、托盘不重弹，
///    因此不存在按固定时间重弹（故事 58）。呈现失败不记录：事件保留到下轮重试（至少一次
///    语义，宁可重试不丢失）。
/// 5. 复检过程（SubmissionRechecking）不在词表内 → 永不提醒，只在界面显示（故事 54）。
/// 6. 未知码不在词表内 → 无动作、无文案、不落记录；词表查不到即没有可显示的文本，
///    构造性保证不显示原始不可信内容（故事 60）。
///
/// 线程模型：<see cref="Observe"/> 只经 <see cref="StatusPipe.Apply"/> 的锁内回调触发
/// （轮询与主动刷新共享同一把状态锁），决定状态天然串行，无需额外加锁。
///
/// 本类不依赖 UI 类型：呈现经委托注入（返回 false 表示呈现失败），可直接受控测试。
/// </summary>
internal sealed class NotificationRouter
{
    /// <summary>
    /// 受控词表（托盘拥有措辞；<see cref="StatusNotification"/> 不携带文本）。键即已知码；
    /// SubmissionRechecking 刻意缺席——复检过程只在界面显示（故事 54）。
    /// 暂停条目正文与既有 WinForms 托盘气泡逐字一致。
    /// </summary>
    private static readonly Dictionary<string, NotificationAlert> Vocabulary = new(StringComparer.Ordinal)
    {
        [NotificationCodes.InternetRecovered] = new(
            NotificationCodes.InternetRecovered,
            "AutoNet · 联网恢复",
            "已观测到外网重新可达；恢复来源无法确认，可能包含人工处理或网络自行恢复。",
            Warning: false),
        [NotificationCodes.AuthenticationPaused] = new(
            NotificationCodes.AuthenticationPaused,
            "AutoNet 需要检查认证",
            "自动认证已安全暂停。请先查看诊断和日志，勿仅为解锁而重存密码。",
            Warning: true),
        [NotificationCodes.SubmissionUnresolved] = new(
            NotificationCodes.SubmissionUnresolved,
            "AutoNet 需要人工认证",
            "自动认证的结果未能确认，等待人工处理；请查看网络诊断页。",
            Warning: true),
        [NotificationCodes.PortalLayoutChanged] = new(
            NotificationCodes.PortalLayoutChanged,
            "AutoNet · 门户结构疑似变化",
            "门户页面结构疑似发生变化，自动认证已暂停；请查看网络诊断页了解详情。",
            Warning: true),
        // 浏览器兜底（故事 61–69）的开页动作属迁移工单 #11；本票只做提示。
        [NotificationCodes.BrowserFallbackRequested] = new(
            NotificationCodes.BrowserFallbackRequested,
            "AutoNet · 需要浏览器认证",
            "连续认证失败，需要使用浏览器完成校园网认证。",
            Warning: true),
    };

    /// <summary>启动暂停提醒：与暂停词表同文案——两者描述同一事实（认证仍在暂停）。</summary>
    private static NotificationAlert StartupPauseAlert => Vocabulary[NotificationCodes.AuthenticationPaused];

    private readonly NotificationConsumptionStore store;
    private readonly Func<NotificationAlert, bool> present;
    private readonly Dictionary<string, DateTimeOffset> consumed;
    private bool primed;
    private bool startupPausePending;

    public NotificationRouter(NotificationConsumptionStore store, Func<NotificationAlert, bool> present)
    {
        this.store = store;
        this.present = present;
        consumed = new Dictionary<string, DateTimeOffset>(store.Load(), StringComparer.Ordinal);
    }

    /// <summary>消费记录（内存视图 = 持久加载 + 会话内新增）；供 QA 与诊断读取。</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Consumed => consumed;

    /// <summary>
    /// 观察一次状态读取（状态读取线程上触发；呈现回调也在该线程，UI 侧负责调度）。
    /// 不可信读取直接忽略：绝不依据陈旧信息提醒，也绝不把不可信读取当作首读。
    /// </summary>
    public void Observe(StatusReading? reading)
    {
        if (reading is not { Trusted: true, Snapshot: { } snapshot }) return;

        if (!primed)
        {
            primed = true;
            // 首读：payload 的一切事件只记录不提醒（故事 56——历史恢复不重播）。
            // 词表缺席（复检过程）的事件不落记录：它们永远不会被提醒（故事 54）。
            if (snapshot.Notification is { } first && Vocabulary.ContainsKey(first.Code))
                Remember(first);
            // 启动暂停提醒（故事 55）：暂停是当前状态而非一次性事件，重启托盘必须让用户
            // 仍能知道待处理事项。呈现失败不视为送达，暂停仍存在时下轮重试（见下方）。
            if (snapshot.CredentialsBlocked && !present(StartupPauseAlert))
                startupPausePending = true;
            return;
        }

        // 启动暂停提醒的补送：只在暂停仍然成立时重试（暂停消失后提醒过时，随之失效）。
        if (startupPausePending)
        {
            if (!snapshot.CredentialsBlocked || present(StartupPauseAlert)) startupPausePending = false;
        }

        if (snapshot.Notification is not { } current) return;
        if (!NotificationCodes.IsKnown(current.Code)) return; // 故事 60：无动作、无文案、不记录
        // 词表缺席（复检过程）= 只在界面显示的事件：绝不提醒（故事 54）。
        if (!Vocabulary.TryGetValue(current.Code, out var alert)) return;
        if (consumed.TryGetValue(current.Code, out var seen) && seen == current.At) return; // 故事 58
        // 呈现失败不记录：同一事件下轮重试（至少一次语义）；呈现成功才落消费记录。
        if (!present(alert)) return;
        Remember(current);
    }

    /// <summary>把事件记入内存与持久消费记录；保存失败只放弃持久化（会话内仍然防重弹）。</summary>
    private void Remember(StatusNotification notification)
    {
        consumed[notification.Code] = notification.At;
        store.TrySave(consumed);
    }
}
