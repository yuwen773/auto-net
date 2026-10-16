using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>日志列表筛选（迁移工单 #7）：与既有 WinForms 日志中心的四个筛选逐字一致。</summary>
internal enum LogsFilter
{
    All,
    State,
    Recovery,
    Authentication
}

/// <summary>
/// 日志列表一行：时间、事件、说明预览与该行详情。详情在投影时按新旧日志规则渲染成受控文本
/// （故事 46、51），选中即显示；一条记录渲染失败绝不影响其他行或整页（故事 49）。
/// </summary>
internal sealed record LogsRow(string Time, string Action, string Preview, string Detail);

/// <summary>日志页的可变读取状态：由 <see cref="LogsPage"/> 维护，<see cref="LogsModel"/> 据此投影内容。</summary>
internal sealed record LogsState(
    IReadOnlyList<string> Dates,
    string? SelectedDate,
    LogsFilter Filter,
    int SelectedIndex,
    IReadOnlyList<ActivityLog> Entries,
    int Skipped,
    bool Complete,
    string? FailureType,
    bool Loading)
{
    public static readonly LogsState Initial = new([], null, LogsFilter.All, -1, [], 0, true, null, false);
}

/// <summary>
/// 日志页内容投影（迁移工单 #7 / T09）：把一次受限日志读取映射为日期入口、受限列表、
/// 显式范围标注、缺口说明、详情与脱敏复制文本；不依赖 UI 类型，可直接受控测试。
/// 范围与缺口显式成文（故事 45、49）；复制只输出受控白名单字段（故事 47）；
/// 读取失败与空目录都有固定受控文案，绝不让页面失效（故事 51、安全边界）。
/// </summary>
internal sealed record LogsContent : PageContent
{
    internal LogsContent() : base(ShellPage.Logs, ShellPages.Info(ShellPage.Logs).Name) { }

    /// <summary>日期入口（故事 44）：单日日志文件名，最新在前。</summary>
    public IReadOnlyList<string> Dates { get; init; } = [];

    /// <summary>当前选中的日期入口；目录为空时为 null。</summary>
    public string? SelectedDate { get; init; }

    public LogsFilter Filter { get; init; } = LogsFilter.All;

    /// <summary>受限列表（故事 45）：按筛选后的可见记录投影，最新在前。</summary>
    public IReadOnlyList<LogsRow> Rows { get; init; } = [];

    /// <summary>读取范围显式标注（故事 45）：显示/已读计数、单日上限，以及列表是否只含当日最新记录。</summary>
    public string ScopeNote { get; init; } = "";

    /// <summary>缺口说明（故事 49）：有跳过的损坏记录时非空。</summary>
    public string GapNote { get; init; } = "";

    /// <summary>读取失败说明（安全边界）：只含异常类型名，绝不回显异常消息。</summary>
    public string FailureNote { get; init; } = "";

    /// <summary>当前选中行；-1 表示没有选中。</summary>
    public int SelectedIndex { get; init; } = -1;

    /// <summary>详情区文本（故事 46）：选中行的受控渲染详情（含验证脚注），或空态/读取提示。</summary>
    public string DetailText { get; init; } = "";

    /// <summary>脱敏复制文本（故事 47）：受控白名单摘要 + 条数说明；没有可复制内容时为空。</summary>
    public string CopyText { get; init; } = "";

    /// <summary>渲染指纹：全部可见字段的稳定拼接；不变时视图跳过重建（不打断选择与焦点）。</summary>
    public string ContentKey { get; init; } = "";
}

/// <summary>
/// 日志页投影规则（#7 / T09）。条目来自 <see cref="LogTailReader.ReadDetailed"/> 的受限尾部读取；
/// 筛选沿用既有 <see cref="LogEventClassifier"/> 分类；详情沿用既有新旧日志渲染规则（故事 51）；
/// 复制沿用既有 <see cref="LogSummary"/> 白名单摘要（故事 47）。规则无 I/O、无副作用。
/// </summary>
internal static class LogsModel
{
    /// <summary>详情脚注：与既有日志中心逐字一致的验证边界声明。</summary>
    public const string ValidationNotice = "物理出口、UU 会话与真实认证仍待现场验收；此处记录不等于已通过验证。";

    public const string EmptyDirectoryNote = "该目录还没有日志文件。";
    public const string ReadFailureNote = "暂时无法读取该日志文件；可稍后刷新重试。";
    public const string LoadingNote = "正在读取…";
    public const string UnreadableDetailNote = "该条日志详情不可读取；可选择其他事件或刷新重试。";
    public const string NoFilterMatchNote = "没有符合筛选条件的事件。";
    public const string NoSelectionNote = "请选择一个事件查看技术诊断详情。";

    /// <summary>把一次读取状态投影为日志页内容。</summary>
    public static LogsContent From(LogsState state)
    {
        var content = new LogsContent
        {
            Dates = state.Dates,
            SelectedDate = state.SelectedDate,
            Filter = state.Filter,
            SelectedIndex = state.SelectedIndex
        };

        if (state.FailureType is { } failure)
        {
            // 读取失败：固定受控降级，只披露异常类型（安全边界）；列表清空，绝不沿用上次画面。
            return Keyed(content with
            {
                Rows = [],
                ScopeNote = "",
                GapNote = "",
                FailureNote = "读取失败：" + failure,
                DetailText = ReadFailureNote + Environment.NewLine + Environment.NewLine + ValidationNotice,
                CopyText = ""
            });
        }

        if (state.Dates.Count == 0)
        {
            return Keyed(content with
            {
                Rows = [],
                ScopeNote = EmptyDirectoryNote,
                GapNote = "",
                DetailText = EmptyDirectoryNote,
                CopyText = ""
            });
        }

        if (state.Loading)
        {
            return Keyed(content with
            {
                Rows = [],
                ScopeNote = LoadingNote,
                GapNote = "",
                DetailText = LoadingNote,
                CopyText = ""
            });
        }

        var visible = state.Entries.Where(entry => Matches(state.Filter, entry)).ToList();
        var rows = visible.Select(ToRow).ToList();
        // 无历史选中时默认选中最新一行（与既有日志中心一致）；越界选中收敛到列表范围内。
        var selectedIndex = rows.Count == 0 ? -1 : state.SelectedIndex < 0 ? 0 : Math.Min(state.SelectedIndex, rows.Count - 1);
        return Keyed(content with
        {
            Rows = rows,
            SelectedIndex = selectedIndex,
            ScopeNote = ScopeNote(visible.Count, state.Entries.Count, state.Complete),
            GapNote = state.Skipped > 0
                ? $"已跳过 {state.Skipped} 条无法读取的记录；对应时刻的事件可能缺失。"
                : "",
            DetailText = selectedIndex >= 0 ? rows[selectedIndex].Detail : NoFilterMatchNote + Environment.NewLine + Environment.NewLine + ValidationNotice,
            CopyText = CopyText(visible, state.Entries.Count)
        });
    }

    private static LogsContent Keyed(LogsContent content) => content with { ContentKey = Fingerprint(content) };

    /// <summary>
    /// 日期入口枚举（故事 44）：文件名匹配既有单日日志形状，且中段必须是真实日历日期，
    /// 最新在前；目录不存在按空处理。手误命名的文件不进入入口，也不影响合法日期。
    /// </summary>
    public static IReadOnlyList<string> DateEntries(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "autonet-????-??-??.jsonl")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(IsDailyLogName)
            .OrderDescending()
            .ToList();
    }

    /// <summary>autonet-yyyy-MM-dd.jsonl 的严格校验：通配符之外再核对日历日期（如 autonet-2026-13-99.jsonl 不是日志入口）。</summary>
    public static bool IsDailyLogName(string name)
    {
        const string prefix = "autonet-";
        const string suffix = ".jsonl";
        if (name.Length != prefix.Length + 10 + suffix.Length
            || !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)) return false;
        return DateTimeOffset.TryParseExact(
            name[prefix.Length..(name.Length - suffix.Length)],
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out _);
    }

    /// <summary>筛选标签（与既有日志中心逐字一致）。</summary>
    public static string FilterLabel(LogsFilter filter) => filter switch
    {
        LogsFilter.All => "全部事件",
        LogsFilter.State => "状态变化",
        LogsFilter.Recovery => "恢复动作",
        LogsFilter.Authentication => "认证相关",
        _ => "全部事件"
    };

    public static IReadOnlyList<LogsFilter> Filters { get; } = [LogsFilter.All, LogsFilter.State, LogsFilter.Recovery, LogsFilter.Authentication];

    /// <summary>筛选语义：全部放行，其余按既有事件分类匹配。</summary>
    public static bool Matches(LogsFilter filter, ActivityLog entry) => filter switch
    {
        LogsFilter.All => true,
        LogsFilter.State => LogEventClassifier.Classify(entry) == LogEventCategory.State,
        LogsFilter.Recovery => LogEventClassifier.Classify(entry) == LogEventCategory.Recovery,
        LogsFilter.Authentication => LogEventClassifier.Classify(entry) == LogEventCategory.Authentication,
        _ => true
    };

    /// <summary>
    /// 读取范围标注（故事 45）：既有日志中心的计数口径，加显式的「更早记录不在列表」声明，
    /// 不把列表尾部误认为完整历史。
    /// </summary>
    public static string ScopeNote(int visible, int loaded, bool complete)
    {
        var text = $"显示 {visible} 条 / 已读取 {loaded} 条（单日最多 {LogTailReader.DefaultMaxRecords} 条）";
        return complete
            ? text + "；已包含当日全部记录。"
            : text + "；本列表只含当日最新记录，更早的记录请通过「日志目录」查看。";
    }

    /// <summary>
    /// 脱敏复制文本（故事 47）：沿用既有 <see cref="LogSummary"/> 白名单摘要——
    /// 自由文本（Detail、LastError 等）从不进入输出；只复制可见行，并附条数说明。
    /// </summary>
    public static string CopyText(IReadOnlyList<ActivityLog> visible, int loaded) =>
        visible.Count == 0
            ? ""
            : LogSummary.Format(visible) + Environment.NewLine + Environment.NewLine + LogSummary.Notice(loaded, visible.Count);

    /// <summary>列表预览截断长度：与既有日志中心逐字一致。</summary>
    private const int PreviewMaxLength = 56;

    /// <summary>一行预览：服务撰写的说明压成单行并截断，保持列表可读。</summary>
    public static string Preview(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "";
        var line = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= PreviewMaxLength ? line : line[..PreviewMaxLength] + "…";
    }

    /// <summary>
    /// 行详情渲染（故事 46、51）：新版记录（SchemaVersion ≥ 2）用 Core 的受控可读文本；
    /// 旧版记录按既有日志中心的兼容文案逐字渲染。单行失败只降级该行，不使整页失效（故事 49）。
    /// </summary>
    public static string DetailText(ActivityLog entry)
    {
        try
        {
            return entry.SchemaVersion >= 2 ? entry.ToReadableText() : LegacyDetail(entry);
        }
        catch (Exception e)
        {
            // 安全边界：只披露异常类型，绝不回显异常消息。
            return UnreadableDetailNote + Environment.NewLine + "详情渲染失败：" + e.GetType().Name;
        }
    }

    /// <summary>旧版记录的兼容详情：与既有日志中心的降级文案逐字一致（故事 51）。</summary>
    private static string LegacyDetail(ActivityLog entry)
    {
        var lines = new List<string>
        {
            $"时间：{entry.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            $"事件：{entry.Action}",
            $"Wi-Fi：{entry.Wifi ?? "未知"}",
            $"认证：{entry.Portal ?? "未知"}",
            $"直连：{entry.Direct ?? "未知"}",
            $"自动动作：{entry.Attempts ?? 0} 次　主动断线：{entry.DisconnectAttempts ?? 0} 次"
        };
        if (entry.Seconds is { } seconds) lines.Add($"观测耗时：{seconds:0.##} 秒（从首次发现故障算起，不是端到端实测）");
        if (entry.OutageSince is { } outage) lines.Add($"首次发现故障：{outage.ToLocalTime():HH:mm:ss}");
        if (entry.NextAttempt is { } next) lines.Add($"下次尝试：{next.ToLocalTime():HH:mm:ss}");
        if (entry.AuthenticationStage is { } stage && stage != nameof(PortalStage.Unknown)) lines.Add($"认证阶段：{stage}");
        if (entry.AuthenticationFailure is { } failure && failure != nameof(PortalFailure.None)) lines.Add($"失败分类：{failure}");
        if (entry.AuthenticationPersistentState is { } persistent && persistent != nameof(AuthenticationState.Ready)) lines.Add($"认证持久状态：{persistent}");
        if (entry.AuthenticationHttpStatus is { } http) lines.Add($"HTTP 状态：{http}");
        if (entry.CredentialRequestStarted == true) lines.Add("已发起凭据请求");
        lines.Add("");
        lines.Add("说明：");
        lines.Add(entry.Detail);
        return string.Join(Environment.NewLine, lines);
    }

    private static LogsRow ToRow(ActivityLog entry) => new(
        entry.Time.ToLocalTime().ToString("HH:mm:ss"),
        entry.Action,
        Preview(entry.Detail),
        DetailText(entry) + Environment.NewLine + Environment.NewLine + ValidationNotice);

    private static string Fingerprint(LogsContent content) => string.Join("\u001f",
        string.Join("\u001e", content.Dates),
        content.SelectedDate,
        content.Filter,
        content.SelectedIndex,
        content.ScopeNote,
        content.GapNote,
        content.FailureNote,
        string.Join("\u001e", content.Rows.Select(row => row.Time + ":" + row.Action + ":" + row.Preview + ":" + row.Detail)),
        content.DetailText,
        content.CopyText);
}
