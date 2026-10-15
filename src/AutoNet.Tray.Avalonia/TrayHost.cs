using Avalonia;
using Avalonia.Controls;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 托盘图标与菜单。菜单动作与既有 WinForms 托盘同名同序：
/// 打开、设置认证信息…（独立提权入口）、退出托盘（后台继续运行）。
/// </summary>
internal sealed class TrayHost : IDisposable
{
    private readonly TrayIcons icons;
    private readonly MainWindow window;
    private readonly TraySession session;
    private readonly Action shutdown;

    public TrayHost(TraySession session, MainWindow window, Action shutdown)
    {
        this.session = session;
        this.window = window;
        this.shutdown = shutdown;
        var open = new NativeMenuItem("打开 AutoNet");
        open.Click += (_, _) => ShowMainWindow();
        var settings = new NativeMenuItem("设置认证信息…");
        settings.Click += (_, _) => Elevation.Launch(ElevatedOperationKind.Settings);
        var exit = new NativeMenuItem("退出托盘（后台继续运行）");
        exit.Click += (_, _) => RequestExit();
        var tray = new TrayIcon
        {
            Icon = AppIcons.Load(),
            ToolTipText = "AutoNet · 校园网连接守护（兼容原型）",
            IsVisible = true,
            Menu = new NativeMenu { open, settings, new NativeMenuItemSeparator(), exit }
        };
        tray.Clicked += (_, _) => ShowMainWindow();
        icons = new TrayIcons { tray };
        TrayIcon.SetIcons(Application.Current!, icons);
    }

    /// <summary>与托盘菜单“打开 AutoNet”相同的动作。</summary>
    public void ShowMainWindow() => ShowWindow(window);

    /// <summary>与托盘菜单“退出托盘（后台继续运行）”相同的动作：只结束界面进程，不触碰服务。</summary>
    public void RequestExit()
    {
        session.RequestShutdown();
        shutdown();
    }

    public static void ShowWindow(MainWindow window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void Dispose()
    {
        foreach (var icon in icons) icon.IsVisible = false;
        TrayIcon.SetIcons(Application.Current!, null);
        icons.Clear();
    }
}
