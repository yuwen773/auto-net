using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 管理员恢复开关操作（#4）：按调用方给出的明确目标改写已保存设置的自动恢复开关。
/// 与既有 WinForms 托盘同语义——读取已保存设置（缺失时受控失败）、经既有校验原子写入；
/// 全程不加载、不提交凭据。读取与写入以委托注入，便于受控测试。
/// </summary>
internal static class ElevatedRecovery
{
    public static void Apply(bool enabled) =>
        Apply(enabled, Storage.LoadSettings, Storage.SaveSettings, SettingsWriteLock.Production);

    /// <summary>
    /// 读取与写入在保存锁内完成：与设置页、兼容提权编辑器共享同一跨进程互斥，
    /// 避免用较早读取的旧设置覆盖另一方刚保存的地址、密码或选项。
    /// </summary>
    internal static void Apply(bool enabled, Func<Settings?> load, Action<Settings> save, Func<IDisposable>? acquireLock = null)
    {
        using var writeLock = acquireLock?.Invoke();
        var settings = load() ?? throw new InvalidOperationException("请先保存校园网设置。");
        save(settings with { Enabled = enabled });
    }
}
