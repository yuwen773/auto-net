using Avalonia;
using Avalonia.Styling;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 主题应用与选择（迁移工单 #9 / 故事 26–29）：
/// - 默认跟随系统：偏好为 System 时保持 <c>ThemeVariant.Default</c>，实际明暗随 Windows 外观；
/// - 手动浅/深：显式设置变体，跨托盘重启由 <see cref="ThemePreferenceStore"/> 保留；
/// - 手动优先于系统变化：系统主题变化只重申跟随语义，绝不覆盖手动选择；
/// - 一致性：托盘、提权操作与提示窗口共用同一个 Application 变体（故事 30）。
/// 变体写入器可注入以便受控测试；生产路径写入 <c>Application.Current.RequestedThemeVariant</c>，
/// 各页面取色全部经主题资源键绑定，变体切换无需重建控件树。
/// </summary>
internal sealed class ThemeController
{
    private readonly ThemePreferenceStore store;
    private readonly Action<ThemeVariant> setVariant;

    public ThemeController(ThemePreferenceStore store, Action<ThemeVariant>? setVariant = null)
    {
        this.store = store;
        this.setVariant = setVariant ?? DefaultSetter;
    }

    /// <summary>当前偏好；初始即为跟随系统（故事 26），是否已应用取决于是否调用过 Apply。</summary>
    public ThemePreference Current { get; private set; } = ThemePreference.System;

    /// <summary>启动装载：读取偏好并立即应用（任何窗口显示之前调用，故事 30 的一致性前提）。</summary>
    public static ThemeController LoadAndApply(QaEnvironment qa)
    {
        var controller = new ThemeController(ThemePreferenceStore.Create(qa));
        controller.Apply(controller.store.Load());
        return controller;
    }

    /// <summary>应用偏好到应用级变体（不持久化；持久化走 <see cref="Select"/>）。</summary>
    public void Apply(ThemePreference preference)
    {
        Current = preference;
        setVariant(Resolve(preference));
    }

    /// <summary>
    /// 设置页选择入口：立即应用并持久化（故事 27、28）。保存失败不影响本次会话的
    /// 应用结果——主题偏好不值得打断界面，也不向用户回显异常文本。
    /// </summary>
    public bool Select(ThemePreference preference)
    {
        Apply(preference);
        return store.TrySave(preference);
    }

    /// <summary>
    /// 系统主题变化通知（故事 29）：只在跟随模式下重新应用跟随语义；手动选择保持不变——
    /// 显式变体本就不会被框架改写，这里同时给出可受控测试的接缝（QA 以此记录优先级证据）。
    /// </summary>
    public void OnSystemThemeChanged()
    {
        if (Current == ThemePreference.System) Apply(ThemePreference.System);
    }

    /// <summary>偏好到 Avalonia 变体的映射：跟随系统 = Default（由框架按 Windows 外观解析）。</summary>
    internal static ThemeVariant Resolve(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };

    private static void DefaultSetter(ThemeVariant variant)
    {
        if (Application.Current is { } application) application.RequestedThemeVariant = variant;
    }
}
