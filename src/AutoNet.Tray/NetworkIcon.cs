using System.Drawing.Drawing2D;

namespace AutoNet.Tray;

internal static class NetworkIcon
{
    public static Icon Create(int size = 32)
    {
        using var stream = typeof(NetworkIcon).Assembly.GetManifestResourceStream("AutoNet.Assets.Icon.ico")!;
        using var icon = new Icon(stream, size, size);
        return (Icon)icon.Clone();
    }

    public static Bitmap CreateBitmap(int size = 48)
    {
        using var stream = typeof(NetworkIcon).Assembly.GetManifestResourceStream("AutoNet.Assets.Logo.png")!;
        using var source = Image.FromStream(stream);
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, size, size));
        return bitmap;
    }
}
