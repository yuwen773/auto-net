using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AutoNet.Tray.Avalonia;

/// <summary>按模式启动 Avalonia 桌面生命周期，并把退出代码带回进程。</summary>
internal static class AppHost
{
    public static int RunTray(PrototypeCommandLine request, QaEnvironment qa) => Run(new TrayMode(request, qa));

    public static int RunElevated(ElevatedOperationKind operation, QaEnvironment qa) => Run(new ElevatedMode(operation, qa));

    public static void RunNotice(string heading, string body) => Run(new NoticeMode(heading, body));

    private static int Run(AppMode mode)
    {
        // OnExplicitShutdown：窗口关闭只隐藏，进程只由托盘“退出”或提示窗口关闭结束（故事 6、7）。
        var lifetime = new ClassicDesktopStyleApplicationLifetime
        {
            Args = [],
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        var builder = AppBuilder.Configure(() => new App(mode)).UsePlatformDetect();
        builder.SetupWithLifetime(lifetime);
        return lifetime.Start([]);
    }
}
