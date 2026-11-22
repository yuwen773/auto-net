using System.Runtime.InteropServices;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 系统动效偏好（与认证页覆盖层的 <c>prefers-reduced-motion</c> 同一语义）：
/// Windows 关闭“动画效果”时，壳只呈现终态，不播放页面淡入或状态点过渡。
/// </summary>
internal static class MotionSettings
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public static bool AnimationsEnabled => !ReducedMotionRequested();

    private static bool ReducedMotionRequested()
    {
        try
        {
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) && enabled == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // 非 Windows 或受限环境：按“允许动画”处理，不因读取失败改变界面。
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint winIni);
}
