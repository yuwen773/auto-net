using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Styling;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 窗口边框外观：让系统标题栏跟随应用主题，深色模式下不再出现“白色标题栏 + 深色内容”的割裂。
/// 只调用 DWM 的属性设置，不改变窗口样式、不接管非客户区绘制；不支持该属性的系统上静默保持默认。
/// </summary>
internal static class WindowChrome
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE（Windows 10 20H1 起的正式属性号）。</summary>
    private const int UseImmersiveDarkMode = 20;

    /// <summary>Windows 10 1809–1909 时期该属性的旧编号，作为回退。</summary>
    private const int UseImmersiveDarkModeLegacy = 19;

    /// <summary>
    /// 让窗口标题栏跟随 <see cref="StyledElement.ActualThemeVariant"/>：创建时应用一次，
    /// 之后每次实际变体变化（手动切换或跟随系统改变）再应用一次。
    /// </summary>
    public static void Track(Window window)
    {
        void Apply()
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
            var dark = window.ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref dark, sizeof(int));
        }

        window.ActualThemeVariantChanged += (_, _) => Apply();
        window.Opened += (_, _) => Apply();
        // 句柄可能已存在（再次显示已创建的窗口）：立即尝试一次，失败时由 Opened 补上。
        Apply();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
