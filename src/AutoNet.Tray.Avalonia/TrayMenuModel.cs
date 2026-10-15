namespace AutoNet.Tray.Avalonia;

/// <summary>托盘菜单动作。</summary>
internal enum TrayMenuAction
{
    Open,
    Settings,
    Exit
}

/// <summary>一个托盘菜单条目。<see cref="Separator"/> 表示分隔线。</summary>
internal sealed record TrayMenuEntry(string Label, TrayMenuAction? Action)
{
    /// <summary>分隔线：无标签、无动作。</summary>
    public static readonly TrayMenuEntry Separator = new("", null);
}

/// <summary>
/// 迁移期托盘菜单：保留既有核心入口语义——打开、设置认证信息…（独立提权入口）、退出托盘（后台继续运行）。
/// 既有完整菜单中的自动恢复开关、解除认证暂停与查看日志，属于后续切片（#4/#5/#7），届时按原语义接入；
/// 本票不提前实现其逻辑，也不改变上述核心入口的含义。普通托盘互斥锁始终不拦截独立管理员操作。
/// </summary>
internal static class TrayMenuModel
{
    public static readonly IReadOnlyList<TrayMenuEntry> Entries =
    [
        new("打开 AutoNet", TrayMenuAction.Open),
        new("设置认证信息…", TrayMenuAction.Settings),
        TrayMenuEntry.Separator,
        new("退出托盘（后台继续运行）", TrayMenuAction.Exit)
    ];
}
