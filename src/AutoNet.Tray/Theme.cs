using System.Drawing.Drawing2D;

namespace AutoNet.Tray;

/// <summary>
/// Single place for every visual constant. Windows high contrast is explicitly out of scope for the
/// first release; keeping the values here makes that a one-file decision later.
/// </summary>
internal static class Theme
{
    public const string FontFamily = "Microsoft YaHei UI";

    // Brand and surfaces
    public static readonly Color Brand = Color.FromArgb(18, 72, 130);
    public static readonly Color BrandHover = Color.FromArgb(14, 58, 106);
    public static readonly Color WindowBackground = Color.FromArgb(247, 249, 252);
    public static readonly Color Card = Color.White;
    public static readonly Color Divider = Color.FromArgb(226, 231, 238);
    public static readonly Color Border = Color.FromArgb(219, 226, 235);

    // Text
    public static readonly Color TitleText = Color.FromArgb(23, 32, 44);
    public static readonly Color BodyText = Color.FromArgb(65, 75, 88);
    /// <summary>Secondary text. Darker than it looks like it needs to be: this carries the next-step
    /// instruction and the technical detail, so it has to clear WCAG AA (4.5:1) on every surface it is
    /// painted on — Card, WindowBackground and the chip surfaces — not just on white.</summary>
    public static readonly Color MutedText = Color.FromArgb(98, 110, 122);
    public static readonly Color OnBrand = Color.White;

    // Semantic. Every one of these is paired with an icon and a text label in the UI, so colour is
    // never the only carrier of meaning.
    public static readonly Color Healthy = Color.FromArgb(20, 108, 69);
    public static readonly Color HealthySurface = Color.FromArgb(232, 245, 237);
    public static readonly Color Warning = Color.FromArgb(158, 92, 8);
    public static readonly Color WarningSurface = Color.FromArgb(252, 244, 230);
    public static readonly Color Problem = Color.FromArgb(166, 42, 42);
    public static readonly Color ProblemSurface = Color.FromArgb(253, 236, 236);
    public static readonly Color Attention = Color.FromArgb(30, 95, 160);
    public static readonly Color AttentionSurface = Color.FromArgb(233, 241, 250);
    public static readonly Color Neutral = Color.FromArgb(96, 106, 120);
    public static readonly Color NeutralSurface = Color.FromArgb(240, 243, 247);

    // Metrics. Row heights are intentionally absent: the layout uses AutoSize and percent rows so it
    // adapts to DPI without a per-scale table.
    public const int PagePadding = 24;
    public const int CardPadding = 16;
    public const int CardGap = 12;
    public const int ButtonHeight = 34;
    public const int IconSize = 32;
    public const int SmallIconSize = 16;
    public const int CornerRadius = 8;

    public static Font Font(float size, FontStyle style = FontStyle.Regular)
        => new(FontFamily, size, style, GraphicsUnit.Point);

    /// <summary>Semantic colour for a display state. Unknown and stale deliberately stay neutral.</summary>
    public static (Color Fore, Color Surface) Palette(Core.NetworkPrimaryState state) => state switch
    {
        Core.NetworkPrimaryState.Online => (Healthy, HealthySurface),
        Core.NetworkPrimaryState.AuthenticationUnknown => (Attention, AttentionSurface),
        Core.NetworkPrimaryState.NeedsAuthentication or Core.NetworkPrimaryState.BindingRejected => (Problem, ProblemSurface),
        Core.NetworkPrimaryState.AuthenticatedOffline or Core.NetworkPrimaryState.DisconnectQuotaExhausted => (Warning, WarningSurface),
        _ => (Neutral, NeutralSurface)
    };
}

/// <summary>Shared factory for the window's controls so spacing, height, and keyboard defaults cannot drift.</summary>
internal static class Ui
{
    /// <summary>A card surface: white, rounded, hairline border, auto-sized to its content.</summary>
    public static Panel Card()
    {
        var panel = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Card, Padding = new Padding(Theme.CardPadding) };
        panel.Paint += (_, e) =>
        {
            var bounds = new Rectangle(0, 0, panel.Width - 1, panel.Height - 1);
            using var path = RoundedRect(bounds, Theme.CornerRadius);
            using var fill = new SolidBrush(Theme.Card);
            using var border = new Pen(Theme.Border);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
        };
        panel.SizeChanged += (_, _) => panel.Invalidate();
        return panel;
    }

    /// <summary>A card that fills its cell instead of hugging content.</summary>
    public static Panel CardFill()
    {
        var panel = Card();
        panel.Dock = DockStyle.Fill;
        panel.AutoSize = false;
        return panel;
    }

    public static TableLayoutPanel Stack(int columns = 1) => new()
    {
        Dock = DockStyle.Top, AutoSize = true, ColumnCount = columns, RowCount = 0,
        BackColor = Color.Transparent, Margin = new Padding(0)
    };

    public static Label Text(string value, float size = 10, FontStyle style = FontStyle.Regular, Color? color = null, bool wrap = false)
        => new()
        {
            Text = value, AutoSize = true, Font = Theme.Font(size, style),
            ForeColor = color ?? Theme.BodyText, BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 4)
        };

    /// <summary>
    /// Flat Fluent-style button. AutoSize grows to fit the text, so a longer or higher-DPI label can never
    /// be truncated; MinimumSize only enforces the shared height.
    ///
    /// AccessibleName is deliberately not set. WinForms prefers it over Text, so pinning it here would go
    /// stale the moment a caller relabels the button — and several do, to reflect state ("暂停自动恢复"
    /// becomes "恢复自动恢复"). An unset name means the button announces whatever it currently says.
    /// </summary>
    public static Button Button(string text, bool primary = false)
    {
        return new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, Theme.ButtonHeight), Padding = new Padding(14, 0, 14, 0),
            FlatStyle = FlatStyle.Flat, Font = Theme.Font(10), UseVisualStyleBackColor = false,
            ForeColor = primary ? Theme.OnBrand : Theme.TitleText,
            BackColor = primary ? Theme.Brand : Theme.Card,
            FlatAppearance = { BorderSize = primary ? 0 : 1, BorderColor = Theme.Border, MouseOverBackColor = primary ? Theme.BrandHover : Theme.NeutralSurface },
            Margin = new Padding(0, 0, 8, 8), TabStop = true, Cursor = Cursors.Hand
        };
    }

    /// <summary>
    /// Keeps a wrapping label inside its container at any width. A wrapped Label cannot size itself from a
    /// percent column alone, so the wrap width is recomputed whenever the card resizes — this is also what
    /// makes the layout survive DPI changes without a per-scale table.
    /// </summary>
    public static T Wrapping<T>(this T label, Control container, int inset = 0) where T : Label
    {
        void Apply()
        {
            var available = container.ClientSize.Width - container.Padding.Horizontal - inset;
            if (available > 80) label.MaximumSize = new Size(available, 0);
        }
        container.SizeChanged += (_, _) => Apply();
        label.HandleCreated += (_, _) => Apply();
        Apply();
        return label;
    }


    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
