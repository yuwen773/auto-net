using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AutoNet.Tray.Avalonia;

/// <summary>按模式启动 Avalonia 桌面生命周期，并把退出代码带回进程。</summary>
internal static class AppHost
{
    public static int RunTray(StartupRequest request, QaEnvironment qa) => Run(new TrayMode(request, qa));

    public static int RunElevated(ElevatedOperationKind operation, QaEnvironment qa) => Run(new ElevatedMode(operation, qa));

    public static void RunNotice(string heading, string body) => Run(new NoticeMode(heading, body));

    private static int Run(AppMode mode)
    {
        // ShutdownMode 在 App.OnFrameworkInitializationCompleted 统一设置（托盘语义，见 App.axaml.cs）。
        var lifetime = new ClassicDesktopStyleApplicationLifetime
        {
            Args = []
        };
        var builder = AppBuilder.Configure(() => new App(mode)).UsePlatformDetect();
        builder.SetupWithLifetime(lifetime);
        return lifetime.Start([]);
    }
}
