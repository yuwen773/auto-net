using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 托盘气泡呈现（#10 / 故事 52）：复用既有 Avalonia 托盘图标的平台窗口句柄
/// （Avalonia.Win32.Win32Platform.Instance 的 Handle）与图标标识
/// （Avalonia.Win32.TrayIconImpl 的 _uniqueId），以 Shell_NotifyIconW
/// （NIM_MODIFY + NIF_INFO）在同一图标上显示系统通知——不创建第二个托盘图标，
/// 不新增窗口、服务或运行时依赖。Shell_NotifyIcon 是线程安全的 Shell API，可在
/// 状态读取线程上调用；回调消息由 Avalonia 平台窗口的既有 WndProc 承接。
/// 图标未就绪、内部结构变化或 Shell 调用失败一律受控返回 false（呈现层绝不抛出；
/// 路由层对 false 不落消费记录，同一事件下轮重试）。措辞由
/// <see cref="NotificationRouter"/> 的受控词表提供，本层不携带文案。
///
/// 已知脆弱点：Avalonia 平台内部成员（TrayIcon.Impl / TrayIconImpl._uniqueId /
/// Win32Platform.Instance.Handle）经反射解析，升级 Avalonia 后若改名会静默降级为
/// 「不提醒」（安全侧，不产生错误动作）；升级后必须复跑 scripts/qa-avalonia-notifications.ps1
/// 与真实气泡人工确认。选择反射而非硬引用的原因：该成员在 12.1.4 中为 internal，
/// 硬引用需引入 InternalsVisibleTo 或复制平台代码，权衡后反射的失败模式最可控。
/// </summary>
internal sealed class TrayBalloonNotifier
{
    private const uint NIM_MODIFY = 0x1;
    private const uint NIF_INFO = 0x10;
    private const uint NIIF_NONE = 0x0;
    private const uint NIIF_WARNING = 0x2;

    private readonly TrayIcon icon;

    public TrayBalloonNotifier(TrayIcon icon) => this.icon = icon;

    public bool Show(NotificationAlert alert)
    {
        try { return TryShow(alert); }
        catch (Exception)
        {
            // 受控：呈现失败的降级是“没有提醒”，绝不让异常逃逸到状态管道。
            return false;
        }
    }

    private bool TryShow(NotificationAlert alert)
    {
        var impl = icon.GetType()
            .GetProperty("Impl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(icon);
        if (impl is null || !icon.IsVisible) return false;
        var id = impl.GetType().GetField("_uniqueId", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(impl);
        if (id is not int uniqueId) return false;
        // 平台窗口句柄来自 Avalonia.Win32 的 Win32Platform.Instance.Handle（公开静态单例）；
        // 经 impl 所在程序集解析，避免对本工程不可直接引用的平台程序集产生编译期耦合。
        var platform = impl.GetType().Assembly.GetType("Avalonia.Win32.Win32Platform")?
            .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (platform is null) return false;
        var handle = platform.GetType().GetProperty("Handle", BindingFlags.Public | BindingFlags.Instance)?.GetValue(platform);
        if (handle is not nint hWnd) return false;
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = hWnd,
            uID = (uint)uniqueId,
            uFlags = NIF_INFO,
            szInfo = alert.Body,
            szInfoTitle = alert.Heading,
            dwInfoFlags = alert.Warning ? NIIF_WARNING : NIIF_NONE
        };
        return Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Shell NOTIFYICONDATAW（V4 形状）：完整声明字段以保持 Win32 ABI 偏移；
    /// 本层只使用 balloon 相关字段（szInfo / szInfoTitle / dwInfoFlags）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
}
