using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 主题偏好 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=theme-prefs 激活，迁移工单 #9）：
/// 在真实壳窗口的设置页上驱动真实单选钮，逐场景记录「偏好 → 应用级变体 → 窗口实际变体 →
/// 系统变体 → 偏好文件全文」——默认跟随系统（故事 26）、手动明暗即选即存（故事 27）、
/// 系统变化不覆盖手动选择（故事 29）、跟随模式下响应系统变化（故事 29）。
/// 第二阶段（AUTONET_QA_THEME_PHASE=resume）在新进程启动时不做任何选择，直接记录
/// 启动时偏好，证明跨托盘重启保留（故事 28）。偏好文件重定向到 QA 证据目录，
/// 绝不读写本机真实用户偏好；截图全部来自真实窗口捕获。
/// </summary>
internal static class QaThemePrefs
{
    /// <summary>设置页主题单选钮的定位：组名与文案均来自 SettingsView 的单一来源。</summary>
    private static string Label(ThemePreference preference) => SettingsView.ThemeLabel(preference);

    public static void Run(ShellController controller, MainWindow window, ThemeController theme, QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var steps = new List<QaThemePrefsStep>();
            var phase = Environment.GetEnvironmentVariable("AUTONET_QA_THEME_PHASE") == "resume" ? "resume" : "select";
            var reportPath = phase == "resume" ? qa.ThemePrefsResumeReportPath : qa.ThemePrefsReportPath;

            QaThemePrefsReport Build(string? error = null) => new(
                qa.Mode.ToString(),
                phase,
                Environment.ProcessPath ?? "(unknown)",
                Environment.ProcessId,
                Elevation.IsAdministrator,
                Program.MutexName,
                Application.Current!.RequestedThemeVariant?.ToString() ?? "Default",
                steps,
                error);

            try
            {
                TrayHost.ShowWindow(window);
                await Task.Delay(300);
                await controller.SelectAsync(ShellPage.Settings);
                await controller.RefreshStatusAsync();
                await Task.Delay(250);

                if (phase == "resume")
                {
                    // 故事 28：新进程启动时不做任何选择——启动时的偏好即上次保存的偏好。
                    steps.Add(Describe("restart-preserved", theme, window, qa, "theme-resume"));
                }
                else
                {
                    // 故事 26：无偏好文件时默认跟随系统——窗口实际变体与操作系统一致。
                    steps.Add(Describe("default-system", theme, window, qa, "theme-default-system"));

                    // 故事 27：手动浅色——真实单选钮驱动，即选即存。
                    steps.Add(await SelectAsync("select-light", controller, window, theme, qa,
                        ThemePreference.Light, "theme-light"));
                    steps.Add(await SelectAsync("select-dark", controller, window, theme, qa,
                        ThemePreference.Dark, "theme-dark"));

                    // 故事 29（手动优先）：系统主题变化通知后，手动深色保持不变。
                    theme.OnSystemThemeChanged();
                    steps.Add(Describe("system-change-ignored", theme, window, qa, null));

                    // 跟随系统：回到 Default，窗口实际变体回到操作系统值。
                    steps.Add(await SelectAsync("select-system", controller, window, theme, qa,
                        ThemePreference.System, "theme-system"));

                    // 故事 29（跟随响应）：跟随模式下系统变化重新应用跟随语义。
                    theme.OnSystemThemeChanged();
                    steps.Add(Describe("system-change-follows", theme, window, qa, null));

                    // 收尾停在深色并落盘：resume 阶段据此验证跨重启保留。
                    steps.Add(await SelectAsync("final-dark", controller, window, theme, qa,
                        ThemePreference.Dark, null));
                }

                QaReports.Write(reportPath, Build());
                shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(reportPath, Build(e.GetType().Name + ": " + e.Message));
                shutdown(1);
            }
        });
    }

    /// <summary>
    /// 驱动设置页上的真实单选钮：程序化选中走生产的选中变化事件路径（与用户点击同一
    /// 处理器），并非物理鼠标点击；点击语义的视觉确认属人工项。
    /// </summary>
    private static async Task<QaThemePrefsStep> SelectAsync(
        string scenario, ShellController controller, MainWindow window, ThemeController theme,
        QaEnvironment qa, ThemePreference preference, string? shot)
    {
        var radio = window.GetVisualDescendants()
            .OfType<RadioButton>()
            .FirstOrDefault(candidate => candidate.GroupName == SettingsView.GroupName && candidate.Content as string == Label(preference))
            ?? throw new InvalidOperationException("设置页缺少主题单选钮：" + Label(preference));
        radio.IsChecked = true;
        await Task.Delay(200);
        if (radio.IsChecked != true)
            throw new InvalidOperationException("主题单选钮未被选中：" + Label(preference));
        return Describe(scenario, theme, window, qa, shot);
    }

    /// <summary>
    /// 一步证据：偏好、请求变体、窗口实际变体、系统变体与偏好文件全文（只含主题枚举）。
    /// 给出截图名（不含扩展名，与其他 QA 的 ShotPath 约定一致）时先做真实窗口捕获
    /// （PrintWindow；禁用离屏渲染做中文文本证据）。
    /// </summary>
    private static QaThemePrefsStep Describe(string scenario, ThemeController theme, MainWindow window, QaEnvironment qa, string? shot)
    {
        string? captured = null;
        if (shot is { } name)
        {
            WindowCapture.Save(window, qa.ThemePrefsShotPath(name));
            captured = name + ".png";
        }

        var prefsPath = Path.Combine(qa.Directory, ThemePreferenceStore.FileName);
        var content = File.Exists(prefsPath) ? File.ReadAllText(prefsPath) : "(absent)";
        return new QaThemePrefsStep(
            scenario,
            theme.Current.ToString(),
            Application.Current!.RequestedThemeVariant?.ToString() ?? "Default",
            window.ActualThemeVariant.ToString(),
            Application.Current!.PlatformSettings?.GetColorValues().ThemeVariant.ToString() ?? "unknown",
            ThemePreferenceStore.FileName,
            content,
            captured);
    }
}
