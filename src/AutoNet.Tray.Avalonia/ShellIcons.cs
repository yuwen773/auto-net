using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 线性图标（16×16 坐标系，仅描边不填充）：左侧导航图标与状态字形。装饰性图形，无业务含义；
/// 文本标签始终与图标并存，不用颜色或图形作为唯一信息。
/// </summary>
internal static class ShellIcons
{
    // 状态字形：语义分类对齐既有 WinForms 托盘（健康 / 进行中 / 需处理 / 未知 / 暂停），
    // 形状为线性风格重绘，不要求与旧位图逐像素一致；颜色与文字标题始终同时呈现。
    private const string CheckCircle = "M8 1.6 a6.4 6.4 0 1 1 0 12.8 a6.4 6.4 0 1 1 0 -12.8 M4.6 8.2 L7 10.6 L11.6 5.2";
    private const string CycleArrows = "M12.6 8 a4.6 4.6 0 1 1 -1.4 -3.3 M13 2.6 V5.4 H10.2";
    private const string QuestionCircle = "M8 1.6 a6.4 6.4 0 1 1 0 12.8 a6.4 6.4 0 1 1 0 -12.8 M6.3 6.3 a1.7 1.7 0 1 1 2.4 1.6 c-.6.3-.8.7-.8 1.3 M8 11.3 v.2";
    private const string CrossCircle = "M8 1.6 a6.4 6.4 0 1 1 0 12.8 a6.4 6.4 0 1 1 0 -12.8 M5.7 5.7 L10.3 10.3 M10.3 5.7 L5.7 10.3";
    private const string TriangleExclamation = "M8 2 L14.6 13.6 H1.4 Z M8 6.3 V9.7 M8 11.7 v.1";
    private const string PauseBars = "M5.5 2.4 H6.9 V13.6 H5.5 Z M9.1 2.4 H10.5 V13.6 H9.1 Z";
    private const string DotCircle = "M8 1.6 a6.4 6.4 0 1 1 0 12.8 a6.4 6.4 0 1 1 0 -12.8 M6.4 8 a1.6 1.6 0 1 0 3.2 0 a1.6 1.6 0 1 0 -3.2 0";

    public static string For(ShellPage page) => page switch
    {
        // 概览：房屋轮廓。
        ShellPage.Overview => "M2.6 7.4 L8 3 L13.4 7.4 M4.1 6.9 V13.1 H11.9 V6.9",
        // 网络诊断：脉冲折线。
        ShellPage.Diagnostics => "M1.6 8 H4.4 L6 3.4 L8.6 12.6 L10.4 8 H14.4",
        // 日志：文档与行。
        ShellPage.Logs => "M4.4 1.8 H9.4 L11.6 4 V14.2 H4.4 Z M9.4 1.8 V4 H11.6 M6.3 7.6 H9.7 M6.3 10.2 H8.6",
        // 设置：滑杆与旋钮。
        ShellPage.Settings => "M2 5.4 H14 M8 5.4 a1.6 1.6 0 1 0 3.2 0 a1.6 1.6 0 1 0 -3.2 0 " +
            "M2 10.6 H14 M4.4 10.6 a1.6 1.6 0 1 0 3.2 0 a1.6 1.6 0 1 0 -3.2 0",
        _ => ""
    };

    /// <summary>主状态字形；形状与语义色、文字标题同时呈现，冗余表达，不依赖单一通道。</summary>
    public static string StatusGlyph(NetworkPrimaryState state) => state switch
    {
        NetworkPrimaryState.Online => CheckCircle,
        NetworkPrimaryState.Reconnecting or NetworkPrimaryState.Associating or NetworkPrimaryState.WaitingForAddress => CycleArrows,
        NetworkPrimaryState.AuthenticationUnknown or NetworkPrimaryState.Unrecognized => QuestionCircle,
        NetworkPrimaryState.NeedsAuthentication or NetworkPrimaryState.BindingRejected => CrossCircle,
        NetworkPrimaryState.AuthenticatedOffline or NetworkPrimaryState.DisconnectQuotaExhausted or NetworkPrimaryState.ProbeFailed => TriangleExclamation,
        NetworkPrimaryState.RecoveryPaused => PauseBars,
        _ => DotCircle
    };

    /// <summary>认证列字形：认证暂停使用叉圆环，门户结构疑似变化使用感叹三角，不可确认使用问号圆环。</summary>
    public static string AuthColumnGlyph(OverviewAuthKind kind) => kind switch
    {
        OverviewAuthKind.Suspended => CrossCircle,
        OverviewAuthKind.LayoutSuspected => TriangleExclamation,
        OverviewAuthKind.Unconfirmable => QuestionCircle,
        _ => DotCircle
    };
}
