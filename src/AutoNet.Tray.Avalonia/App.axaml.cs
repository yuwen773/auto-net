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
    /// 管理员操作（#4 起）：恢复开关按明确目标改写已保存设置；解除认证暂停（#5）走
    /// 确认 → 只读复核 → 修订核对 → 持久审计后解除的完整安全流程；设置（#8）仍为受控占位。
    /// 成功保持既有“静默成功”语义（结果经状态管道回到界面），需要说明的结果显示受控提示。
    /// </summary>
    private static void StartElevated(IClassicDesktopStyleApplicationLifetime desktop, ElevatedMode elevated)
    {
        if (elevated.Operation is ElevatedOperationKind.UnblockAuthentication)
        {
            // 只有 unblock 模式才走隔离夹具：其他 QA 模式走到这里仍按生产语义执行，
            // 绝不因为“设了 AUTONET_QA_DIR”就改用夹具。
            if (elevated.Qa.Mode == QaMode.Unblock)
            {
                QaUnblock.Run(elevated.Qa, code => desktop.Shutdown(code));
                return;
            }

            StartUnblock(desktop);
            return;
        }

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

    /// <summary>
    /// 解除认证暂停（#5 / T08 / 故事 42）：提权进程内完成确认 → 只读复核 → 修订核对 → 审计后解除。
    /// 流程需要交互与异步诊断，因此整段在界面线程上驱动；拒绝 UAC 时本进程根本不会运行到这里，
    /// 拒绝确认、复核未通过或修订变化都在写入之前中止并保留认证暂停。
    /// </summary>
    private static void StartUnblock(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var unblock = UnblockAuthentication.Production(_ =>
                    ConfirmWindow.AskAsync(UnblockAuthentication.ConfirmHeading, UnblockAuthentication.ConfirmBody));
                var result = await unblock.RunAsync();

                // 成功与用户取消保持静默：结果经状态管道回到界面，不弹无信息量的窗口。
                if (result.RequiresNotice)
                    StartNotice(desktop, UnblockAuthentication.HeadingFor(result.Outcome), result.Message, width: 560);
                else
                    desktop.Shutdown(0);
            }
            catch (Exception e)
            {
                // 安全边界：SafeSummary 与 lastError 是仅有的异常出口，绝不携带异常文本或 URL。
                // 这里只报异常类型，并明确告知认证暂停保持不变（审计写入失败等场景的真实结论）。
                StartNotice(desktop, "解除认证暂停未完成",
                    "解除认证暂停未完成：" + e.GetType().Name + "。\n认证暂停保持不变；请检查后台服务是否已安装，然后重试。");
            }
        });
    }

    private void StartTrayMode(IClassicDesktopStyleApplicationLifetime desktop, TrayMode tray)
    {
        var session = new TraySession();
        var pipe = new StatusPipe(StatusPipe.ResolveStatusPath());
        // 日志页（#7）：读取既有日志目录；QA 夹具模式只在 logs 模式下重定向到证据目录内的夹具。
        var logsPage = LogsPage.Production(tray.Qa);
        var shell = new ShellController(pipe, (page, token) =>
            page == ShellPage.Logs ? logsPage.LoadAsync(token) : ShellContent.LoadAsync(page, token));
        // 页内更新与导航加载汇聚到同一守卫：旧结果不覆写当前页（故事 16、50）。
        logsPage.Updated += content => shell.ApplyExternal(content);
        // 概览 QA 用记录适配器替换真实提权启动：只记录明确意图，不弹 UAC；生产路径始终 Elevation.Launch。
        Action<ElevatedOperationKind> launcher = tray.Qa.Mode == QaMode.Overview ? QaOverview.RecordLaunch : Elevation.Launch;
        var actions = new OverviewActions(pipe.RefreshAsync, launcher);
        // 日志页复制与目录打开在 QA 模式下替换为受控空操作：不触碰用户剪贴板、不启动资源管理器。
        // 复制文本经 QA 报告的 CopyText 逐字核验；目录入口存在性由截图核验（真实点击为人工项）。
        Func<string, Task>? copyHandler = tray.Qa.Mode == QaMode.Logs ? _ => Task.CompletedTask : null;
        Action? folderHandler = tray.Qa.Mode == QaMode.Logs ? () => { } : null;
        var window = new MainWindow(session, shell, actions, logsPage, copyHandler, folderHandler);
        trayHost = new TrayHost(session, window, shell, actions, () => desktop.Shutdown());
        desktop.Exit += (_, _) =>
        {
            trayHost.Dispose();
            shell.Dispose();
            logsPage.Dispose();
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
            case QaMode.Diagnostics:
                QaDiagnostics.Run(shell, window, tray.Qa, code => desktop.Shutdown(code));
                break;
            case QaMode.Logs:
                QaLogs.Run(shell, logsPage, window, tray.Qa, code => desktop.Shutdown(code));
                break;
        }
    }

    private static void StartNotice(IClassicDesktopStyleApplicationLifetime desktop, string heading, string body, double width = 440)
    {
        var notice = NoticeWindow.Create(heading, body, width);
        notice.Show();
        notice.Closed += (_, _) => desktop.Shutdown();
    }
}
