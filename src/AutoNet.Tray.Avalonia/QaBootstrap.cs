using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 生产路径注入的最终委托。QA 模式只在这些委托之上做受控替换，生产路径本身不再
/// 逐处书写 <c>qa.Mode == ...</c> 分支。
/// </summary>
internal sealed record QaCompositionContext(
    Func<string, string?> BrowserLaunch,
    Func<IAuthPageBrowserHost> AuthHost,
    Action<ElevatedOperationKind> Launcher,
    Func<IReadOnlyList<WifiProfile>> LoadProfiles,
    Func<SettingsDraft, CancellationToken, Task<HandoffResult>> SaveSettings,
    Func<string, Task>? CopyHandler,
    Action? FolderHandler,
    Func<Task<string?>> PickTarget,
    Func<string, Task<DiagnosticsExportLaunch?>> ExportLaunch,
    Func<string, Process?> AuthChannelStart,
    Action<NotificationAlert>? AuthChannelPresent,
    Func<Task>? AuthChannelFallback,
    Action PortalOpenManual);

/// <summary>
/// 一次装配后的委托集合：生产与 QA 的差异只在这里分叉。场景代码仍留在各自模块，
/// 本类只负责选择，不执行任何网络、浏览器或凭据操作。
/// </summary>
internal sealed record QaComposition(
    Func<string, string?> BrowserLaunch,
    IAuthPageBrowserHost AuthHost,
    Action<ElevatedOperationKind> Launcher,
    Func<IReadOnlyList<WifiProfile>> LoadProfiles,
    Func<SettingsDraft, CancellationToken, Task<HandoffResult>> SaveSettings,
    Func<string, Task>? CopyHandler,
    Action? FolderHandler,
    Func<Task<string?>> PickTarget,
    Func<string, Task<DiagnosticsExportLaunch?>> ExportLaunch,
    Func<string, Process?> AuthChannelStart,
    Action<NotificationAlert>? AuthChannelPresent,
    Func<Task>? AuthChannelFallback,
    Action PortalOpenManual,
    List<NotificationAlert>? PresentedSink)
{
    /// <summary>是否把真实通知气泡接到状态管道；notifications QA 用手工驱动，不订阅。</summary>
    public bool AttachNotifications => PresentedSink is null;
}

/// <summary>场景运行所需的已装配对象；只有 QA 模式会使用。</summary>
internal sealed record QaRunContext(
    StartupRequest Request,
    TrayHost TrayHost,
    MainWindow Window,
    ShellController Shell,
    OverviewActions Actions,
    LogsPage LogsPage,
    SettingsPage SettingsPage,
    ThemeController Theme,
    NotificationRouter Notifications,
    List<NotificationAlert> Presented,
    StatusPipe Pipe,
    PortalPageOpener PortalOpener,
    DiagnosticsExporter DiagnosticsExporter,
    AuthPageCoordinator AuthPage,
    IClassicDesktopStyleApplicationLifetime Desktop);

/// <summary>
/// QA 场景装配（工单 #14 / #37 / #39 等）：把所有「按 QA 模式替换真实动作」的决定与
/// 场景分发收在一处。生产路径只调用一次 <see cref="Compose"/>，再用返回的委托完成装配；
/// 新增 QA 场景时改这里与对应模块，不必在启动路径里再散落条件分支。
/// </summary>
internal static class QaBootstrap
{
    public static QaComposition Compose(QaEnvironment qa, QaCompositionContext production) => new(
        // 兜底开页：QA 只记录启动意图，不弹真实浏览器。
        qa.Mode == QaMode.PortalOpen ? QaPortal.RecordLaunch : production.BrowserLaunch,
        // 认证页宿主：auth-page QA 用假宿主；其他 QA 一律失败关闭，不创建真实浏览器或代理。
        qa.Mode switch
        {
            QaMode.AuthPage => new QaAuthPageHost(),
            _ when qa.Active => new UnavailableAuthPageBrowserHost(AuthPageBrowserRuntime.IsWebView2RuntimePresent),
            _ => production.AuthHost()
        },
        // 提权入口：概览/可访问性 QA 只记录意图，不弹 UAC。
        qa.Mode switch
        {
            QaMode.Overview => QaOverview.RecordLaunch,
            QaMode.Accessibility => QaAccessibility.RecordLaunch,
            _ => production.Launcher
        },
        // 设置页：QA 使用固定配置夹具；普通权限的生产路径只枚举 AllUsers 配置。
        qa.Active ? () => QaSettings.Profiles : production.LoadProfiles,
        qa.Mode == QaMode.Settings
            ? QaSettings.SaveForQaAsync
            : qa.Active
                ? (_, _) => Task.FromResult(new HandoffResult(HandoffOutcome.Rejected))
                : production.SaveSettings,
        // 日志页复制与目录打开：QA 替换为受控空操作，不触碰剪贴板或资源管理器。
        qa.Mode is QaMode.Logs or QaMode.Accessibility ? _ => Task.CompletedTask : production.CopyHandler,
        qa.Mode is QaMode.Logs or QaMode.Accessibility ? () => { } : production.FolderHandler,
        // 诊断包导出：export/accessibility QA 用受控夹具，不弹保存对话框或 UAC。
        qa.Mode is QaMode.ExportDiagnostics or QaMode.Accessibility ? QaExport.PickAsync : production.PickTarget,
        qa.Mode is QaMode.ExportDiagnostics or QaMode.Accessibility ? QaExport.LaunchAsync : production.ExportLaunch,
        // 内嵌认证通道：可访问性 QA 只记录启动意图，不启动辅助进程或真实气泡。
        qa.Mode == QaMode.Accessibility ? QaAuthChannel.RecordLaunch : production.AuthChannelStart,
        qa.Mode == QaMode.Accessibility ? null : production.AuthChannelPresent,
        qa.Mode == QaMode.Accessibility ? null : production.AuthChannelFallback,
        qa.Mode == QaMode.Accessibility ? QaAccessibility.RecordPortalOpen : production.PortalOpenManual,
        // notifications QA 用手工驱动并记录呈现结果，不弹真实气泡。
        qa.Mode == QaMode.Notifications ? [] : null);

    /// <summary>按模式启动对应场景；未激活 QA 时不做任何事。</summary>
    public static void Run(QaEnvironment qa, QaRunContext context)
    {
        Action<int> shutdown = code => context.Desktop.Shutdown(code);
        switch (qa.Mode)
        {
            case QaMode.Lifecycle:
            case QaMode.Hold:
                QaLifecycle.Run(context.Request, context.TrayHost, context.Window, qa);
                break;
            case QaMode.ThemeRender:
                QaThemeRender.Run(context.Window, qa, context.Desktop);
                break;
            case QaMode.Shell:
                QaShell.Run(context.Shell, context.Window, qa, shutdown);
                break;
            case QaMode.Overview:
                QaOverview.Run(context.Shell, context.Actions, context.Window, qa, shutdown);
                break;
            case QaMode.Diagnostics:
                QaDiagnostics.Run(context.Shell, context.Window, qa, shutdown);
                break;
            case QaMode.Logs:
                QaLogs.Run(context.Shell, context.LogsPage, context.Window, qa, shutdown);
                break;
            case QaMode.Settings:
                QaSettings.Run(context.Shell, context.Window, context.SettingsPage, qa, shutdown);
                break;
            case QaMode.ThemePrefs:
                QaThemePrefs.Run(context.Shell, context.Window, context.Theme, qa, shutdown);
                break;
            case QaMode.Notifications:
                QaNotifications.Run(context.Shell, context.Window, context.Notifications, context.Presented, qa, shutdown);
                break;
            case QaMode.PortalOpen:
                QaPortal.Run(context.Shell, context.Window, context.Pipe, context.PortalOpener, qa, shutdown);
                break;
            case QaMode.ExportDiagnostics:
                QaExport.Run(context.Shell, context.Window, context.DiagnosticsExporter, qa, shutdown);
                break;
            case QaMode.Accessibility:
                QaAccessibility.Run(context.Shell, context.Window, qa, shutdown);
                break;
            case QaMode.AuthPage:
                QaAuthPage.Run(context.Shell, context.AuthPage, context.Window, qa, shutdown);
                break;
        }
    }
}
