namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 认证页一次“打开 → 附着”回合的协调器侧状态（工单 #37 / #39）。
///
/// 宿主快照始终是唯一事实来源；本类只记录协调器自己的尝试策略，避免这些布尔量
/// 散落在协调器的各个分支里：
/// - 是否已发起过附着：失败后只允许用户显式重试，状态轮询不得自动重试；
/// - 是否成功进入过附着：只有它允许后台通道会话结束后恢复一次只读附着；
/// - 附着抛出异常时保留的受控失败投影：直到宿主快照变化或用户显式重试。
///
/// 纯状态、无 I/O，可独立回归；线程契约与协调器一致（只在界面线程改写）。
/// </summary>
internal sealed class AuthPageAttachEpisode
{
    private bool attempted;
    private bool live;
    private AuthPageHostSnapshot? failureOverride;
    private AuthPageHostSnapshot? failureObserved;

    /// <summary>已发起过附着尝试（含进行中与已失败）。</summary>
    public bool Attempted => attempted;

    /// <summary>附着尝试尚未返回；期间不允许再发起新的附着（包括会话结束恢复）。</summary>
    public bool InFlight { get; private set; }

    /// <summary>成功进入过 Attaching/Attached：会话结束后允许恢复一次只读附着。</summary>
    public bool Live => live;

    /// <summary>是否可以发起新的附着：已附着、已尝试过或仍有尝试在途时返回 false。</summary>
    public bool CanBegin(AuthPageHostSnapshot snapshot) => !InFlight && !snapshot.Attached && !attempted;

    /// <summary>登记一次附着尝试；调用方应先用 <see cref="CanBegin"/> 判定。</summary>
    public void Begin()
    {
        attempted = true;
        InFlight = true;
    }

    /// <summary>附着调用已返回（无论成功、失败还是取消）：清除进行中标记。</summary>
    public void Complete() => InFlight = false;

    /// <summary>取消或会话结束后的恢复：允许下一次打开页面重新附着，不清理失败投影。</summary>
    public void Reopen() => attempted = false;

    /// <summary>用户显式重试：清除尝试记录与受控失败投影，下一次可重新附着。</summary>
    public void ResetForRetry()
    {
        attempted = false;
        failureOverride = null;
        failureObserved = null;
    }

    /// <summary>
    /// 记录宿主快照里的附着事实：Attaching/Attached 视为“进入过”，受控失败清除该事实；
    /// 未附着与通道会话进行中都不改变它（会话结束后仍可恢复）。
    /// </summary>
    public void Observe(AuthPageHostSnapshot snapshot)
    {
        if (snapshot.Attached || snapshot.Status == AuthPageHostStatus.Attaching) live = true;
        else if (snapshot.Status is AuthPageHostStatus.RuntimeMissing
            or AuthPageHostStatus.NetworkBoundaryUnavailable or AuthPageHostStatus.Failed)
            live = false;
    }

    /// <summary>
    /// 附着抛出异常：宿主可能没有发布自己的失败快照，因此保留一个受控失败投影。
    /// 返回该投影，供调用方立即发布。
    /// </summary>
    public AuthPageHostSnapshot RecordFailure(AuthPageHostSnapshot observed)
    {
        failureObserved = observed;
        live = false;
        return failureOverride = new(AuthPageHostStatus.Failed, AuthPageModel.PortalAddress, false,
            AuthPageModel.HostCopyFor(AuthPageHostStatus.Failed).Detail);
    }

    /// <summary>
    /// 宿主快照进入投影：受控失败投影只在宿主快照没有变化时覆盖它；
    /// 宿主一旦发布新状态（例如稍后变为边界不可用），旧失败投影立即失效。
    /// </summary>
    public AuthPageHostSnapshot Project(AuthPageHostSnapshot snapshot)
    {
        if (failureOverride is null) return snapshot;
        if (snapshot == failureObserved) return failureOverride;
        failureOverride = null;
        failureObserved = null;
        return snapshot;
    }
}
