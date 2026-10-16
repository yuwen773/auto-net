using Avalonia.Controls;

namespace AutoNet.Tray.Avalonia;

public partial class MainWindow : Window
{
    private readonly TraySession session;
    private readonly ShellController? controller;

    /// <summary>供 XAML 资源运行时加载器使用的无参构造；应用内始终注入托盘会话与壳控制器。</summary>
    public MainWindow() : this(new TraySession(), null, null) { }

    internal MainWindow(
        TraySession session,
        ShellController? controller,
        OverviewActions? actions,
        LogsPage? logs = null,
        Func<string, Task>? copy = null,
        Action? openFolder = null,
        Action? modifySettings = null)
    {
        this.session = session;
        this.controller = controller;
        Icon = AppIcons.Load();
        InitializeComponent();
        if (controller is not null)
        {
            view.Attach(controller, actions, logs, copy, openFolder, modifySettings);
            // 窗口每次激活时刷新一次状态；定期轮询仍由状态管道负责。
            Activated += (_, _) => _ = RefreshStatusSafelyAsync();
        }
    }

    /// <summary>关闭窗口只是隐藏到托盘；只有托盘退出请求才允许真正关闭（故事 6）。</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (session.ShouldHideOnClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private async Task RefreshStatusSafelyAsync()
    {
        try
        {
            if (controller is not null) await controller.RefreshStatusAsync();
        }
        catch (Exception)
        {
            // 激活时的刷新失败不打断界面；周期读取会自行恢复。
        }
    }
}
