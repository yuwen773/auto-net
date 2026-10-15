using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace AutoNet.Tray.Avalonia;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        themeSystem.Click += (_, _) => ApplyTheme(ThemeVariant.Default);
        themeLight.Click += (_, _) => ApplyTheme(ThemeVariant.Light);
        themeDark.Click += (_, _) => ApplyTheme(ThemeVariant.Dark);
        runInfo.Text = string.Join('\n',
            "进程：" + (Environment.ProcessPath ?? "(未知)"),
            "管理员：" + (Elevation.IsAdministrator ? "是" : "否") + "　互斥：" + Program.MutexName,
            "Avalonia " + (typeof(Application).Assembly.GetName().Version?.ToString() ?? "(未知)") +
                "　.NET " + Environment.Version);
        UpdateThemeLabel();
    }

    private static void ApplyTheme(ThemeVariant variant)
    {
        if (Application.Current is { } application) application.RequestedThemeVariant = variant;
    }

    private void UpdateThemeLabel()
    {
        var current = Application.Current?.RequestedThemeVariant;
        themeCurrent.Text = "当前：" + (current == ThemeVariant.Light ? "浅色"
            : current == ThemeVariant.Dark ? "深色"
            : "跟随系统");
    }
}
