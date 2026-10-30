using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>应用启动模式：普通壳（托盘）、管理员操作、提示窗口。</summary>
internal abstract record AppMode;

internal sealed record TrayMode(StartupRequest Request, QaEnvironment Qa) : AppMode;

internal sealed record ElevatedMode(ElevatedOperationKind Operation, QaEnvironment Qa, string? ExportTargetPath = null) : AppMode;

internal sealed record NoticeMode(string Heading, string Body) : AppMode;

public sealed class App : Application
{
    private readonly AppMode mode;
    private TrayHost? trayHost;
    private ThemeController? theme;

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
            // 主题偏好（#9 / 故事 26、30）：任何模式（托盘、提权操作、提示窗口）在任何窗口
            // 显示之前应用同一份用户偏好，主界面与自有操作窗口明暗一致；读取失败回退为跟随系统。
            // UAC 提权以同一 Windows 账户运行时读到同一偏好文件；跨管理员账户提权时偏好
            // 按账户各自隔离（故事 70 的用户隔离语义），一致性作为人工验收项披露。
            // QA 模式下偏好文件重定向到证据目录，绝不读写本机真实用户偏好。
            theme = ThemeController.LoadAndApply(mode switch
            {
                TrayMode tray => tray.Qa,
                ElevatedMode elevated => elevated.Qa,
                _ => QaEnvironment.Inactive
            });
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
    /// 确认 → 只读复核 → 修订核对 → 持久审计后解除的完整安全流程；设置（#8）打开管理员编辑窗口，
    /// 保存按既有规则写入凭据与设置并在窗口内确认，取消不写入，保存不清认证阻断。
    /// 需要说明的结果显示受控提示（只披露异常类型，绝不回显异常文本）。
    /// </summary>
    private static void StartElevated(IClassicDesktopStyleApplicationLifetime desktop, ElevatedMode elevated)
    {
        if (elevated.Operation is ElevatedOperationKind.RestartAuthentication)
        {
            if (elevated.Qa.Active) { Dispatcher.UIThread.Post(() => desktop.Shutdown(0)); return; }
            StartRestartAuthentication(desktop);
            return;
        }
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

        if (elevated.Operation is ElevatedOperationKind.Settings)
        {
            DispatchSettings(elevated.Qa,
                () => StartSettingsEditor(desktop),
                () => QaSettings.RunEditor(elevated.Qa, code => desktop.Shutdown(code)),
                () => desktop.Shutdown(0));
            return;
        }

        if (elevated.Operation is ElevatedOperationKind.DiagnosticsExport)
        {
            // 只有 export 模式才走隔离夹具：其他 QA 模式走到这里仍按生产语义执行。
            if (elevated.Qa.Mode == QaMode.ExportDiagnostics)
            {
                QaExport.RunElevated(elevated, elevated.Qa, code => desktop.Shutdown(code));
                return;
            }

            StartDiagnosticsExport(desktop, elevated);
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

    private static void StartRestartAuthentication(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var store = new SafetyStateStore(Storage.Private);
                var restart = new RestartAuthentication(store, () => ConfirmWindow.AskAsync("重新开始认证",
                    "此操作会重置凭据提交额度，并可能允许之后再次提交凭据。认证暂停和主动断线额度保持不变。\n\n确认重新开始认证？",
                    "确认重新开始", cancelByDefault: true));
                var result = await restart.RunAsync();
                if (result == RestartAuthenticationOutcome.Cancelled) { desktop.Shutdown(0); return; }
                if (result == RestartAuthenticationOutcome.StorageFailure)
                {
                    StartNotice(desktop, "重新开始认证未完成", "安全状态或审计存储不可用，或状态记录损坏；操作未能确认完成。部分记录可能已更新；请检查本地存储和认证诊断，再重新检查后重试。");
                    return;
                }
                if (result == RestartAuthenticationOutcome.Changed)
                {
                    StartNotice(desktop, "重新开始认证未完成", "认证正在进行或安全状态已变化；本次未重置提交额度，请重新检查后重试。");
                    return;
                }
                StartNotice(desktop, "已重置提交额度", "提交额度已重置。认证暂停仍保持原状；如需解除暂停，请先检查认证诊断。");
            }
            catch (Exception e)
            {
                StartNotice(desktop, "重新开始认证未完成", "操作未完成：" + e.GetType().Name + "。请检查安全状态后重试。");
            }
        });
    }

    /// <summary>
    /// 管理员设置编辑（#8 / T07 / 故事 35–39）：提权进程内打开编辑窗口，既有受限存储语义不变。
    /// private 目录缺失时显示安装提示（与既有 WinForms 托盘同语义）；取消或保存成功都以
    /// 窗口关闭收尾；打开失败只披露异常类型，不修改任何配置。
    /// </summary>
    private static void StartSettingsEditor(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var window = SettingsWindow.Create(ElevatedSettings.Production());
                if (window is null)
                {
                    StartNotice(desktop, "AutoNet", ElevatedSettings.MissingStoreMessage);
                    return;
                }

                window.Closed += (_, _) => desktop.Shutdown(0);
                window.Show();
            }
            catch (Exception e)
            {
                StartNotice(desktop, "设置未完成",
                    "设置界面无法打开：" + e.GetType().Name + "。\n当前不会修改任何设置、凭据或服务。");
            }
        });
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

    /// <summary>
    /// 管理员诊断包导出（#12 / T13 / 故事 73、75–77）：提权进程按普通界面逐字传来的目标路径
    /// 执行只读采集并写共享结果文件；退出码把最终结果带回界面（0 成功 / 3 失败），窗口无 UI。
    /// </summary>
    private static void StartDiagnosticsExport(IClassicDesktopStyleApplicationLifetime desktop, ElevatedMode elevated)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var code = DiagnosticsExportJob.Run(elevated.ExportTargetPath, DiagnosticsExportJob.ProductionResultPath);
            desktop.Shutdown(code);
        });
    }

    private void StartTrayMode(IClassicDesktopStyleApplicationLifetime desktop, TrayMode tray)
    {
        var session = new TraySession();
        var pipe = new StatusPipe(StatusPipe.ResolveStatusPath());
        // 浏览器兜底开页（#11 / 故事 61–69）：消费记录按 Windows 用户持久化（QA 重定向证据目录）。
        // 浏览器启动经默认 shell 只带固定门户根地址；QA 模式替换为受控记录器，不弹真实浏览器。
        Func<string, string?> browserLaunch = tray.Qa.Mode == QaMode.PortalOpen
            ? QaPortal.RecordLaunch
            : url =>
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return null;
                }
                catch (Exception e) { return e.GetType().Name; }
            };
        var portalOpener = new PortalPageOpener(PortalOpenStore.Create(tray.Qa), pipe.RefreshAsync, browserLaunch);
        // 日志页（#7）：读取既有日志目录；QA 夹具模式只在 logs 模式下重定向到证据目录内的夹具。
        var logsPage = LogsPage.Production(tray.Qa);
        // 认证页（#37）：浏览器宿主抽象。生产宿主在 #38 接入受控网络边界前失败关闭，
        // 不创建浏览器、不发起网络请求；auth-page QA 用假宿主验证页面生命周期。
        IAuthPageBrowserHost authHost = tray.Qa.Mode == QaMode.AuthPage
            ? new QaAuthPageHost()
            : AuthPageBrowserHostFactory.Production();
        // 状态投影在读取线程上发起，协调器经此调度器把内容与事件交给界面线程；
        // 已在界面线程时同步执行，避免 QA 驱动时多一次排队。
        var authPage = new AuthPageCoordinator(authHost, action =>
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.Post(action);
        });
        var shell = new ShellController(pipe, (page, token) =>
            page == ShellPage.Logs ? logsPage.LoadAsync(token) : ShellContent.LoadAsync(page, token), portalOpener, authPage);
        // 页内更新与导航加载汇聚到同一守卫：旧结果不覆写当前页（故事 16、50）。
        logsPage.Updated += content => shell.ApplyExternal(content);
        // 概览/可访问性 QA 用记录适配器替换真实提权启动：只记录明确意图，不弹 UAC；生产路径始终 Elevation.Launch。
        Action<ElevatedOperationKind> launcher = tray.Qa.Mode switch
        {
            QaMode.Overview => QaOverview.RecordLaunch,
            QaMode.Accessibility => QaAccessibility.RecordLaunch,
            _ => Elevation.Launch
        };
        var actions = new OverviewActions(pipe.RefreshAsync, launcher);
        // 设置页（#8 / #41）：普通权限下直接编辑非秘密字段；保存经独立提权交接。
        // 受限配置不从普通进程读取，非秘密值来自 status.json 的配置投影。
        Func<IReadOnlyList<WifiProfile>> loadProfiles = tray.Qa.Active
            ? () => QaSettings.Profiles
            : () => Wifi.Profiles().Where(profile => profile.AllUsers).ToList();
        Func<SettingsDraft, CancellationToken, Task<HandoffResult>> saveSettings = tray.Qa.Mode == QaMode.Settings
            ? QaSettings.SaveForQaAsync
            : tray.Qa.Active
                ? (_, _) => Task.FromResult(new HandoffResult(HandoffOutcome.Rejected))
                : SettingsHandoffLauncher.SaveAsync;
        var settingsPage = new SettingsPage(loadProfiles, saveSettings, () => pipe.RefreshAsync());
        // 主题（#9 / 故事 27–29）：设置页外观分区直接读写偏好并当场应用（不走状态投影，
        // 轮询重建不打断选择）；跟随模式下响应系统主题变化，手动选择保持优先。
        // 订阅挂在长驻的托盘进程；提权/提示窗口是短生命周期进程，跟随语义由框架对
        // Default 变体的平台跟随覆盖（故事 29 的真实系统切换验证为人工项）。
        if (Application.Current?.PlatformSettings is { } platformSettings)
            platformSettings.ColorValuesChanged += (_, _) => theme!.OnSystemThemeChanged();
        Func<ThemePreference> themeProvider = () => theme!.Current;
        Action<ThemePreference> themeSelected = preference => _ = theme!.Select(preference);
        // 日志页复制与目录打开在 QA 模式下替换为受控空操作：不触碰用户剪贴板、不启动资源管理器。
        // 复制文本经 QA 报告的 CopyText 逐字核验；目录入口存在性由截图核验（真实点击为人工项）。
        Func<string, Task>? copyHandler = tray.Qa.Mode is QaMode.Logs or QaMode.Accessibility ? _ => Task.CompletedTask : null;
        Action? folderHandler = tray.Qa.Mode is QaMode.Logs or QaMode.Accessibility ? () => { } : null;
        // 诊断包导出（#12 / 故事 71–74、81）：普通界面状态机驱动——保存对话框与提权启动
        // 都以委托注入；export-diagnostics 与可访问性 QA 模式替换为受控夹具（不弹对话框、不弹 UAC）。
        MainWindow? window = null;
        Func<Task<string?>> pickTarget;
        Func<string, Task<DiagnosticsExportLaunch?>> exportLaunch;
        if (tray.Qa.Mode is QaMode.ExportDiagnostics or QaMode.Accessibility)
        {
            pickTarget = QaExport.PickAsync;
            exportLaunch = QaExport.LaunchAsync;
        }
        else
        {
            pickTarget = () => PickDiagnosticTargetAsync(window);
            exportLaunch = LaunchDiagnosticsExportAsync;
        }
        var diagnosticsExporter = new DiagnosticsExporter(pickTarget, exportLaunch);
        // 内嵌认证通道（工单 #24/#25 / ADR-0009）：发起器消费服务置位的授予并经 UAC 提权
        // 启动辅助进程（辅助进程自读机器级凭据）。结果回读经受控关键通知呈现（不静默）；
        // 通道无法运行时回退系统浏览器兜底。可访问性 QA 模式全部替换为受控记录/空操作。
        List<NotificationAlert>? qaPresented = tray.Qa.Mode == QaMode.Notifications ? [] : null;
        TrayBalloonNotifier? notifier = null;
        var authChannelStart = tray.Qa.Mode == QaMode.Accessibility
            ? QaAuthChannel.RecordLaunch
            : (Func<string, Process?>)(resultPath => Elevation.LaunchAuthChannel(resultPath, Elevation.StartProcess));
        Action<NotificationAlert>? authChannelPresent = tray.Qa.Mode == QaMode.Accessibility
            ? null
            : alert => { if (notifier is { } balloon) balloon.Show(alert); };
        Func<Task>? authChannelFallback = tray.Qa.Mode == QaMode.Accessibility
            ? null
            : () => portalOpener.OpenManuallyAsync();
        var authChannelLauncher = new AuthChannelLauncher(
            async () => (await pipe.RefreshAsync())?.Snapshot, authChannelStart, authChannelPresent, authChannelFallback,
            launchActive: () => new AutoNet.Core.SafetyStateStore(AutoNet.Core.Storage.Root).IsChannelLaunchActive());
        // 兜底开页手动入口（#11）：可访问性 QA 只记录动作意图，不启动浏览器。
        Action portalOpenManual = tray.Qa.Mode == QaMode.Accessibility
            ? QaAccessibility.RecordPortalOpen
            : () => _ = portalOpener.OpenManuallyAsync();
        window = new MainWindow(session, shell, actions, logsPage, copyHandler, folderHandler, settingsPage,
            portalOpen: portalOpenManual, themeProvider, themeSelected, diagnosticsExporter, authChannelLauncher, authPage);
        // 关键通知消费（#10 / T10 / 故事 52–60）：路由把状态读取映射为受控提醒决定（一次提醒、
        // 不重播、未知码降级），呈现交给托盘气泡（复用同一图标）；消费记录按 Windows 用户持久化。
        // notifications QA 模式下呈现替换为受控记录器且不订阅管道——由 QaNotifications 手工驱动
        // 确定时序；消费记录经 Create(qa) 落证据目录，不弹真实气泡、不触碰本机真实记录。
        var notifications = new NotificationRouter(
            NotificationConsumptionStore.Create(tray.Qa),
            qaPresented is { } sink ? alert => { sink.Add(alert); return true; } : alert => notifier is { } balloon && balloon.Show(alert));
        trayHost = new TrayHost(session, window, shell, actions, () => desktop.Shutdown());
        if (qaPresented is null)
        {
            notifier = new TrayBalloonNotifier(trayHost.TrayIcon!);
            pipe.Updated += notifications.Observe;
        }
        // 自动开页与认证通道由生产状态读取驱动；所有 QA 模式都隔离真实动作。
        // portal-open 仍由 QaPortal 手工驱动受控记录器，不受此订阅边界影响。
        AttachAutomaticActions(pipe, tray.Qa,
            reading => _ = portalOpener.EvaluateAsync(reading),
            reading => _ = authChannelLauncher.ObserveAsync(reading.Snapshot));
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
            case QaMode.Settings:
                QaSettings.Run(shell, window, settingsPage, tray.Qa, code => desktop.Shutdown(code));
                break;
            case QaMode.ThemePrefs:
                QaThemePrefs.Run(shell, window, theme!, tray.Qa, code => desktop.Shutdown(code));
                break;
            case QaMode.Notifications:
                QaNotifications.Run(shell, window, notifications, qaPresented!, tray.Qa, code => desktop.Shutdown(code));
                break;
            case QaMode.PortalOpen:
                QaPortal.Run(shell, window, pipe, portalOpener, tray.Qa, code => desktop.Shutdown(code));
                break;
            case QaMode.ExportDiagnostics:
                QaExport.Run(shell, window, diagnosticsExporter, tray.Qa, code => desktop.Shutdown(code));
                break;
            case QaMode.Accessibility:
                QaAccessibility.Run(shell, window, tray.Qa, code => desktop.Shutdown(code));
                break;
            case QaMode.AuthPage:
                QaAuthPage.Run(shell, authPage, window, tray.Qa, code => desktop.Shutdown(code));
                break;
        }
    }

    internal static void AttachAutomaticActions(StatusPipe pipe, QaEnvironment qa,
        Action<StatusReading> portalOpen, Action<StatusReading> authChannel)
    {
        if (qa.Active) return;
        pipe.Updated += portalOpen;
        pipe.Updated += authChannel;
    }

    internal static void DispatchSettings(QaEnvironment qa, Action production, Action fixture, Action stopProbe)
    {
        if (!qa.Active) production();
        else if (qa.Mode == QaMode.Settings) fixture();
        else stopProbe();
    }

    /// <summary>
    /// 生产路径：诊断包导出的保存位置选择（故事 72、78）。默认当前用户桌面 + 默认文件名；
    /// 同名覆盖由系统对话框原生确认；取消返回 null（调用方据此取消导出，不发起提权）。
    /// </summary>
    private static async Task<string?> PickDiagnosticTargetAsync(MainWindow? window)
    {
        if (window is null) return null;
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var options = new FilePickerSaveOptions
        {
            Title = "保存诊断包",
            SuggestedFileName = "autonet-diagnostics-" + DateTimeOffset.Now.ToLocalTime().ToString("yyyyMMdd-HHmmss") + ".zip",
            ShowOverwritePrompt = true
        };
        if (Directory.Exists(desktopPath))
            options.SuggestedStartLocation = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(desktopPath));
        var file = await window.StorageProvider.SaveFilePickerAsync(options);
        return file?.Path.LocalPath;
    }

    /// <summary>
    /// 生产路径：发起独立提权采集并异步等待其退出（界面保持普通权限且可操作，故事 81）。
    /// 结果文件匹配本次导出（目标逐字一致 + 时刻晚于发起）时补充缺口明细；不匹配或缺失时
    /// 标记明细未确认（界面只引导查看包内 README，不谎称无缺口）。等待上限三分钟：超时按
    /// 受控 Timeout 呈现（不杀死提权进程——若其稍后完成发布，以实际文件为准）。
    /// </summary>
    private static async Task<DiagnosticsExportLaunch?> LaunchDiagnosticsExportAsync(string target)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var process = Elevation.LaunchDiagnosticsExport(target, Elevation.StartProcess);
        if (process is null) return null; // 拒绝 UAC：取消导出（故事 74）
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new DiagnosticsExportLaunch(int.MinValue, "Timeout", [], DetailsConfirmed: false);
        }

        var outcome = DiagnosticsExportOutcome.TryRead(DiagnosticsExportJob.ProductionResultPath);
        var matching = outcome is { } read && read.Matches(target, startedAt) ? read : null;
        return new DiagnosticsExportLaunch(process.ExitCode, matching?.ErrorKind, matching?.Notes ?? [],
            DetailsConfirmed: matching is not null);
    }

    private static void StartNotice(IClassicDesktopStyleApplicationLifetime desktop, string heading, string body, double width = 440)
    {
        var notice = NoticeWindow.Create(heading, body, width);
        notice.Show();
        notice.Closed += (_, _) => desktop.Shutdown();
    }
}
