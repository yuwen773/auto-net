namespace AutoNet.Tray.Avalonia;

/// <summary>托盘菜单动作。</summary>
internal enum TrayMenuAction
{
    Open,
    /// <summary>查看日志（#7）：打开主窗口并导航到日志页，替代既有托盘的独立日志窗口。</summary>
    Logs,
    Settings,
    /// <summary>自动恢复开关：标签与可用性随状态同步（由 <see cref="OverviewModel.RecoverySwitch"/> 派生），点击发送明确意图。</summary>
    RecoverySwitch,
    /// <summary>解除认证暂停（#5）：标签与可用性随状态同步（由 <see cref="OverviewModel.UnblockEntry"/> 派生），点击走独立提权入口。</summary>
    UnblockAuthentication,
    Exit
}

/// <summary>一个托盘菜单条目。<see cref="Separator"/> 表示分隔线。</summary>
internal sealed record TrayMenuEntry(string Label, TrayMenuAction? Action)
{
    /// <summary>分隔线：无标签、无动作。</summary>
    public static readonly TrayMenuEntry Separator = new("", null);
}

/// <summary>
/// 迁移期托盘菜单：保留既有核心入口语义——打开、查看日志（#7：打开主窗口并导航到日志页，
/// 与既有托盘「查看日志」同语义）、设置认证信息…（独立提权入口）、
/// 自动恢复开关（#4：与概览开关同源，默认显示“待确认”占位，运行时随状态同步）、
/// 解除认证暂停…（#5：与概览解除入口同源，仅在存在待解除阻断时可用，点击发起 --unblock-auth）、
/// 退出托盘（后台继续运行）。普通托盘互斥锁始终不拦截独立管理员操作。
/// </summary>
internal static class TrayMenuModel
{
    public static readonly IReadOnlyList<TrayMenuEntry> Entries =
    [
        new("打开 AutoNet", TrayMenuAction.Open),
        new("查看日志", TrayMenuAction.Logs),
        new("设置认证信息…", TrayMenuAction.Settings),
        new("自动恢复状态待确认", TrayMenuAction.RecoverySwitch),
        new(OverviewModel.PlaceholderLabel, TrayMenuAction.UnblockAuthentication),
        TrayMenuEntry.Separator,
        new("退出托盘（后台继续运行）", TrayMenuAction.Exit)
    ];
}
