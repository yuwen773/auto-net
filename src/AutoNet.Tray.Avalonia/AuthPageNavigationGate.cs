namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 首次导航的判定（工单 #38）：只有第一次主框架导航结果决定页面状态。
/// 成功 → 发布 Attached；失败 → 可重试的 Failed；一旦确认成功，后续导航（门户自身的
/// 跳转、被来源校验取消的跳转）不再把页面改回加载失败，也不重复发布 Attached。
/// 这是纯规则，便于离线覆盖“迟到事件/重复事件”的交错。
/// </summary>
internal static class AuthPageNavigationGate
{
    internal enum Decision
    {
        /// <summary>首次导航成功，可以发布 Attached。</summary>
        Loaded,
        /// <summary>已确认加载，或本次结果无法对应预期导航：不改写状态。</summary>
        Ignore,
        /// <summary>首次导航失败：降级为可重试的 Failed。</summary>
        Failed
    }

    internal static Decision Decide(bool confirmed, bool succeeded) =>
        confirmed ? Decision.Ignore : succeeded ? Decision.Loaded : Decision.Failed;
}
