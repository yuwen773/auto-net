using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>语义色分类：文字、图标与标签底色成对出现，颜色从不是唯一的信息载体。</summary>
internal enum OverviewTone
{
    Neutral,
    Good,
    Attention,
    Warning,
    Problem
}

/// <summary>附加事实标签：可并存于任何主状态，独立呈现，不与主状态或彼此覆盖。</summary>
internal sealed record OverviewChip(string Text, OverviewTone Tone);

/// <summary>进度行：标题、当前值与可选补充说明。</summary>
internal sealed record OverviewRow(string Caption, string Value, string Note = "");

/// <summary>认证列的三种事实（外加不显示）：认证暂停、门户结构信号、以及不可确认。</summary>
internal enum OverviewAuthKind
{
    None,
    Suspended,
    LayoutSuspected,
    Unconfirmable
}

/// <summary>认证列：与联网事实分列呈现的认证管线信息；解除入口紧邻暂停事实。</summary>
internal sealed record OverviewAuthColumn(OverviewAuthKind Kind, string Title, string Note, OverviewTone Tone)
{
    public bool Visible => Kind != OverviewAuthKind.None;

    public static readonly OverviewAuthColumn None = new(OverviewAuthKind.None, "", "", OverviewTone.Neutral);

    /// <summary>不可确认时的认证列：固定降级文案，绝不沿用上次的认证事实。</summary>
    public static readonly OverviewAuthColumn Unconfirmable = new(
        OverviewAuthKind.Unconfirmable, "认证状态不可确认", "当前状态不可确认，认证相关操作暂不可用。", OverviewTone.Neutral);
}

/// <summary>恢复开关的明确意图：只发送开启或关闭，不使用 toggle 语义（故事 40）。</summary>
internal enum RecoveryIntent
{
    Enable,
    Disable
}

/// <summary>恢复开关的显示状态：开关文案描述点击后要执行的动作；不可信状态下禁用并改为“待确认”。</summary>
internal sealed record RecoverySwitchState(string State, string Label, bool Enabled, RecoveryIntent? Intent);

/// <summary>解除认证暂停入口的显示状态；可见时紧邻认证事实，可用性由可信度与暂停事实共同决定。</summary>
internal sealed record UnblockEntryState(bool Visible, bool Enabled, string Label);

/// <summary>
/// 浏览器兜底开页入口的显示状态（#11 / 故事 61–69）：可见时独立成卡，说明逐字受控——
/// 默认声明「只开根页、无凭据」边界；自动开页失败或消费记录保存失败时改为对应受控提示，
/// 手动入口在两种失败下都保持可用（故事 69）。不可信状态一律隐藏（不依据陈旧信息提供入口）。
/// </summary>
internal sealed record PortalOpenEntryState(bool Visible, string Label, string Note, OverviewTone Tone)
{
    public bool Enabled => Visible;

    public static readonly PortalOpenEntryState Hidden = new(false, "打开门户页面…", "", OverviewTone.Neutral);
}

/// <summary>
/// 概览页内容投影（迁移工单 #4）：把一次状态读取映射为“当前网络状态 / 应用正在做什么 / 下一步操作”，
/// 不依赖 UI 类型，可直接受控测试。缺失、过期、损坏或不可信状态一律降级为“不可确认”并禁用
/// 依赖可信状态的动作，绝不沿用上一次画面。完整诊断项在网络诊断页，本页只保留受控技术详情。
/// </summary>
internal sealed record OverviewContent : PageContent
{
    internal OverviewContent() : base(ShellPage.Overview, ShellPages.Info(ShellPage.Overview).Name) { }

    /// <summary>状态边界（完整性、时间有效性）是否允许依据其内容显示事实或执行动作。</summary>
    public bool Trusted { get; init; }

    public NetworkPrimaryState Primary { get; init; }
    public OverviewTone Tone { get; init; }
    public string StatusTitle { get; init; } = "";
    public string StatusExplanation { get; init; } = "";
    public string NextStep { get; init; } = "";
    public string TechnicalDetail { get; init; } = "";
    public bool HasTechnical { get; init; }

    /// <summary>认证列（在线与认证暂停并存分列）：不可确认时也显示，但只呈现固定降级文案。</summary>
    public OverviewAuthColumn Auth { get; init; } = OverviewAuthColumn.None;

    public IReadOnlyList<OverviewChip> Chips { get; init; } = [];
    public IReadOnlyList<OverviewRow> Progress { get; init; } = [];
    public RecoverySwitchState Recovery { get; init; } = OverviewModel.UnconfirmedRecovery;
    public UnblockEntryState Unblock { get; init; } = OverviewModel.HiddenUnblock;

    /// <summary>浏览器兜底开页入口（#11）：仅在可信且存在有效授予时可见。</summary>
    public PortalOpenEntryState PortalOpen { get; init; } = PortalOpenEntryState.Hidden;

    /// <summary>渲染指纹：全部可见字段的稳定拼接；不变时视图跳过重建（不打断展开状态与焦点）。</summary>
    public string ContentKey { get; init; } = "";
}

/// <summary>
/// 概览投影规则（#4 / T02）：在线与认证暂停各自独立（故事 14）；不可信状态不可确认并禁用动作
/// （故事 15）；进度与下一步使用受控中文领域术语（故事 24、25）。规则只依赖 Core 的
/// <see cref="NetworkDisplayMap"/> 输出与快照事实，不读取文件、不产生副作用。
/// </summary>
internal static class OverviewModel
{
    /// <summary>不可信状态的恢复开关：禁用且不提供意图。</summary>
    public static readonly RecoverySwitchState UnconfirmedRecovery = new("待确认", "自动恢复状态待确认", false, null);

    /// <summary>不可确认时的解除入口：可见但禁用——不能证明存在待解除阻断，也不能依据陈旧信息操作。</summary>
    public static readonly UnblockEntryState UnconfirmedUnblock = new(true, false, "解除认证暂停…");

    /// <summary>不可用的解除入口（可信且没有暂停事实时，按 #5“仅在存在待解除阻断时提供入口”不显示）。</summary>
    public static readonly UnblockEntryState HiddenUnblock = new(false, false, "解除认证暂停…");

    /// <summary>解除入口的默认占位标签：托盘菜单与概览共用同一术语，状态未知时不谎报存在待解除阻断。</summary>
    public const string PlaceholderLabel = "解除认证暂停…";

    private const string RecoveryCaveat =
        "观测耗时从首次发现故障算起，只说明 AutoNet 观察到外网重新可达，无法区分是程序自动恢复、人工处理还是网络自行恢复；真实端到端耗时需现场测量。";

    /// <summary>主状态的语义色分类；映射与既有 WinForms 托盘一致，未知状态保持中性。</summary>
    public static OverviewTone ToneFor(NetworkPrimaryState state) => state switch
    {
        NetworkPrimaryState.Online => OverviewTone.Good,
        NetworkPrimaryState.AuthenticationUnknown => OverviewTone.Attention,
        NetworkPrimaryState.NeedsAuthentication or NetworkPrimaryState.BindingRejected => OverviewTone.Problem,
        NetworkPrimaryState.AuthenticatedOffline or NetworkPrimaryState.DisconnectQuotaExhausted => OverviewTone.Warning,
        _ => OverviewTone.Neutral
    };

    /// <summary>恢复意图到管理员操作契约的单一映射（与既有启动参数逐字一致）。</summary>
    public static ElevatedOperationKind OperationFor(RecoveryIntent intent) =>
        intent == RecoveryIntent.Enable ? ElevatedOperationKind.EnableRecovery : ElevatedOperationKind.DisableRecovery;

    /// <summary>
    /// 恢复开关状态：可信时按快照的 Enabled 事实给出明确的开启或关闭意图；
    /// 不可信（缺失/过期/损坏）时禁用并显示“待确认”，不沿用上次结论。
    /// </summary>
    public static RecoverySwitchState RecoverySwitch(StatusReading? reading)
    {
        if (reading is not { Trusted: true }) return UnconfirmedRecovery;
        return reading.Snapshot!.Enabled
            ? new RecoverySwitchState("已开启", "关闭自动恢复", true, RecoveryIntent.Disable)
            : new RecoverySwitchState("已关闭", "开启自动恢复", true, RecoveryIntent.Enable);
    }

    /// <summary>
    /// 解除入口状态（#5 / T08）：托盘菜单与概览解除按钮共用同一投影。
    /// 不可信时可见但禁用（不能证明存在待解除阻断，也不能依据陈旧信息操作），
    /// 可信且确有认证暂停时才可用；标签随失败事实切换，与既有 WinForms 托盘逐字一致。
    /// </summary>
    public static UnblockEntryState UnblockEntry(StatusReading? reading)
    {
        if (reading is not { Trusted: true }) return UnconfirmedUnblock;
        return Unblock(true, reading.Snapshot);
    }

    /// <summary>把一次状态读取投影为概览页内容；<paramref name="reading"/> 为 null 表示尚未取得状态。
    /// <paramref name="portalFailure"/> 与 <paramref name="portalSaveFailure"/> 来自开页控制器的会话内
    /// 状态（#11）：仅影响兜底入口的说明文案，不影响入口可用性。</summary>
    public static OverviewContent From(StatusReading? reading, PortalOpenFailure? portalFailure = null, bool portalSaveFailure = false)
    {
        var display = reading?.Display ?? NetworkDisplayMap.Map(null, null);
        var trusted = reading?.Trusted == true;
        var snapshot = trusted ? reading!.Snapshot : null;

        IReadOnlyList<OverviewRow> progress = snapshot is null
            ? [new OverviewRow("进度", display.Title + "，暂不显示进度信息。")]
            : ProgressRows(snapshot);
        IReadOnlyList<OverviewChip> chips = snapshot is null ? [] : Chips(display.Additional, display.Primary);

        var content = new OverviewContent
        {
            Trusted = trusted,
            Primary = display.Primary,
            Tone = ToneFor(display.Primary),
            StatusTitle = display.Title,
            StatusExplanation = display.Explanation,
            NextStep = display.NextStep,
            TechnicalDetail = display.TechnicalDetail,
            HasTechnical = display.TechnicalDetail.Length > 0,
            Auth = snapshot is null ? OverviewAuthColumn.Unconfirmable : AuthColumn(snapshot),
            Progress = progress,
            Chips = chips,
            Recovery = RecoverySwitch(reading),
            Unblock = Unblock(trusted, snapshot),
            PortalOpen = PortalEntry(trusted, snapshot, portalFailure, portalSaveFailure)
        };
        return content with { ContentKey = Fingerprint(content) };
    }

    /// <summary>
    /// 兜底开页入口（#11 / 故事 61–69）：只在可信且快照同时携带授予标志与授予时间戳时可见；
    /// 上次自动开页失败或消费记录保存失败时说明切换为对应受控提示（绝不显示异常文本或状态原文）。
    /// </summary>
    public static PortalOpenEntryState PortalEntry(bool trusted, StatusSnapshot? snapshot, PortalOpenFailure? failure, bool saveFailure)
    {
        if (!trusted || snapshot is not { BrowserFallbackRequested: true, BrowserFallbackNotBefore: not null })
            return PortalOpenEntryState.Hidden;
        const string label = "打开门户页面…";
        if (failure is { } openFailure)
            return new(true, label, "上次自动开页未成功（" + openFailure.ErrorKind + "）；可点击下方按钮再次手动打开。", OverviewTone.Warning);
        if (saveFailure)
            return new(true, label, "开页记录暂时无法保存，已暂停自动开页；可手动打开门户页面。", OverviewTone.Attention);
        return new(true, label, "需要使用浏览器完成校园网认证；只打开固定允许的门户根地址，不填表、不携带凭据。", OverviewTone.Attention);
    }

    private static OverviewAuthColumn AuthColumn(StatusSnapshot snapshot)
    {
        if (snapshot.CredentialsBlocked)
        {
            var note = snapshot.PortalLayoutSuspected
                ? "自动认证已安全暂停，需要人工核实后解除；门户结构疑似变化。"
                : "自动认证已安全暂停，需要人工核实后解除。";
            return new OverviewAuthColumn(OverviewAuthKind.Suspended, "认证提交已暂停", note, OverviewTone.Problem);
        }

        if (snapshot.PortalLayoutSuspected)
            return new OverviewAuthColumn(OverviewAuthKind.LayoutSuspected, "门户结构疑似变化",
                "门户结构疑似变化不代表认证已暂停；请查看网络诊断了解详情。", OverviewTone.Warning);

        return OverviewAuthColumn.None;
    }

    private static UnblockEntryState Unblock(bool trusted, StatusSnapshot? snapshot)
    {
        // 不可信：显示但禁用（故事 15 要求禁用解除入口，且无法证明是否存在待解除阻断）。
        if (!trusted) return UnconfirmedUnblock;
        if (snapshot is not { CredentialsBlocked: true }) return HiddenUnblock;
        var label = snapshot.AuthenticationFailure == nameof(PortalFailure.BindingRejected)
            ? "查看认证诊断并解除…"
            : "解除认证暂停…";
        return new UnblockEntryState(true, true, label);
    }

    /// <summary>“应用正在做什么”：断线额度、下次重试与最近恢复，数值全部来自快照投影，界面不重复策略常量。</summary>
    private static IReadOnlyList<OverviewRow> ProgressRows(StatusSnapshot snapshot)
    {
        var max = Math.Max(1, snapshot.DisconnectBudgetMax);
        var quota = $"本次故障主动断线额度：{snapshot.DisconnectAttempts}/{max} 次" +
            (snapshot.DisconnectRequestUnconfirmed ? "（上次断线的完成状态未确认，已禁止继续断线）" : "");
        var next = snapshot.NextAttempt is { } at
            ? $"{at.ToLocalTime():HH:mm:ss}（等待期间继续检测）"
            : snapshot.Direct == "外网可达" ? "当前无需重试" : "正在观察，尚未排定";
        var last = snapshot.LastRecoveryAt is { } recovered
            ? $"{recovered.ToLocalTime():MM-dd HH:mm:ss} · 观测耗时 {(snapshot.LastRecoverySeconds is { } seconds ? $"{seconds:0.##} 秒" : "耗时未知")}"
            : "尚无恢复记录。";
        return
        [
            new OverviewRow("主动断线额度", quota),
            new OverviewRow("下次允许重试", next,
                $"额度在连续在线 {snapshot.DisconnectResetAfterSeconds} 秒后重置；每次断线请求至少间隔 {snapshot.DisconnectCooldownSeconds} 秒。"),
            new OverviewRow("最近一次恢复", last, snapshot.LastRecoveryAt is null ? "" : RecoveryCaveat)
        ];
    }

    // 附加事实标签：恢复开关关闭由进度行与开关呈现，不重复为标签；额度用尽已是主状态时不重复。
    private static IReadOnlyList<OverviewChip> Chips(NetworkAdditional additional, NetworkPrimaryState primary)
    {
        var chips = new List<OverviewChip>();
        if (additional.HasFlag(NetworkAdditional.DisconnectQuotaExhausted) && primary != NetworkPrimaryState.DisconnectQuotaExhausted)
            chips.Add(new OverviewChip("主动断线额度已用尽", OverviewTone.Warning));
        if (additional.HasFlag(NetworkAdditional.DisconnectUnconfirmed))
            chips.Add(new OverviewChip("断线完成状态未确认", OverviewTone.Problem));
        if (additional.HasFlag(NetworkAdditional.ProxyFault))
            chips.Add(new OverviewChip("代理 / 系统路径异常", OverviewTone.Warning));
        if (additional.HasFlag(NetworkAdditional.EgressUnverified))
            chips.Add(new OverviewChip("物理出口未核验", OverviewTone.Neutral));
        return chips;
    }

    private static string Fingerprint(OverviewContent content) => string.Join("\u001f", new object?[]
    {
        content.Trusted,
        content.StatusTitle, content.StatusExplanation, content.NextStep, content.TechnicalDetail,
        content.Auth,
        content.Primary, content.Tone,
        string.Join("\u001e", content.Chips.Select(chip => chip.Text + ":" + chip.Tone)),
        string.Join("\u001e", content.Progress.Select(row => row.Caption + ":" + row.Value + ":" + row.Note)),
        content.Recovery, content.Unblock, content.PortalOpen
    });
}
