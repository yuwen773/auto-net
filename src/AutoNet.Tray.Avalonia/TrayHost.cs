using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 托盘图标与菜单。菜单条目取自 <see cref="TrayMenuModel"/>（既有入口语义）：
/// 打开、设置认证信息…（独立提权入口）、自动恢复开关（随状态同步、发送明确意图）、
/// 退出托盘（后台继续运行）。
/// </summary>
internal sealed class TrayHost : IDisposable
{
    private readonly TrayIcons icons;
    private readonly MainWindow window;
    private readonly TraySession session;
    private readonly OverviewActions actions;
    private readonly Action shutdown;
    private readonly ShellController shell;
    private readonly Action<StatusReading> statusHandler;
    private NativeMenuItem? recoveryItem;

    /// <summary>最近一次同步到菜单上的开关意图；点击时按用户看到的那一个发送，绝不重新翻转。</summary>
    private RecoveryIntent? displayedIntent;

    public TrayHost(TraySession session, MainWindow window, ShellController shell, OverviewActions actions, Action shutdown)
    {
        this.session = session;
        this.window = window;
        this.shell = shell;
        this.actions = actions;
        this.shutdown = shutdown;
        var menu = new NativeMenu();
        foreach (var entry in TrayMenuModel.Entries)
        {
            if (entry.Action is not { } action)
            {
                menu.Add(new NativeMenuItemSeparator());
                continue;
            }

            var item = new NativeMenuItem(entry.Label);
            if (action == TrayMenuAction.RecoverySwitch)
            {
                recoveryItem = item;
                item.Click += (_, _) => HandleRecovery();
            }
            else
            {
                item.Click += (_, _) => Handle(action);
            }

            menu.Add(item);
        }

        var tray = new TrayIcon
        {
            Icon = AppIcons.Load(),
            ToolTipText = "AutoNet · 校园网连接守护",
            IsVisible = true,
            Menu = menu
        };
        tray.Clicked += (_, _) => ShowMainWindow();
        icons = new TrayIcons { tray };
        TrayIcon.SetIcons(Application.Current!, icons);

        // 开关条目随状态同步（状态事件来自读取线程，菜单更新必须回到界面线程）。
        statusHandler = reading => Dispatcher.UIThread.Post(() => SyncRecovery(reading));
        shell.StatusChanged += statusHandler;
        SyncRecovery(shell.Status);
    }

    private void Handle(TrayMenuAction action)
    {
        switch (action)
        {
            case TrayMenuAction.Open:
                ShowMainWindow();
                break;
            case TrayMenuAction.Settings:
                Elevation.Launch(ElevatedOperationKind.Settings);
                break;
            case TrayMenuAction.Exit:
                RequestExit();
                break;
        }
    }

    private void SyncRecovery(StatusReading? reading)
    {
        if (recoveryItem is null) return;
        var state = OverviewModel.RecoverySwitch(reading);
        recoveryItem.Header = state.Label;
        recoveryItem.IsEnabled = state.Enabled;
        displayedIntent = state.Intent;
    }

    private void HandleRecovery()
    {
        if (displayedIntent is not { } intent) return;
        _ = RequestRecoverySafelyAsync(intent);
    }

    private async Task RequestRecoverySafelyAsync(RecoveryIntent intent)
    {
        try { await actions.RequestRecoveryChangeAsync(intent); }
        catch (Exception)
        {
            // 受控：菜单点击异常不逃逸到原生菜单回调。
        }
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
        shell.StatusChanged -= statusHandler;
        foreach (var icon in icons) icon.IsVisible = false;
        TrayIcon.SetIcons(Application.Current!, null);
        icons.Clear();
    }
}
