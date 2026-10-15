namespace AutoNet.Tray.Avalonia;

/// <summary>壳内四个导航页。顺序即左侧导航顺序；页面内容由后续迁移工单填充（#4–#8）。</summary>
internal enum ShellPage
{
    Overview,
    Diagnostics,
    Logs,
    Settings
}

/// <summary>页面元数据：中文名称与迁移说明；术语与 CONTEXT.md 一致。</summary>
internal sealed record ShellPageInfo(ShellPage Page, string Name, string Description);

/// <summary>导航目录的单一来源。每个 <see cref="ShellPage"/> 必须恰好出现一次（有回归用例约束）。</summary>
internal static class ShellPages
{
    /// <summary>默认打开页：概览。</summary>
    public const ShellPage Default = ShellPage.Overview;

    public static readonly IReadOnlyList<ShellPageInfo> All =
    [
        new(ShellPage.Overview, "概览", "当前网络状态、AutoNet 正在做什么以及下一步操作。"),
        new(ShellPage.Diagnostics, "网络诊断", "校园 Wi-Fi、认证、直连与代理/系统路径等五类诊断（只读，不提交凭据）。"),
        new(ShellPage.Logs, "日志", "按日期查看受限范围的日志列表、详情与复制（保持脱敏）。"),
        new(ShellPage.Settings, "设置", "网卡、Wi-Fi 与认证信息；受限设置需要单独的提权操作。")
    ];

    public static ShellPageInfo Info(ShellPage page) => All.First(info => info.Page == page);
}
