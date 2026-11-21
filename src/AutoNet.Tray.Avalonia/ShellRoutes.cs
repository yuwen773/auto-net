namespace AutoNet.Tray.Avalonia;

/// <summary>页面内容的获取方式：同步状态投影，或经代次守卫异步加载。</summary>
internal enum ShellPageContentMode
{
    SyncProjection,
    AsyncLoad
}

/// <summary>
/// 一页的壳行为：导航时如何取内容、状态更新时是否重建、进入时是否需要额外刷新。
/// 导航（<see cref="ShellController.SelectAsync"/>）、状态刷新（<see cref="ShellController.OnStatusUpdated"/>）
/// 与壳视图的进入动作都读这张表，不再各自维护一份页面清单。
/// </summary>
internal sealed record ShellPageRoute(
    ShellPage Page,
    ShellPageContentMode Mode,
    bool RefreshesOnStatus,
    bool TogglesAuthPage,
    bool RefreshesProfilesOnEnter)
{
    public bool IsSyncProjection => Mode == ShellPageContentMode.SyncProjection;
}

/// <summary>页面路由的单一来源；每个 <see cref="ShellPage"/> 必须恰好出现一次（有回归用例约束）。</summary>
internal static class ShellRoutes
{
    public static readonly IReadOnlyList<ShellPageRoute> All =
    [
        new(ShellPage.Overview, ShellPageContentMode.SyncProjection,
            RefreshesOnStatus: true, TogglesAuthPage: false, RefreshesProfilesOnEnter: false),
        new(ShellPage.Authentication, ShellPageContentMode.SyncProjection,
            RefreshesOnStatus: true, TogglesAuthPage: true, RefreshesProfilesOnEnter: false),
        new(ShellPage.Diagnostics, ShellPageContentMode.SyncProjection,
            RefreshesOnStatus: true, TogglesAuthPage: false, RefreshesProfilesOnEnter: false),
        new(ShellPage.Logs, ShellPageContentMode.AsyncLoad,
            RefreshesOnStatus: false, TogglesAuthPage: false, RefreshesProfilesOnEnter: false),
        new(ShellPage.Settings, ShellPageContentMode.SyncProjection,
            RefreshesOnStatus: true, TogglesAuthPage: false, RefreshesProfilesOnEnter: true)
    ];

    private static readonly IReadOnlyDictionary<ShellPage, ShellPageRoute> ByPage =
        All.ToDictionary(route => route.Page);

    public static ShellPageRoute For(ShellPage page) => ByPage[page];
}
