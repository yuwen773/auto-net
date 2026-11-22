using System.Globalization;

namespace AutoNet.Core;

/// <summary>一次只读测量的结果：内容尺寸与当时的视口尺寸，全部是 CSS 像素。</summary>
public readonly record struct AuthPageZoomMetrics(
    double ContentWidth, double ContentHeight, double ViewportWidth, double ViewportHeight);

/// <summary>
/// 认证页只读缩放（ADR-0016，2026-11-22 修订）：把门户整页缩放到承载盒，效果等同浏览器缩放
/// （Ctrl+滚轮），页面仍按自己的设计排版，只是整体缩小，而不是被重排成另一套布局。
///
/// 本类只承担两件不依赖 WebView2 的纯事：
/// 1. <see cref="MeasureScript"/>——在页面里读四个尺寸（内容宽高、视口宽高），只读，不写 DOM、
///    不碰表单、不读 cookie/存储、不发请求；回归测试逐字钉住这些性质。
/// 2. <see cref="Fit"/>——由测量结果算出缩放比例，纯函数，离线可回归。
///
/// 测量必须在 ZoomFactor=1 时进行：缩放会同时改变视口尺寸，在已缩放的状态下测量得到的是
/// 被缩放后的视口，结果会自指（越量越小或越量越大）。
/// </summary>
public static class AuthPageZoom
{
    /// <summary>缩放下限：再小文字就无法辨认，宁可留纵向滚动条，也不把页面缩成缩略图。</summary>
    public const double MinimumZoom = 0.55;

    /// <summary>缩放上限：只缩小、不放大，小页面保持原样。</summary>
    public const double MaximumZoom = 1.0;

    /// <summary>
    /// 只读尺寸测量脚本：返回 <c>内容宽;内容高;视口宽;视口高</c>（CSS 像素，分号分隔）。
    /// 只读 <c>documentElement</c> 的滚动尺寸与窗口视口尺寸，不读写其他 DOM、不触达表单与存储。
    /// </summary>
    public const string MeasureScript =
        "(function(){var d=document.documentElement;" +
        "return [d.scrollWidth,d.scrollHeight,innerWidth,innerHeight].join(';');})()";

    /// <summary>
    /// 目标缩放比例。整页放得下就按高度收，否则先把横向滚动条消掉、纵向留给滚动——
    /// 横向滚动条是这类固定宽度门户页最难看的东西，纵向滚动是浏览器的常态。
    /// </summary>
    public static double Fit(AuthPageZoomMetrics metrics)
    {
        if (!IsUsable(metrics.ViewportWidth) || !IsUsable(metrics.ViewportHeight) ||
            !IsUsable(metrics.ContentWidth) || !IsUsable(metrics.ContentHeight))
            return MaximumZoom;

        var widthFit = Math.Clamp(metrics.ViewportWidth / metrics.ContentWidth, MinimumZoom, MaximumZoom);
        var heightFit = metrics.ViewportHeight / metrics.ContentHeight;
        // 高度比例只有落在“可辨认下限”与“宽度比例”之间时才更优：否则宽度就是约束。
        return heightFit >= MinimumZoom && heightFit < widthFit ? heightFit : widthFit;
    }

    /// <summary>
    /// 解析 <c>ExecuteScriptAsync</c> 的返回值。脚本返回字符串，因此结果是带引号的 JSON 字符串；
    /// 任何非四个有限正数的输入都算无效，调用方据此保持 1.0，不猜。
    /// </summary>
    public static bool TryParseMeasurement(string? result, out AuthPageZoomMetrics metrics)
    {
        metrics = default;
        if (string.IsNullOrWhiteSpace(result)) return false;
        var text = result.Trim();
        // ExecuteScriptAsync 把结果按 JSON 序列化：字符串值会带一层引号（含可能的转义）。
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        var parts = text.Split(';');
        if (parts.Length != 4) return false;
        var values = new double[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]))
                return false;
        }

        var parsed = new AuthPageZoomMetrics(values[0], values[1], values[2], values[3]);
        if (!IsUsable(parsed.ContentWidth) || !IsUsable(parsed.ContentHeight) ||
            !IsUsable(parsed.ViewportWidth) || !IsUsable(parsed.ViewportHeight))
            return false;
        metrics = parsed;
        return true;
    }

    /// <summary>尺寸必须是有界的正数：0、负数、NaN 与离谱的大值都视为无效测量。</summary>
    private static bool IsUsable(double value) => value > 0 && value <= 200000 && !double.IsNaN(value);
}
