namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 托盘会话状态机：关闭窗口默认隐藏到托盘，只有托盘“退出”请求才真正结束进程。
/// 与既有 WinForms 语义一致（故事 6、7）。
/// </summary>
internal sealed class TraySession
{
    public bool IsShuttingDown { get; private set; }

    public bool ShouldHideOnClose => !IsShuttingDown;

    public void RequestShutdown() => IsShuttingDown = true;
}
