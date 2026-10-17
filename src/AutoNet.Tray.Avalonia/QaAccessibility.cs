using System.Text.Json;
using AutoNet.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 可访问性与缩放 QA（仅 AUTONET_QA_DIR / AUTONET_QA_MODE=accessibility 激活，迁移工单 #14 / T05+T06）：
/// - 键盘（故事 32）：真实窗口内注入 Tab / 方向键 / Enter 键事件，核验焦点移动、页面切换与
///   主要操作触发（恢复开关意图经记录器落证据，不弹 UAC）；
/// - 可访问名称（故事 33）：夹具状态切换后核验恢复开关等关键控件名称随状态动态更新；
/// - 受控缩放（T05 / 故事 31）：100%/125%/150% 经 LayoutTransformControl 在真实窗口内渲染并截图，
///   程序化核验关键控件「视口内完整可见，或位于可滚动区域可滚动到达」；
///   该证据仅覆盖受控缩放，真实 Windows DPI 与系统键盘交互另行分列验收（显式待验收，不补为通过）。
/// 本模式不订阅自动开页（夹具只驱动投影显示），剪贴板、目录打开与导出对话框均替换为受控记录。
/// </summary>
internal static class QaAccessibility
{
    private static readonly object launchGate = new();
    private static readonly List<string> launches = [];

    /// <summary>可访问性 QA 的启动记录适配器：只记录命令意图，不弹出 UAC（与 QaOverview.RecordLaunch 同约定）。</summary>
    public static void RecordLaunch(ElevatedOperationKind kind)
    {
        lock (launchGate) launches.Add(kind.CommandLineArgument());
    }

    /// <summary>手动开页记录适配器：只记录，不启动浏览器。</summary>
    public static void RecordPortalOpen()
    {
        lock (launchGate) launches.Add("manual-portal-open");
    }

    public static void Run(ShellController controller, MainWindow window, QaEnvironment qa, Action<int> shutdown)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var tabSequences = new List<QaPageTabStops>();
            var names = new List<QaAccessibilityNames>();
            var keyboard = new List<QaAccessibilityKeyboard>();
            var scaling = new List<QaAccessibilityScale>();
            var fixturePath = Environment.GetEnvironmentVariable("AUTONET_QA_STATUS_PATH");
            var fixtureMode = fixturePath is { Length: > 0 };
            lock (launchGate) launches.Clear();

            QaAccessibilityReport Build(string? error = null) => new(
                fixtureMode ? "fixture" : "live",
                fixtureMode,
                Environment.ProcessPath ?? "(unknown)",
                Environment.ProcessId,
                Elevation.IsAdministrator,
                Program.MutexName,
                Application.Current!.RequestedThemeVariant?.ToString() ?? "Default",
                tabSequences,
                names,
                keyboard,
                scaling,
                "待验收：真实 Windows DPI 与系统键盘/辅助技术交互另行分列验收；本报告为受控缩放与受控键盘注入证据，不以模拟值补为通过",
                "受控缩放 = 真实窗口 LayoutTransformControl(100/125/150%) + PrintWindow 捕获；可达性判定 = 视口内完整可见，或位于可滚动区域内可滚动到达",
                error);

            try
            {
                if (!fixtureMode) throw new InvalidOperationException("可访问性 QA 只在夹具模式运行（需要 AUTONET_QA_STATUS_PATH）。");
                if (!Path.GetFullPath(fixturePath!).StartsWith(qa.Directory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("AUTONET_QA_STATUS_PATH 必须位于 QA 证据目录内。");

                TrayHost.ShowWindow(window);
                await Task.Delay(300);

                // ── 可访问名称随状态更新（故事 33）─────────────────────────────────
                WriteStatus(fixturePath!, Online());
                await SettleAsync(controller);
                var (onName, _) = RequireRecoveryButton(window);
                Require(onName.Contains("已开启") && onName.Contains("关闭自动恢复"), "已开启状态的可访问名称不符：" + onName);

                WriteStatus(fixturePath!, Online() with { Enabled = false });
                await SettleAsync(controller);
                var (offName, _) = RequireRecoveryButton(window);
                Require(offName.Contains("已关闭") && offName.Contains("开启自动恢复"), "已关闭状态的可访问名称不符：" + offName);
                Require(onName != offName, "恢复开关可访问名称未随状态更新");

                WriteStatus(fixturePath!, Online() with
                {
                    CredentialsBlocked = true,
                    AuthenticationFailure = "CredentialsRejected",
                    AuthenticationPersistentState = "CredentialsRejected"
                });
                await SettleAsync(controller);
                var unblockName = FindButtonName(window, name => name.StartsWith("认证状态（", StringComparison.Ordinal));
                Require(unblockName is { Length: > 0 } && unblockName.Contains("认证提交已暂停"), "解除入口名称未携带当前认证事实：" + (unblockName ?? "(缺失)"));

                // 解除入口的键盘触发（故事 32）：Enter 记录 --unblock-authentication 意图，不弹 UAC。
                var unblockButton = VisibleDescendants(window).OfType<Button>()
                    .FirstOrDefault(button => (AutomationProperties.GetName(button) ?? "").StartsWith("认证状态（", StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("未找到解除认证暂停按钮");
                var unblockFocus = unblockButton.Focus(NavigationMethod.Tab, KeyModifiers.None);
                Require(unblockFocus, "解除入口未能获得键盘焦点");
                var unblockBefore = LaunchCount();
                PressKey(unblockButton, Key.Enter);
                await Task.Delay(250);
                Require(LaunchCount() == unblockBefore + 1, "Enter 未触发解除入口动作");
                string unblockCommand;
                lock (launchGate) unblockCommand = launches[^1];
                Require(unblockCommand == ElevatedOperationKind.UnblockAuthentication.CommandLineArgument(),
                    $"解除入口键盘意图不符：{unblockCommand}");
                keyboard.Add(new QaAccessibilityKeyboard("解除入口（" + unblockName + "）", "Enter", unblockFocus, true, unblockCommand));

                WriteStatus(fixturePath!, Online() with
                {
                    BrowserFallbackRequested = true,
                    BrowserFallbackNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1)
                });
                await SettleAsync(controller);
                var portalName = FindButtonName(window, name => name.StartsWith("需要浏览器认证", StringComparison.Ordinal));
                Require(portalName is { Length: > 0 } && portalName.Contains("打开门户页面"), "兜底开页入口名称不符：" + (portalName ?? "(缺失)"));

                names.Add(new QaAccessibilityNames("恢复开关·已开启", onName, null, null, null));
                names.Add(new QaAccessibilityNames("恢复开关·已关闭", offName, null, null, null));
                names.Add(new QaAccessibilityNames("解除入口·认证暂停", RecoveryButtonName(window), unblockName, null, null));
                names.Add(new QaAccessibilityNames("兜底开页·可见", RecoveryButtonName(window), null, portalName, null));

                // ── 键盘（故事 32）────────────────────────────────────────────────
                // Enter 触发主要操作：恢复开关按钮持有键盘焦点时按 Enter，意图经记录器落证据。
                WriteStatus(fixturePath!, Online());
                await SettleAsync(controller);
                var (recoveryName, recoveryButton) = RequireRecoveryButton(window);
                var focusSet = recoveryButton.Focus(NavigationMethod.Tab, KeyModifiers.None);
                Require(focusSet, "恢复开关按钮未能获得键盘焦点");
                var before = LaunchCount();
                PressKey(recoveryButton, Key.Enter);
                await Task.Delay(250);
                Require(LaunchCount() == before + 1, "Enter 未触发恢复开关动作");
                string command;
                lock (launchGate) command = launches[^1];
                var expected = OverviewModel.OperationFor(RecoveryIntent.Disable).CommandLineArgument();
                Require(command == expected, $"Enter 触发的意图不符：期望 {expected}，实际 {command}");
                keyboard.Add(new QaAccessibilityKeyboard("恢复开关（" + recoveryName + "）", "Enter", focusSet, true, command));

                // Tab 遍历（故事 32 键盘顺序）：从恢复开关连按 Tab 记录真实焦点轨迹（受控注入走
                // 真实键盘导航处理器），直到绕环回到出发点；轨迹长度覆盖该页全部键盘停留点。
                var tabTrail = new List<string> { Describe(recoveryButton) };
                var walker = recoveryButton as Control;
                for (var hop = 0; hop < 24; hop++)
                {
                    PressKey(walker!, Key.Tab);
                    await Task.Delay(80);
                    if (CurrentFocus(window) is not { } stepped) break;
                    tabTrail.Add(Describe(stepped));
                    if (ReferenceEquals(stepped, recoveryButton)) break;
                    walker = stepped;
                }
                Require(tabTrail.Count > 1 && tabTrail[^1] == Describe(recoveryButton),
                    "Tab 遍历未从恢复开关绕环回到出发点：" + string.Join(" → ", tabTrail));
                keyboard.Add(new QaAccessibilityKeyboard("Tab 遍历（概览页，绕环一周）", "Tab ×" + (tabTrail.Count - 1), true, true,
                    string.Join(" → ", tabTrail)));

                // 方向键导航页面：左侧导航第一项按 ↓ 切换到网络诊断。
                await controller.SelectAsync(ShellPage.Overview);
                await Task.Delay(150);
                var firstNav = VisibleDescendants(window).OfType<ListBoxItem>()
                    .FirstOrDefault(item => item.Tag is ShellPage page && page == ShellPage.Overview)
                    ?? throw new InvalidOperationException("未找到概览导航项");
                focusSet = firstNav.Focus(NavigationMethod.Tab, KeyModifiers.None);
                Require(focusSet, "导航项未能获得键盘焦点");
                PressKey(firstNav, Key.Down);
                await Task.Delay(400);
                Require(controller.SelectedPage == ShellPage.Diagnostics, "方向键未切换到网络诊断页");
                keyboard.Add(new QaAccessibilityKeyboard("左侧导航（概览 → 网络诊断）", "Down", focusSet, true,
                    controller.SelectedPage.ToString()));

                // 诊断页导出入口名称（非运行中状态；#12 与 #33 的交叉证据）。
                await Task.Delay(150);
                var exportName = FindButtonName(window, name => name.StartsWith("导出诊断包", StringComparison.Ordinal));
                Require(exportName == "导出诊断包…", "诊断页导出入口名称不符：" + (exportName ?? "(缺失)"));
                names.Add(new QaAccessibilityNames("导出入口·诊断页", "(概览页无)", null, null, exportName));

                // ── 每页：Tab 停留点序列 + 受控缩放（T05）──────────────────────────
                var scales = new (string Label, double Factor)[] { ("100", 1.0), ("125", 1.25), ("150", 1.5) };
                foreach (var info in ShellPages.All)
                {
                    await controller.SelectAsync(info.Page);
                    await Task.Delay(250);
                    tabSequences.Add(new QaPageTabStops(info.Page.ToString(), TabStops(window)));
                    Require(tabSequences[^1].Stops.Count > 0, info.Name + "页没有可键盘到达的控件");

                    foreach (var (label, factor) in scales)
                    {
                        window.viewScale.LayoutTransform = new ScaleTransform(factor, factor);
                        await Task.Delay(200);
                        var clipped = ClippedControls(window);
                        var shot = qa.AccessibilityShotPath("scale-" + info.Page.ToString().ToLowerInvariant() + "-" + label);
                        WindowCapture.Save(window, shot);
                        scaling.Add(new QaAccessibilityScale(
                            info.Page.ToString(), label, clipped.Count == 0, clipped, Path.GetFileName(shot)));
                        Require(clipped.Count == 0,
                            $"{info.Name} 页 {label}% 受控缩放存在不可达或被裁切的关键控件：{string.Join("；", clipped)}");
                    }

                    window.viewScale.LayoutTransform = null;
                    await Task.Delay(120);
                }

                QaReports.Write(qa.AccessibilityReportPath, Build());
                shutdown(0);
            }
            catch (Exception e)
            {
                QaReports.Write(qa.AccessibilityReportPath, Build(e.GetType().Name + ": " + e.Message));
                shutdown(1);
            }
        });
    }

    /// <summary>
    /// 关键控件可达性核验（T05 / 故事 31）。左侧导航项与底部状态栏没有可滚动容器兜底，
    /// 任何缩放档位都必须完整位于窗口客户区内（硬性清单）；其余用户可交互控件
    /// （类型白名单，含禁用态——禁用按钮仍需视觉可达）必须完整在视口内，
    /// 或位于可滚动区域内可滚动到达（内容溢出产生的滚动条不算关键裁切）。
    /// </summary>
    private static List<string> ClippedControls(MainWindow window)
    {
        var clipped = new List<string>();
        var viewport = new Rect(window.ClientSize);
        foreach (var control in VisibleDescendants(window).Where(control =>
                     IsOperableType(control) || IsNavEntry(control) || ReferenceEquals(control, window.view.statusText)))
        {
            var strict = IsNavEntry(control) || ReferenceEquals(control, window.view.statusText);
            var bounds = control.GetTransformedBounds();
            if (bounds is not { } transformed || transformed.Bounds.Width <= 0 || transformed.Bounds.Height <= 0)
            {
                clipped.Add(Describe(control) + "（变换后尺寸无效）");
                continue;
            }

            var rect = transformed.Bounds;
            if (viewport.Contains(rect)) continue;
            if (!strict)
            {
                var scrollable = control.GetVisualAncestors().OfType<ScrollViewer>()
                    .Any(s => s.Extent.Height > s.Viewport.Height + 0.5 || s.Extent.Width > s.Viewport.Width + 0.5);
                if (scrollable) continue;
            }

            clipped.Add($"{Describe(control)}（边界 {Math.Round(rect.X)},{Math.Round(rect.Y)} {Math.Round(rect.Width)}×{Math.Round(rect.Height)} 超出视口 {Math.Round(viewport.Width)}×{Math.Round(viewport.Height)}）");
        }

        return clipped;
    }

    /// <summary>左侧导航项：Tag 承载页面标识。</summary>
    private static bool IsNavEntry(Control control) => control is ListBoxItem { Tag: ShellPage };

    private static List<QaAccessibilityTabStop> TabStops(MainWindow window) =>
        VisibleDescendants(window)
            .Where(control => control.IsEnabled && IsOperableType(control))
            .Select(c => new QaAccessibilityTabStop(c.GetType().Name, Describe(c)))
            .ToList();

    /// <summary>
    /// 用户可交互的控件类型白名单（键盘停留点 / 缩放可达性检查对象）。用类型白名单而不是
    /// IsTabStop：布局容器与模板基础设施不是停留点，它们的边界不代表「按钮与状态可达」；
    /// 滚动条内部按钮（翻页/箭头）同理排除——键盘滚动天然可用，零尺寸不构成关键裁切。
    /// </summary>
    private static bool IsOperableType(Control control) =>
        !control.GetVisualAncestors().OfType<ScrollBar>().Any() && control is
            Button or RadioButton or ToggleButton or CheckBox or ComboBox or ComboBoxItem or
            ListBoxItem or ListBox or TextBox or Expander or Slider or CalendarDatePicker or
            NumericUpDown or AutoCompleteBox;

    /// <summary>控件描述：优先可访问名称，其次控件文本，最后类型名。</summary>
    private static string Describe(Control control)
    {
        var name = AutomationProperties.GetName(control);
        if (!string.IsNullOrEmpty(name)) return name;
        return control switch
        {
            RadioButton radio => radio.Content?.ToString() ?? radio.GetType().Name,
            Button button => button.Content?.ToString() ?? button.GetType().Name,
            Expander expander => expander.Header?.ToString() ?? expander.GetType().Name,
            TextBox => "(文本输入)",
            _ => control.GetType().Name
        };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static (string Name, Button Button) RequireRecoveryButton(MainWindow window) =>
        VisibleDescendants(window).OfType<Button>()
            .Select(button => (Name: AutomationProperties.GetName(button), Button: button))
            .FirstOrDefault(pair => (pair.Name ?? "").StartsWith("自动恢复（", StringComparison.Ordinal)) is { Name: { Length: > 0 } found, Button: { } foundButton }
            ? (found, foundButton)
            : throw new InvalidOperationException("未找到恢复开关按钮（可访问名称应以「自动恢复（」开头）");

    private static string RecoveryButtonName(MainWindow window) => RequireRecoveryButton(window).Name;

    private static string? FindButtonName(MainWindow window, Func<string, bool> matches) =>
        VisibleDescendants(window).OfType<Button>()
            .Select(button => AutomationProperties.GetName(button))
            .FirstOrDefault(name => name is { Length: > 0 } && matches(name));

    private static Control? CurrentFocus(MainWindow window) => window.FocusManager?.GetFocusedElement() as Control;

    private static void PressKey(Control target, Key key)
    {
        // 受控键注入：从持有焦点的控件冒泡，走与真实键盘相同的路由（按钮 Enter 触发、
        // Tab 由顶层键盘导航处理器处理、ListBox 方向键换选），只省去 OS 输入队列一跳。
        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.None,
            Source = target
        });
    }

    /// <summary>先序遍历可见视觉子树；折叠分支整支跳过，与用户实际所见一致。</summary>
    private static IEnumerable<Control> VisibleDescendants(Control root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is not Control control || !control.IsVisible) continue;
            yield return control;
            foreach (var descendant in VisibleDescendants(control)) yield return descendant;
        }
    }

    private static int LaunchCount()
    {
        lock (launchGate) return launches.Count;
    }

    private static async Task SettleAsync(ShellController controller)
    {
        await controller.RefreshStatusAsync();
        await Task.Delay(150);
    }

    private static void WriteStatus(string path, StatusSnapshot snapshot) =>
        Storage.AtomicWrite(path, JsonSerializer.Serialize(snapshot, Storage.Json));

    private static StatusSnapshot Online() => new()
    {
        Enabled = true,
        State = "校园网在线",
        Direct = "外网可达",
        Wifi = "已连接",
        Portal = "未查询（外网检查已通过）",
        Tunnel = "系统路径可达，出口未核验",
        Uu = "未核验"
    };
}
