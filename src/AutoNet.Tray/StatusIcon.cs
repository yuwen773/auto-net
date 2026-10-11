using System.Drawing.Drawing2D;

namespace AutoNet.Tray;

/// <summary>
/// Code-drawn status glyphs. Drawn per requested size rather than scaled from one bitmap, so the
/// 32px and 16px renditions are each crisp. Icons are a redundant cue only — every state also carries
/// a text title, and the tray icon itself is a fixed brand mark.
/// </summary>
internal static class StatusIcon
{
    private static readonly Dictionary<(Core.NetworkPrimaryState State, int Size), Bitmap> cache = [];

    public static Bitmap For(Core.NetworkPrimaryState state, int size)
    {
        lock (cache)
        {
            if (cache.TryGetValue((state, size), out var cached)) return cached;
            var bitmap = Draw(state, size);
            cache[(state, size)] = bitmap;
            return bitmap;
        }
    }

    /// <summary>Disposes every cached bitmap. Call once when the tray process exits.</summary>
    public static void ReleaseAll()
    {
        lock (cache)
        {
            foreach (var bitmap in cache.Values) bitmap.Dispose();
            cache.Clear();
        }
    }

    private static Bitmap Draw(Core.NetworkPrimaryState state, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        var (fore, _) = Theme.Palette(state);
        var inset = Math.Max(1, size / 16);
        var box = new Rectangle(inset, inset, size - inset * 2, size - inset * 2);
        using var brush = new SolidBrush(fore);
        using var pen = new Pen(fore, Math.Max(1f, size / 12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        switch (state)
        {
            // Disc + wifi arcs for a healthy link.
            case Core.NetworkPrimaryState.Online:
                g.FillEllipse(brush, box.X, box.Y, box.Width / 3, box.Height / 3);
                g.DrawArc(pen, box.X, box.Y + box.Height / 6, box.Width, box.Height * 2 / 3, 200, 140);
                g.DrawArc(pen, box.X + box.Width / 5, box.Y + box.Height / 3, box.Width * 3 / 5, box.Height / 2, 200, 140);
                break;
            // Question mark for "cannot tell", never a cross.
            case Core.NetworkPrimaryState.AuthenticationUnknown:
            case Core.NetworkPrimaryState.Unrecognized:
                using (var font = new Font(Theme.FontFamily, size * 0.7f, FontStyle.Bold))
                    g.DrawString("?", font, brush, new PointF(-size * 0.04f, -size * 0.12f));
                break;
            // Exclamation for a state that needs attention but is not an error.
            case Core.NetworkPrimaryState.Reconnecting:
            case Core.NetworkPrimaryState.Associating:
            case Core.NetworkPrimaryState.WaitingForAddress:
                using (var font = new Font(Theme.FontFamily, size * 0.7f, FontStyle.Bold))
                    g.DrawString("!", font, brush, new PointF(-size * 0.03f, -size * 0.12f));
                break;
            // Cross for conditions that genuinely failed.
            case Core.NetworkPrimaryState.NeedsAuthentication:
            case Core.NetworkPrimaryState.BindingRejected:
            case Core.NetworkPrimaryState.AuthenticatedOffline:
            case Core.NetworkPrimaryState.DisconnectQuotaExhausted:
            case Core.NetworkPrimaryState.ProbeFailed:
                g.DrawLine(pen, box.X, box.Y, box.Right, box.Bottom);
                g.DrawLine(pen, box.Right, box.Y, box.X, box.Bottom);
                break;
            // Pause bars: recovery deliberately switched off.
            case Core.NetworkPrimaryState.RecoveryPaused:
                g.FillRectangle(brush, box.X + box.Width / 5, box.Y, box.Width / 5, box.Height);
                g.FillRectangle(brush, box.Right - box.Width / 5 - box.Width / 5, box.Y, box.Width / 5, box.Height);
                break;
            default:
                g.DrawEllipse(pen, box.X, box.Y, box.Width, box.Height);
                g.FillEllipse(brush, box.X + box.Width / 3, box.Y + box.Height / 3, box.Width / 3, box.Height / 3);
                break;
        }
        return bitmap;
    }

}
