namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 左侧导航的线性图标（16×16 坐标系，仅描边不填充）。装饰性图形，无业务含义；
/// 文本标签始终与图标并存，不用颜色或图形作为唯一信息。
/// </summary>
internal static class ShellIcons
{
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
}
