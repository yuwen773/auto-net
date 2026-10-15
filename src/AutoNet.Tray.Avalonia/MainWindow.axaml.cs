using Avalonia.Controls;

namespace AutoNet.Tray.Avalonia;

public partial class MainWindow : Window
{
    private readonly TraySession session;

    /// <summary>供 XAML 资源运行时加载器使用的无参构造；应用内始终注入托盘会话。</summary>
    public MainWindow() : this(new TraySession()) { }

    internal MainWindow(TraySession session)
    {
        this.session = session;
        Icon = AppIcons.Load();
        InitializeComponent();
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
}
