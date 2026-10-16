using Avalonia.Styling;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 主题偏好回归（迁移工单 #9 / T04 / 故事 26–29、70）：
/// 默认跟随系统、手动明暗的持久化与回读（跨重启语义）、损坏文件与未知值的降级、
/// 偏好文件逐字只含主题字段（不含秘密）、保存失败不抛出且会话内仍生效、
/// 偏好到 Avalonia 变体的映射、手动选择优先于系统变化、跟随模式响应系统变化。
/// 受控 UI 证据（真实单选钮、真实窗口变体与截图）由 scripts/qa-avalonia-theme.ps1 提供。
/// </summary>
internal static class ThemePreferenceRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("主题回归：缺失偏好文件时默认跟随系统", () => Sync(() =>
        {
            var directory = NewDirectory();
            try
            {
                var store = new ThemePreferenceStore(Path.Combine(directory, "ui-preferences.json"));
                Require(store.Load() == ThemePreference.System, "缺失文件未回退为跟随系统");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("主题回归：手动浅色与深色跨实例回读（跨重启语义）", () => Sync(() =>
        {
            var path = NewPath();
            var first = new ThemePreferenceStore(path);
            Require(first.TrySave(ThemePreference.Light), "浅色保存失败");
            Require(new ThemePreferenceStore(path).Load() == ThemePreference.Light, "浅色未跨实例回读");
            Require(first.TrySave(ThemePreference.Dark), "深色保存失败");
            Require(new ThemePreferenceStore(path).Load() == ThemePreference.Dark, "深色未跨实例回读");
        })),
        ("主题回归：损坏文件与未知值回退为跟随系统", () => Sync(() =>
        {
            foreach (var payload in new[] { "not-json", "", "{\"theme\":\"sepia\"}", "{\"theme\":null}", "[]" })
            {
                var path = NewPath();
                File.WriteAllText(path, payload);
                Require(new ThemePreferenceStore(path).Load() == ThemePreference.System,
                    "损坏载荷未回退为跟随系统：" + payload);
            }
        })),
        ("主题回归：偏好文件逐字只含主题字段（不含秘密）", () => Sync(() =>
        {
            var path = NewPath();
            var store = new ThemePreferenceStore(path);
            Require(store.TrySave(ThemePreference.Dark), "深色保存失败");
            Require(File.ReadAllText(path) == "{\"theme\":\"dark\"}", "偏好文件含主题以外的内容：" + File.ReadAllText(path));
            Require(store.TrySave(ThemePreference.System), "跟随系统保存失败");
            Require(File.ReadAllText(path) == "{\"theme\":\"system\"}", "偏好文件含主题以外的内容：" + File.ReadAllText(path));
        })),
        ("主题回归：保存失败返回 false 不抛出且读取回退跟随系统", () => Sync(() =>
        {
            // 以“文件充当目录”的路径注入写入失败：CreateDirectory 必然失败，绝不触碰真实用户目录。
            var blocker = NewPath();
            File.WriteAllText(blocker, "file-as-directory");
            var store = new ThemePreferenceStore(Path.Combine(blocker, "ui-preferences.json"));
            Require(!store.TrySave(ThemePreference.Dark), "坏路径上的保存不应返回 true");
            Require(store.Load() == ThemePreference.System, "坏路径上的读取未回退为跟随系统");
        })),
        ("主题回归：偏好到 Avalonia 变体的映射", () => Sync(() =>
        {
            Require(ThemeController.Resolve(ThemePreference.System) == ThemeVariant.Default, "跟随系统未映射为 Default");
            Require(ThemeController.Resolve(ThemePreference.Light) == ThemeVariant.Light, "浅色未映射为 Light");
            Require(ThemeController.Resolve(ThemePreference.Dark) == ThemeVariant.Dark, "深色未映射为 Dark");
        })),
        ("主题回归：跟随模式下响应系统主题变化", () => Sync(() =>
        {
            var applied = new List<ThemeVariant>();
            var controller = new ThemeController(new ThemePreferenceStore(NewPath()), applied.Add);
            controller.Apply(ThemePreference.System);
            controller.OnSystemThemeChanged();
            Require(applied.Count == 2 && applied[^1] == ThemeVariant.Default,
                "跟随模式未在系统变化时重新应用跟随语义：" + string.Join(",", applied));
        })),
        ("主题回归：手动主题不被系统主题变化覆盖", () => Sync(() =>
        {
            var applied = new List<ThemeVariant>();
            var controller = new ThemeController(new ThemePreferenceStore(NewPath()), applied.Add);
            controller.Apply(ThemePreference.Dark);
            applied.Clear();
            controller.OnSystemThemeChanged();
            Require(applied.Count == 0, "系统变化覆盖了手动深色");
            Require(controller.Current == ThemePreference.Dark, "系统变化改写了手动偏好");
        })),
        ("主题回归：选择入口立即应用并持久化，保存失败仍生效", () => Sync(() =>
        {
            var path = NewPath();
            var applied = new List<ThemeVariant>();
            var controller = new ThemeController(new ThemePreferenceStore(path), applied.Add);
            Require(controller.Select(ThemePreference.Light), "浅色选择未持久化");
            Require(applied.Count == 1 && applied[0] == ThemeVariant.Light, "浅色未立即应用：" + string.Join(",", applied));
            Require(controller.Current == ThemePreference.Light, "选择后偏好未更新");
            Require(new ThemePreferenceStore(path).Load() == ThemePreference.Light, "持久化内容与选择不符");

            // 存储失败：会话内仍然立即应用（主题偏好不值得打断界面），但如实返回 false。
            var blocker = NewPath();
            File.WriteAllText(blocker, "file-as-directory");
            var failing = new ThemeController(new ThemePreferenceStore(Path.Combine(blocker, "ui-preferences.json")), applied.Add);
            applied.Clear();
            Require(!failing.Select(ThemePreference.Dark), "坏路径上的选择应如实返回 false");
            Require(applied.Count == 1 && applied[0] == ThemeVariant.Dark, "保存失败时未在会话内应用深色");
        })),
        ("主题回归：控制器初始为跟随系统且未动变体，QA 偏好重定向到证据目录", () => Sync(() =>
        {
            var applied = new List<ThemeVariant>();
            var controller = new ThemeController(new ThemePreferenceStore(NewPath()), applied.Add);
            Require(controller.Current == ThemePreference.System, "初始偏好不是跟随系统");
            Require(applied.Count == 0, "构造时不应改动应用级变体");

            var evidence = NewDirectory();
            try
            {
                var redirected = ThemePreferenceStore.Create(new QaEnvironment(QaMode.ThemePrefs, evidence));
                Require(redirected.Load() == ThemePreference.System, "QA 重定向存储读取失败");
                Require(redirected.TrySave(ThemePreference.Dark) && File.ReadAllText(Path.Combine(evidence, "ui-preferences.json")) == "{\"theme\":\"dark\"}",
                    "QA 偏好未落在证据目录内");
            }
            finally { Directory.Delete(evidence, recursive: true); }
            Require(ThemePreferenceStore.Create(QaEnvironment.Inactive).Load() == ThemePreference.System,
                "非 QA 存储读取应安全回退");
            Require(ThemePreferenceStore.DefaultPath().EndsWith("AutoNet\\ui-preferences.json", StringComparison.Ordinal),
                "真实用户偏好路径不在按用户隔离的 AutoNet 目录下");
        })),
    ];

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "autonet-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string NewPath() => Path.Combine(NewDirectory(), "ui-preferences.json");

    private static Task Sync(Action action) { action(); return Task.CompletedTask; }

    private static void Require(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
}
