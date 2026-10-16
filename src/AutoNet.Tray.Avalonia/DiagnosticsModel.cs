using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>诊断页一行证据：类别名、当前值、证据边界说明与语义色；文字与颜色同时呈现。</summary>
internal sealed record DiagnosticsRow(string Caption, string Value, string Note, OverviewTone Tone);

/// <summary>
/// 网络诊断页内容投影（迁移工单 #6 / T03）：把一次状态读取映射为五类诊断证据、认证信号与
/// 受控认证分类，不依赖 UI 类型，可直接受控测试。页面完全只读（故事 23）：内容纯投影自
/// 状态管道，不发起任何检测、不提交凭据、不触发认证或断线；证据由后台服务产生。
/// 缺失、过期、损坏或不可信状态一律降级为「不可确认」，绝不沿用上一次画面（与概览同一可信度规则）。
/// </summary>
internal sealed record DiagnosticsContent : PageContent
{
    internal DiagnosticsContent() : base(ShellPage.Diagnostics, ShellPages.Info(ShellPage.Diagnostics).Name) { }

    /// <summary>状态边界（完整性、时间有效性）是否允许依据其内容显示证据。</summary>
    public bool Trusted { get; init; }

    /// <summary>五类诊断分列（故事 18）：校园 Wi-Fi、认证、直连、代理/系统选路、UU（本机探测与远程会话分列）。</summary>
    public IReadOnlyList<DiagnosticsRow> Categories { get; init; } = [];

    /// <summary>认证信号（故事 21）：认证提交暂停与门户结构疑似变化分别呈现，互不改写。</summary>
    public IReadOnlyList<DiagnosticsRow> Signals { get; init; } = [];

    /// <summary>最近认证尝试的受控阶段与失败分类（故事 22）：不暴露原始响应或敏感 URL。</summary>
    public IReadOnlyList<DiagnosticsRow> Attempts { get; init; } = [];

    /// <summary>渲染指纹：全部可见字段的稳定拼接；不变时视图跳过重建（状态轮询不打断焦点）。</summary>
    public string ContentKey { get; init; } = "";
}

/// <summary>
/// 诊断页投影规则（#6 / T03）。证据值来自快照的五个分类字段；证据边界说明为常量，
/// 即使旧版服务的值缺少推断标注，页面也始终声明「系统选路是推断」「物理出口未核验」
/// 「远程会话未实测」的口径（故事 19、20）。规则只读快照，无 I/O、无副作用。
/// </summary>
internal static class DiagnosticsModel
{
    /// <summary>只读边界声明（故事 23）：页面脚注逐字呈现。</summary>
    public const string ReadOnlyNotice =
        "本页只读：仅展示后台服务的检测证据，不发起检测、不提交凭据、不触发认证或断线。";

    /// <summary>不可信状态的统一降级说明。</summary>
    private const string UnconfirmedNote = "状态不可确认；等待下一次有效检测。";

    /// <summary>把一次状态读取投影为诊断页内容；<paramref name="reading"/> 为 null 表示尚未取得状态。</summary>
    public static DiagnosticsContent From(StatusReading? reading)
    {
        var trusted = reading?.Trusted == true;
        var snapshot = trusted ? reading!.Snapshot : null;
        var content = new DiagnosticsContent
        {
            Trusted = trusted,
            Categories = snapshot is null ? UnconfirmedCategories() : Categories(snapshot),
            Signals = snapshot is null
                ? [new DiagnosticsRow("认证信号", "不可确认", "当前状态不可确认，无法判断认证暂停或门户结构信号。", OverviewTone.Neutral)]
                : Signals(snapshot),
            Attempts = snapshot is null
                ? [new DiagnosticsRow("最近认证尝试", "不可确认", UnconfirmedNote, OverviewTone.Neutral)]
                : Attempts(snapshot)
        };
        return content with { ContentKey = Fingerprint(content) };
    }

    /// <summary>
    /// 五类诊断分列（故事 18）。类别名与既有 WinForms 托盘逐字一致；UU 拆为「本机探测」与
    /// 「远程会话」两行（故事 20）：前者是进程/连接的局部探测，后者显式标注未实测，
    /// 不把局部探测当作远程可用证明。
    /// </summary>
    private static IReadOnlyList<DiagnosticsRow> Categories(StatusSnapshot s) =>
    [
        new("校园 Wi-Fi", s.Wifi, "证据来自后台服务对选定 Wi-Fi 网卡的链路观测。", ToneWifi(s.Wifi)),
        new("校园网认证", s.Portal, "证据来自后台服务的只读门户状态查询；不提交凭据。", TonePortal(s.Portal)),
        new("校园网直连", s.Direct, "直连探测是 HTTPS 内容校验；通过不等于物理出口已核验。", ToneDirect(s.Direct)),
        new("代理 / 系统路径", s.Tunnel,
            "系统选路结论是路由样本推断，不是物理出口验证；物理出口需脱敏抓包核验。", ToneTunnel(s.Tunnel)),
        new("UU 远程 · 本机探测", s.Uu, "进程与 TCP 连接计数是本机观测；不能当作远程会话可用的证明。", ToneUuLocal(s.Uu)),
        new("UU 远程 · 远程会话", "未实测", "AutoNet 不发起远程会话实测；会话可用性以实际使用为准。", OverviewTone.Neutral)
    ];

    /// <summary>认证信号（故事 21）：两类信号独立成行、各自带口径说明；都没有时显式说明「无」。</summary>
    private static IReadOnlyList<DiagnosticsRow> Signals(StatusSnapshot s)
    {
        var rows = new List<DiagnosticsRow>();
        if (s.CredentialsBlocked)
            rows.Add(new DiagnosticsRow("认证提交暂停", "存在",
                "自动认证已安全暂停，需要人工核实后解除；解除须经确认、只读复核、修订核对与审计。", OverviewTone.Problem));
        if (s.PortalLayoutSuspected)
            rows.Add(new DiagnosticsRow("门户结构疑似变化", "存在",
                "这是门户改版信号，不代表认证已暂停，也不代表凭据被拒绝。", OverviewTone.Warning));
        if (rows.Count == 0)
            rows.Add(new DiagnosticsRow("认证信号", "无", "当前没有认证暂停，也未观测到门户结构变化信号。", OverviewTone.Neutral));
        return rows;
    }

    /// <summary>
    /// 受控阶段与失败分类（故事 22）：复用 <see cref="PortalAttempt.SafeSummary"/> 的受控词表，
    /// 只显示阶段、失败分类、HTTP 状态与是否发起凭据请求；绝不显示 LastError、URL 或原始响应。
    /// 快照枚举无法解析或值未知时降级为受控说明，不回显任何原始值。
    /// 构造前提：SafeSummary 不读取 <see cref="PortalAttempt.Result"/>（Failure == None 的文案区分
    /// 只依赖 CredentialRequestStarted，已随快照传入），因此以任意 LoginResult 构造不会伪造结论。
    /// </summary>
    private static IReadOnlyList<DiagnosticsRow> Attempts(StatusSnapshot s)
    {
        var stage = Parse<PortalStage>(s.AuthenticationStage);
        var failure = Parse<PortalFailure>(s.AuthenticationFailure);
        var attempt = (stage, failure) switch
        {
            (null, _) or (_, null) => "认证阶段或失败分类无法识别；按受控口径降级显示。",
            (PortalStage.Unknown, PortalFailure.None) => "尚无认证尝试记录。",
            _ => new PortalAttempt(LoginResult.Unsupported, stage.Value, failure.Value, s.AuthenticationHttpStatus,
                    CredentialRequestStarted: s.CredentialRequestStarted).SafeSummary
        };
        var persistent = Parse<AuthenticationState>(s.AuthenticationPersistentState);
        return
        [
            new("最近认证尝试", attempt, "分类来自受控词表；不含原始响应、URL 或异常文本。", OverviewTone.Neutral),
            new("认证持久状态", persistent is { } state ? state.ToString() : "未知",
                "持久状态决定认证提交是否被安全暂停。", OverviewTone.Neutral)
        ];
    }

    /// <summary>
    /// 不可信状态的降级行：类别名与可信状态逐字一致（「分列」在不可信时仍成立），
    /// 值固定为「不可确认」，绝不沿用上一次画面。
    /// </summary>
    private static IReadOnlyList<DiagnosticsRow> UnconfirmedCategories()
    {
        string[] captions = ["校园 Wi-Fi", "校园网认证", "校园网直连", "代理 / 系统路径", "UU 远程 · 本机探测", "UU 远程 · 远程会话"];
        return captions.Select(caption => new DiagnosticsRow(caption, "不可确认", UnconfirmedNote, OverviewTone.Neutral)).ToList();
    }

    /// <summary>严格枚举解析：仅接受已定义且逐字一致的值；其余一律视为未知（不回显原始值）。</summary>
    private static T? Parse<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == value ? parsed : null;

    private static OverviewTone ToneWifi(string value) =>
        value.Contains("已连接", StringComparison.Ordinal) ? OverviewTone.Good : OverviewTone.Neutral;

    private static OverviewTone TonePortal(string value) => value switch
    {
        _ when value.Contains("未查询（外网检查已通过）", StringComparison.Ordinal) || value == "已认证" => OverviewTone.Good,
        _ when value.Contains("需认证", StringComparison.Ordinal) => OverviewTone.Problem,
        _ when value.Contains("未知", StringComparison.Ordinal) => OverviewTone.Attention,
        _ => OverviewTone.Neutral
    };

    private static OverviewTone ToneDirect(string value) => value switch
    {
        "外网可达" => OverviewTone.Good,
        "外网检查未通过" => OverviewTone.Problem,
        _ => OverviewTone.Attention
    };

    // 与概览的「代理 / 系统路径异常」判定同口径（NetworkAdditional.ProxyFault：包含「不可达」）。
    private static OverviewTone ToneTunnel(string value) =>
        value.Contains("不可达", StringComparison.Ordinal) ? OverviewTone.Warning : OverviewTone.Neutral;

    private static OverviewTone ToneUuLocal(string value) => value switch
    {
        _ when value.Contains("服务运行", StringComparison.Ordinal) => OverviewTone.Good,
        _ when value.Contains("未知", StringComparison.Ordinal) => OverviewTone.Attention,
        _ => OverviewTone.Neutral
    };

    private static string Fingerprint(DiagnosticsContent content)
    {
        static string Rows(IReadOnlyList<DiagnosticsRow> rows) =>
            string.Join("\u001e", rows.Select(row => row.Caption + ":" + row.Value + ":" + row.Note + ":" + row.Tone));
        return string.Join("\u001f", content.Trusted, Rows(content.Categories), Rows(content.Signals), Rows(content.Attempts));
    }
}
