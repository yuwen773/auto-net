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
    private NativeMenuItem? unblockItem;
    private TrayIcon? trayIcon;

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
            else if (action == TrayMenuAction.UnblockAuthentication)
            {
                // 解除入口与概览按钮同源：可用性由可信状态与认证暂停事实共同决定，点击走同一条提权路径。
                unblockItem = item;
                item.Click += (_, _) => _ = RequestUnblockSafelyAsync();
            }
            else
            {
                item.Click += (_, _) => Handle(action);
            }

            menu.Add(item);
        }

        var tray = trayIcon = new TrayIcon
        {
            Icon = AppIcons.Load(),
            ToolTipText = "AutoNet · 校园网连接守护",
            IsVisible = true,
            Menu = menu
        };
        tray.Clicked += (_, _) => ShowMainWindow();
        icons = new TrayIcons { tray };
        TrayIcon.SetIcons(Application.Current!, icons);

        // 开关与解除条目随状态同步（状态事件来自读取线程，菜单更新必须回到界面线程）。
        statusHandler = reading => Dispatcher.UIThread.Post(() => SyncEntries(reading));
        shell.StatusChanged += statusHandler;
        SyncEntries(shell.Status);
    }

    private void Handle(TrayMenuAction action)
    {
        switch (action)
        {
            case TrayMenuAction.Open:
                ShowMainWindow();
                break;
            case TrayMenuAction.Logs:
                // 查看日志（#7）：与既有托盘同语义——打开界面并定位到日志页。
                ShowMainWindow();
                _ = SelectLogsSafelyAsync();
                break;
            case TrayMenuAction.Settings:
                Elevation.Launch(ElevatedOperationKind.Settings);
                break;
            case TrayMenuAction.Exit:
                RequestExit();
                break;
        }
    }

    private async Task SelectLogsSafelyAsync()
    {
        try { await shell.SelectAsync(ShellPage.Logs); }
        catch (Exception)
        {
            // 受控：菜单点击异常不逃逸到原生菜单回调；页面内容仍有降级路径。
        }
    }

    private void SyncEntries(StatusReading? reading)
    {
        if (recoveryItem is not null)
        {
            var state = OverviewModel.RecoverySwitch(reading);
            recoveryItem.Header = state.Label;
            recoveryItem.IsEnabled = state.Enabled;
            displayedIntent = state.Intent;
        }

        if (unblockItem is not null)
        {
            var entry = OverviewModel.UnblockEntry(reading);
            // 不可信时菜单项保留但禁用：既不谎报存在待解除阻断，也不给出可点的假入口。
            unblockItem.Header = entry.Visible ? entry.Label : OverviewModel.PlaceholderLabel;
            unblockItem.IsEnabled = entry.Enabled;
        }
    }

    private void HandleRecovery()
    {
        if (displayedIntent is not { } intent) return;
        _ = RequestRecoverySafelyAsync(intent);
    }

    private async Task RequestUnblockSafelyAsync()
    {
        // 点击前由 OverviewActions 重读并核对可信状态与暂停事实；此处只负责不让异常逃逸到原生菜单回调。
        try { await actions.RequestUnblockAsync(); }
        catch (Exception)
        {
            // 受控：菜单点击异常不逃逸，且失败关闭意味着什么都不发生。
        }
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

    /// <summary>托盘图标控件（#10：气泡呈现复用同一图标，避免第二个托盘图标）。</summary>
    public TrayIcon? TrayIcon => trayIcon;

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
