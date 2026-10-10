using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AutoNet.Tray;

internal static class NetworkIcon
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(64, 64);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(18, 72, 130));
        g.FillEllipse(background, 1, 1, 62, 62);
        using var pen = new Pen(Color.White, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, 12, 16, 40, 38, 220, 100);
        g.DrawArc(pen, 21, 27, 22, 22, 220, 100);
        g.FillEllipse(Brushes.White, 29, 43, 6, 6);
        var handle = bitmap.GetHicon();
        try { using var temporary = Icon.FromHandle(handle); return (Icon)temporary.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
