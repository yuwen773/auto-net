using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 管理员恢复开关操作（#4）：按调用方给出的明确目标改写已保存设置的自动恢复开关。
/// 与既有 WinForms 托盘同语义——读取已保存设置（缺失时受控失败）、经既有校验原子写入；
/// 全程不加载、不提交凭据。读取与写入以委托注入，便于受控测试。
/// </summary>
internal static class ElevatedRecovery
{
    public static void Apply(bool enabled) => Apply(enabled, Storage.LoadSettings, Storage.SaveSettings);

    internal static void Apply(bool enabled, Func<Settings?> load, Action<Settings> save)
    {
        var settings = load() ?? throw new InvalidOperationException("请先保存校园网设置。");
        save(settings with { Enabled = enabled });
    }
}
