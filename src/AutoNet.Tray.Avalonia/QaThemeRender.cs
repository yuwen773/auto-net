using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 明暗主题渲染 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=theme 激活）。
/// 启动真实窗口并依次应用浅色、深色，再用真实窗口捕获保存 PNG。
/// 窗口会在这几秒内真实显示一次——这是有意的：证据必须来自真实渲染而不是离屏渲染。
/// </summary>
internal static class QaThemeRender
{
    public static void Run(MainWindow window, QaEnvironment qa, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                TrayHost.ShowWindow(window);
                foreach (var (variant, path) in new[]
                {
                    (ThemeVariant.Light, qa.ThemeLightPath),
                    (ThemeVariant.Dark, qa.ThemeDarkPath)
                })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    await Task.Delay(500);
                    WindowCapture.Save(window, path);
                }

                lifetime.Shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(Path.Combine(qa.Directory, "theme-error.json"),
                    new { phase = "error", error = e.GetType().Name + ": " + e.Message });
                lifetime.Shutdown(1);
            }
        });
    }
}
