using System.Globalization;
using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>一次开页动作的结果；文案面由概览投影与调用方组合，本类型只承载事实。</summary>
internal enum PortalOpenOutcome
{
    /// <summary>已发起浏览器打开（自动或手动）。</summary>
    Opened,
    /// <summary>当前无可执行的授予（请求消失、状态不可信或授予缺失）。</summary>
    NotEligible,
    /// <summary>同一授予已消费（仅自动路径可能返回）。</summary>
    AlreadyConsumed,
    /// <summary>本地冷却未满足（仅自动路径可能返回）。</summary>
    CooldownActive,
    /// <summary>消费记录无法安全保存；按决策 11 拒绝自动开页。</summary>
    SaveFailed,
    /// <summary>浏览器启动失败；不自动重试，等待手动入口。</summary>
    LaunchFailed
}

/// <summary>一次自动开页失败的受控记录：只保留异常类型名与时刻，绝不携带异常文本或 URL 之外的内容。</summary>
internal sealed record PortalOpenFailure(DateTimeOffset At, string ErrorKind);

/// <summary>
/// 浏览器兜底开页控制器（迁移工单 #11 / T11、T12 / 故事 61–69，ADR-0008）。
///
/// 服务端语义（MonitorWorker）：授予 = 快照 <see cref="StatusSnapshot.BrowserFallbackRequested"/>
/// 与 <see cref="StatusSnapshot.BrowserFallbackNotBefore"/> 同时成立；NotBefore 是「每段授予一次」的
/// 稳定时间戳（服务重启换发新值），status.json 没有故障 ID，本类按授予键（NotBefore 的圆整形式）
/// 识别同一授予，不承诺识别跨服务重启的同一业务故障（决策 10）。
///
/// 自动开页规则（按序，全部构造性满足对应故事）：
/// 1. 不可信读取直接忽略（不依据陈旧信息开页，故事 68）。
/// 2. 同一授予键已在消费记录 → 永不再自动开页（故事 64；记录按用户持久化，跨托盘重启有效）。
/// 3. 本地冷却未满足 → 暂缓（本轮不开，后续读取再试；ADR-0008：仅新的有效授予且冷却已满足时
///    可再次自动打开）。冷却按「最近开页时刻（自动或手动）+ 10 分钟」计算且要求 now ≥ 时刻——
///    系统时钟回拨只会让冷却显得更长，时间异常不得成为提前重复开页的理由（决策 11）。
/// 4. 开页前重读可信当前状态（故事 68）：重读不可信、请求消失或授予时间戳已变化 → 不开；
///    联网恢复由服务端撤旗表现，重读时一并覆盖。
/// 5. 消费记录必须先成功落盘（决策 11：必要消费记录无法安全保存时不自动开页），然后才启动浏览器；
///    因此启动失败不会造成跨重启的重复自动开页（故事 69），手动入口仍然可用。
/// 6. 冷却到期本身不触发任何开页（故事 67）：本类没有定时器，只被状态读取驱动。
/// 手动开页（概览页按钮 / 故事 66）：重验可信与授予存在后直接开页，不计入去重与冷却判断，
/// 但把当前授予写入消费记录并刷新最近开页时刻——刚手动打开后不会马上被自动再开一次。
///
/// 只打开 <see cref="RecoveryPolicy.FallbackPortalPageUrl"/> 且开页前用
/// <see cref="Settings.IsSupportedPortal"/> 复核（故事 62）；URL 以参数传给启动委托，
/// 不填表、不携带任何凭据（故事 63）。启动失败只记录异常类型（故事 69 的受控错误），
/// 绝不在轮询中重试（重入守卫 + 消费先落盘共同保证）。
///
/// 线程模型：<see cref="EvaluateAsync"/> 只经状态管道更新触发，重入自抑制；委托全部注入，
/// 本类不依赖 UI 类型，可直接受控测试。
/// </summary>
internal sealed class PortalPageOpener
{
    private readonly PortalOpenStore store;
    private readonly Func<CancellationToken, Task<StatusReading>> reread;
    private readonly Func<string, string?> launch;
    private readonly Func<DateTimeOffset> clock;

    private readonly Dictionary<string, DateTimeOffset> consumedGrants;
    private DateTimeOffset? lastOpenAt;
    private int evaluating;
    private PortalOpenFailure? lastFailure;

    public PortalPageOpener(
        PortalOpenStore store,
        Func<CancellationToken, Task<StatusReading>> reread,
        Func<string, string?> launch,
        Func<DateTimeOffset>? clock = null)
    {
        this.store = store;
        this.reread = reread;
        this.launch = launch;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        var loaded = store.Load();
        consumedGrants = new Dictionary<string, DateTimeOffset>(loaded.Grants, StringComparer.Ordinal);
        lastOpenAt = loaded.LastOpenAt;
    }

    /// <summary>本地冷却时长（与服务端授予戳共用同一契约常量）。</summary>
    public static TimeSpan Cooldown => RecoveryPolicy.BrowserFallbackOpenCooldown;

    /// <summary>固定允许的门户根地址（开页前仍会按受控校验复核）。</summary>
    public static string PortalPageUrl => RecoveryPolicy.FallbackPortalPageUrl;

    /// <summary>最近一次自动开页失败（会话内）；无失败为 null。供概览投影显示受控错误与手动入口。</summary>
    public PortalOpenFailure? LastFailure => lastFailure;

    /// <summary>消费记录的内存视图（内存 + 持久加载）；供 QA 与诊断读取。</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> ConsumedGrants => consumedGrants;

    /// <summary>是否因消费记录保存失败而拒绝过自动开页（概览投影的降级依据之一）。</summary>
    public bool SaveFailurePending { get; private set; }

    /// <summary>
    /// 观察一次状态读取并按需自动开页；返回本次评估的受控结果（QA 与诊断用途）。
    /// 评估自身失败关闭：任何异常都不外抛，按「不可执行」收场，等待下一轮读取。
    /// 重入自抑制：上一次评估尚未结束时本轮直接跳过（轮询节奏下不存在排队风暴）。
    /// </summary>
    public async Task<PortalOpenOutcome> EvaluateAsync(StatusReading? reading, CancellationToken cancellationToken = default)
    {
        if (reading is not { Trusted: true, Snapshot: { } snapshot }) return PortalOpenOutcome.NotEligible;
        if (snapshot.BrowserFallbackNotBefore is not { } notBefore) return PortalOpenOutcome.NotEligible; // 没有授予时间戳就不开页
        if (!snapshot.BrowserFallbackRequested) return PortalOpenOutcome.NotEligible;
        if (Interlocked.CompareExchange(ref evaluating, 1, 0) != 0) return PortalOpenOutcome.NotEligible;
        try
        {
            return await EvaluateCoreAsync(notBefore, manual: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PortalOpenOutcome.NotEligible;
        }
        catch (Exception)
        {
            // 评估路径上的一切意外都留在会话内；下一轮状态读取自然重试，浏览器绝不在轮询中反复启动。
            return PortalOpenOutcome.NotEligible;
        }
        finally
        {
            Interlocked.Exchange(ref evaluating, 0);
        }
    }

    /// <summary>手动开页入口（概览页按钮 / 故事 66、69）：返回受控结果，绝不抛出。</summary>
    public async Task<PortalOpenOutcome> OpenManuallyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 手动路径不继承轮询上下文里的旧授予：以重读结果为准（故事 41 的动作前重验同源）。
            var fresh = await ReadTrustedAsync(cancellationToken).ConfigureAwait(false);
            if (fresh?.Snapshot is not { } snapshot
                || !snapshot.BrowserFallbackRequested
                || snapshot.BrowserFallbackNotBefore is not { } notBefore)
                return PortalOpenOutcome.NotEligible;
            return await EvaluateCoreAsync(notBefore, manual: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException or UnauthorizedAccessException)
        {
            return PortalOpenOutcome.NotEligible;
        }
    }

    private async Task<PortalOpenOutcome> EvaluateCoreAsync(DateTimeOffset notBefore, bool manual, CancellationToken cancellationToken)
    {
        var grantKey = notBefore.ToString("O", CultureInfo.InvariantCulture);

        if (!manual)
        {
            // 故事 64：同一授予只自动打开一次（记录跨托盘重启持久）。
            if (consumedGrants.ContainsKey(grantKey)) return PortalOpenOutcome.AlreadyConsumed;
            // 故事 65、67 + 决策 11：本地冷却跨授予生效；时钟回拨使差值为负，同样视为未满足。
            var now = clock();
            if (lastOpenAt is { } last && now - last < Cooldown) return PortalOpenOutcome.CooldownActive;
        }

        // 故事 68：开页前重读可信当前状态；请求消失（含联网恢复后服务撤旗）或授予时间戳
        // 已被服务换发 → 按当前事实行动，不基于旧读取开页。
        var fresh = await ReadTrustedAsync(cancellationToken).ConfigureAwait(false);
        if (fresh?.Snapshot is not { } snapshot
            || !snapshot.BrowserFallbackRequested
            || snapshot.BrowserFallbackNotBefore is not { } current
            || current != notBefore)
            return PortalOpenOutcome.NotEligible;

        // 故事 62：只打开固定允许的门户根地址；校验不过 = 契约漂移，宁可不开。
        if (!Uri.TryCreate(PortalPageUrl, UriKind.Absolute, out var page) || !Settings.IsSupportedPortal(page))
            return PortalOpenOutcome.NotEligible;

        // 决策 11：消费记录先落盘且必须成功；失败时本轮不开页（下轮读取再试保存，浏览器零启动）。
        var openedAt = clock();
        if (!TryConsume(grantKey, openedAt))
        {
            if (!manual) SaveFailurePending = true;
            return PortalOpenOutcome.SaveFailed;
        }

        SaveFailurePending = false;
        var errorKind = launch(PortalPageUrl);
        if (errorKind is null)
        {
            lastFailure = null;
            return PortalOpenOutcome.Opened;
        }

        // 故事 69：启动失败记录受控错误（仅异常类型）；授予已消费，轮询不会再次启动浏览器，
        // 概览页的手动入口继续可用。
        lastFailure = new PortalOpenFailure(openedAt, errorKind);
        return PortalOpenOutcome.LaunchFailed;
    }

    private async Task<StatusReading?> ReadTrustedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reading = await reread(cancellationToken).ConfigureAwait(false);
            return reading.Trusted ? reading : null;
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException or UnauthorizedAccessException)
        {
            // 重读失败按“不可确认”处理：不依据陈旧信息开页（失败关闭）。
            return null;
        }
    }

    /// <summary>把授予写入内存与持久消费记录并刷新最近开页时刻；持久化失败返回 false。</summary>
    private bool TryConsume(string grantKey, DateTimeOffset openedAt)
    {
        consumedGrants[grantKey] = openedAt;
        lastOpenAt = openedAt;
        return store.TrySave(consumedGrants, lastOpenAt);
    }
}
