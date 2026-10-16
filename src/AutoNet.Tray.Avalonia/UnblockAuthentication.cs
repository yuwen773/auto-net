using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>解除认证暂停的受控结果。成功与用户取消保持既有“静默”语义，其余都必须给出受控说明。</summary>
internal enum UnblockOutcome
{
    /// <summary>已按确认、只读复核、修订核对与持久审计的完整序列解除。</summary>
    Released,
    /// <summary>当前没有需要人工解除的认证暂停标记（不得凭空解除）。</summary>
    NoBlock,
    /// <summary>用户在确认对话框上选择不解除：不复核、不核对、不写任何文件。</summary>
    Declined,
    /// <summary>缺少校园网设置或校园 Wi-Fi 未就绪，前置条件不具备。</summary>
    NotReady,
    /// <summary>只读复核未通过：保留认证暂停并给出受控说明（故事 42）。</summary>
    RecheckFailed,
    /// <summary>诊断期间设置或校园 Wi-Fi 发生变化：中止并保留认证暂停。</summary>
    RevisionChanged
}

/// <summary>解除流程的必经阶段。顺序即安全顺序：确认 → 只读复核 → 修订核对 → 审计后解除。</summary>
internal enum UnblockStage
{
    Confirm,
    Recheck,
    RevisionCheck,
    Audit
}

/// <summary>一次解除尝试的完整可观察结果；<see cref="Stages"/> 记录实际走过的阶段序列。</summary>
internal sealed record UnblockResult(UnblockOutcome Outcome, string Message, IReadOnlyList<UnblockStage> Stages)
{
    public bool Released => Outcome == UnblockOutcome.Released;

    /// <summary>成功与用户取消静默（结果经状态管道回到界面），其余显示受控说明。</summary>
    public bool RequiresNotice => Outcome is not (UnblockOutcome.Released or UnblockOutcome.Declined);
}

/// <summary>
/// 管理员解除认证暂停（迁移工单 #5 / T08 / 故事 42、43）。
/// 与既有 WinForms 托盘同语义并逐段完整搬运：仅在存在需要人工解除的认证暂停标记时继续；
/// 用户确认 → 只读复核（认证诊断前置核对）→ 修订核对（设置与校园 Wi-Fi 在诊断期间不得变化）
/// → 持久审计后解除。全程只调用门户的只读诊断路径，不加载、不提交凭据；
/// 取消确认、复核未通过或修订变化时一律保留暂停且不写任何文件。
/// 所有依赖以委托注入，便于受控测试；本类不依赖任何 UI 类型。
/// </summary>
internal sealed class UnblockAuthentication(
    Func<AuthenticationBlock?> loadBlock,
    Func<Settings?> loadSettings,
    Func<Settings, WifiLink> readLink,
    Func<Settings, WifiLink, CancellationToken, Task<PortalAttempt>> recheck,
    Action<AuthenticationBlock> release,
    Func<CancellationToken, Task<bool>> confirm)
{
    /// <summary>确认文案与既有 WinForms 托盘逐字一致：先核实、再说清后果、默认不确认。</summary>
    public const string ConfirmHeading = "解除认证暂停";

    public const string ConfirmBody =
        "请先核实诊断、网关及设备绑定配置。解除后，后台将允许再次提交当前账号密码；若再次被明确拒绝，会重新暂停。\n\n确认已核实并解除认证暂停？";

    private const string NoBlockMessage = "当前没有需要人工解除的认证暂停标记。";
    private const string RevisionChangedMessage = "诊断期间设置或校园 Wi-Fi 已变化，请重新诊断；认证暂停保持不变。";

    /// <summary>生产依赖：受限目录的安全状态、已保存设置、真实校园 Wi-Fi 与门户只读诊断。</summary>
    public static UnblockAuthentication Production(Func<CancellationToken, Task<bool>> confirm) => new(
        () => new SafetyStateStore(Storage.Private).LoadAuthenticationBlock(),
        Storage.LoadSettings,
        Wifi.Read,
        async (settings, link, cancellationToken) =>
        {
            using var http = DirectNetwork.CreateClient(link);
            return await new DrcomPortal(http, new Uri(settings.PortalOrigin)).DiagnoseAsync(cancellationToken);
        },
        block => new SafetyStateStore(Storage.Private).ClearAuthenticationBlock(block),
        confirm);

    /// <summary>受控结果的中文标题；界面提示与 QA 证据共用同一份术语。</summary>
    public static string HeadingFor(UnblockOutcome outcome) => outcome switch
    {
        UnblockOutcome.Released => "已解除认证暂停",
        UnblockOutcome.NoBlock => "无需解除认证暂停",
        UnblockOutcome.Declined => "已取消解除认证暂停",
        UnblockOutcome.NotReady => "前置条件不具备",
        UnblockOutcome.RecheckFailed => "前置复核未通过，保留认证暂停",
        _ => "解除已中止，认证暂停保持不变"
    };

    public async Task<UnblockResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var stages = new List<UnblockStage>();

        // 入口前提：没有需要人工解除的标记时不得凭空解除（AC：入口仅在存在待解除阻断时可用）。
        var block = loadBlock();
        if (block?.BlocksSubmissions != true)
            return new UnblockResult(UnblockOutcome.NoBlock, NoBlockMessage, stages);

        // 1) 用户确认。取消即止：不复核、不核对、不写任何文件。
        stages.Add(UnblockStage.Confirm);
        if (!await confirm(cancellationToken))
            return new UnblockResult(UnblockOutcome.Declined, "已取消解除认证暂停；认证暂停保持不变。", stages);

        // 2) 只读复核：按已保存的校园网配置读取 Wi-Fi 并走门户只读诊断（不加载、不提交凭据）。
        stages.Add(UnblockStage.Recheck);
        var settings = loadSettings();
        if (settings is null)
            return new UnblockResult(UnblockOutcome.NotReady, "请先保存校园网设置。", stages);
        try
        {
            settings.Validate();
        }
        catch (ArgumentException e)
        {
            return new UnblockResult(UnblockOutcome.NotReady, e.Message, stages);
        }

        var link = readLink(settings);
        if (link.State != WifiState.Ready)
            return new UnblockResult(UnblockOutcome.NotReady, "请先连接指定校园 Wi-Fi 并取得 IPv4。", stages);

        var diagnosis = await recheck(settings, link, cancellationToken);
        if (diagnosis.Failure != PortalFailure.None)
            return new UnblockResult(UnblockOutcome.RecheckFailed, "前置复核未通过，保留认证暂停：" + diagnosis.SafeSummary, stages);

        // 3) 修订核对：诊断期间设置或校园 Wi-Fi 变化即中止，绝不按过期事实解除。
        stages.Add(UnblockStage.RevisionCheck);
        var finalLink = readLink(settings);
        if (!Equals(loadSettings(), settings) || finalLink.State != WifiState.Ready ||
            finalLink.Index != link.Index || !Equals(finalLink.Address, link.Address))
            return new UnblockResult(UnblockOutcome.RevisionChanged, RevisionChangedMessage, stages);

        // 4) 持久审计后解除：审计行先落盘；审计写入失败或修订不一致都会抛异常，阻断保持不变。
        stages.Add(UnblockStage.Audit);
        release(block);
        return new UnblockResult(UnblockOutcome.Released, "已解除认证暂停；后台将按既有策略继续检测。", stages);
    }
}
