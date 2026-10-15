using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>应用启动模式：普通壳（托盘）、管理员操作、提示窗口。</summary>
internal abstract record AppMode;

internal sealed record TrayMode(StartupRequest Request, QaEnvironment Qa) : AppMode;

internal sealed record ElevatedMode(ElevatedOperationKind Operation, QaEnvironment Qa) : AppMode;

internal sealed record NoticeMode(string Heading, string Body) : AppMode;

public sealed class App : Application
{
    private readonly AppMode mode;
    private TrayHost? trayHost;

    /// <summary>供 XAML 资源运行时加载器使用的无参构造；正式启动一律通过 AppHost 注入模式。</summary>
    public App() : this(new NoticeMode("AutoNet", "此入口仅用于 XAML 设计时加载。")) { }

    internal App(AppMode mode) => this.mode = mode;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 托盘语义：窗口关闭只隐藏，进程只能在托盘“退出”时结束（故事 6、7）。
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            switch (mode)
            {
                case TrayMode tray:
                    StartTrayMode(desktop, tray);
                    break;
                case ElevatedMode elevated:
                    StartElevated(desktop, elevated);
                    break;
                case NoticeMode notice:
                    StartNotice(desktop, notice.Heading, notice.Body);
                    break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 管理员操作（#4 起）：恢复开关按明确目标改写已保存设置；解除认证暂停（#5）与设置（#8）
    /// 仍为受控占位。成功保持既有“静默成功”语义（结果经状态管道回到界面），失败显示受控提示。
    /// </summary>
    private static void StartElevated(IClassicDesktopStyleApplicationLifetime desktop, ElevatedMode elevated)
    {
        if (elevated.Operation is ElevatedOperationKind.EnableRecovery or ElevatedOperationKind.DisableRecovery)
        {
            if (elevated.Qa.Active)
            {
                // QA 不修改本机真实设置：只保留 Program 已写入的“已提权直接执行”记录并退出。
                Dispatcher.UIThread.Post(() => desktop.Shutdown(0));
                return;
            }

            try
            {
                ElevatedRecovery.Apply(elevated.Operation == ElevatedOperationKind.EnableRecovery);
                Dispatcher.UIThread.Post(() => desktop.Shutdown(0));
            }
            catch (Exception e)
            {
                StartNotice(desktop, "操作未完成", "操作未完成：" + e.GetType().Name + "。请检查后台服务是否已安装，然后重试。");
            }

            return;
        }

        // 受控占位：入口已成立（不被普通互斥锁拦截），具体页面随后续工单提供。
        StartNotice(desktop, elevated.Operation.DisplayName() + "（迁移中）",
            "该操作将在界面迁移完成后通过新的页面提供。\n当前版本不会修改任何设置、凭据或服务。");
    }

    private void StartTrayMode(IClassicDesktopStyleApplicationLifetime desktop, TrayMode tray)
    {
        var session = new TraySession();
        var pipe = new StatusPipe(StatusPipe.ResolveStatusPath());
        var shell = new ShellController(pipe);
        // 概览 QA 用记录适配器替换真实提权启动：只记录明确意图，不弹 UAC；生产路径始终 Elevation.Launch。
        Action<ElevatedOperationKind> launcher = tray.Qa.Mode == QaMode.Overview ? QaOverview.RecordLaunch : Elevation.Launch;
        var actions = new OverviewActions(pipe.RefreshAsync, launcher);
        var window = new MainWindow(session, shell, actions);
        trayHost = new TrayHost(session, window, shell, actions, () => desktop.Shutdown());
        desktop.Exit += (_, _) =>
        {
            trayHost.Dispose();
            shell.Dispose();
        };
        shell.Start();

        if (!tray.Request.StartHidden) TrayHost.ShowWindow(window);
        switch (tray.Qa.Mode)
        {
            case QaMode.Lifecycle:
            case QaMode.Hold:
                QaLifecycle.Run(tray.Request, trayHost, window, tray.Qa);
                break;
            case QaMode.ThemeRender:
                QaThemeRender.Run(window, tray.Qa, desktop);
                break;
            case QaMode.Shell:
                QaShell.Run(shell, window, tray.Qa, code => desktop.Shutdown(code));
                break;
            case QaMode.Overview:
                QaOverview.Run(shell, actions, window, tray.Qa, code => desktop.Shutdown(code));
                break;
        }
    }

    private static void StartNotice(IClassicDesktopStyleApplicationLifetime desktop, string heading, string body)
    {
        var notice = NoticeWindow.Create(heading, body);
        notice.Show();
        notice.Closed += (_, _) => desktop.Shutdown();
    }
}
