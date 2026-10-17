using Avalonia.Automation;
using Avalonia.Controls;
using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 关键控件的可访问名称（迁移工单 #14 / T06 / 故事 33）：名称与控件当前可见状态一致，
/// 并随内容投影重建动态更新。构造集中在本类单一来源，视图构建与 QA 断言共用同一实现，
/// 避免逐字漂移；名称只包含受控词表内的投影文本，不含凭据、URL 或原始响应。
/// </summary>
internal static class ShellAccessibility
{
    /// <summary>左侧导航项：名称与页面可见名称一致（T06 导航可达）。</summary>
    public static string NavItem(ShellPage page) => ShellPages.Info(page).Name;

    /// <summary>恢复开关（故事 33、40）：名称携带当前状态与点击将发送的明确意图，随状态重建自动更新。</summary>
    public static string RecoveryToggle(RecoverySwitchState recovery) => $"自动恢复（{recovery.State}），{recovery.Label}";

    /// <summary>解除认证暂停入口：名称携带当前认证事实与动作，不依据陈旧信息。</summary>
    public static string Unblock(OverviewContent content) => $"认证状态（{content.Auth.Title}），{content.Unblock.Label}";

    /// <summary>浏览器兜底开页入口（#11）：名称固定声明「需要浏览器认证」边界 + 动作标签。</summary>
    public static string PortalOpen(PortalOpenEntryState entry) => $"需要浏览器认证，{entry.Label}";

    /// <summary>诊断包导出入口（#12）：进行中时名称如实反映并与按钮禁用状态一致。</summary>
    public static string Export(DiagnosticsExportState state) => state.Running ? "诊断包导出（进行中）" : "导出诊断包…";

    /// <summary>日志页日期入口（故事 44）：下拉框无自带文本标签，名称显式声明。</summary>
    public const string LogsDates = "日志日期";

    /// <summary>日志页筛选入口：下拉框无自带文本标签，名称显式声明。</summary>
    public const string LogsFilter = "日志筛选";

    /// <summary>日志页受限事件列表（故事 45）：列表控件名称显式声明。</summary>
    public const string LogsList = "日志事件列表";

    /// <summary>日志页技术诊断详情（故事 46）：只读文本区名称显式声明。</summary>
    public const string LogsDetail = "日志详情（只读）";

    /// <summary>底部状态栏（T06 可达）：状态文本随轮询更新，名称固定声明区域用途。</summary>
    public const string StatusStrip = "后台状态栏";

    /// <summary>左侧导航列表（T06 可达）：导航项名称逐项由 <see cref="NavItem"/> 提供。</summary>
    public const string NavList = "页面导航";

    /// <summary>技术详情展开区（概览页）：标题固定，展开状态由控件自身状态呈现。</summary>
    public const string TechnicalDetail = "技术详情";

    /// <summary>设置控件可访问名称的统一入口，视图构建时链式调用。</summary>
    public static T Named<T>(T control, string name) where T : Control
    {
        AutomationProperties.SetName(control, name);
        return control;
    }
}
