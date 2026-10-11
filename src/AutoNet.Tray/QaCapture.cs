namespace AutoNet.Tray;

/// <summary>
/// Offline screenshot support for scripts/qa-tray.ps1. Inactive unless AUTONET_CAPTURE_PATH is set,
/// so it never runs on a user's machine.
/// </summary>
internal static class QaCapture
{
    /// <summary>
    /// Renders the window off-screen with Control.DrawToBitmap.
    ///
    /// Deliberately not a screen grab: CopyFromScreen captures whatever else happens to be on the
    /// desktop at that rectangle, which on a developer's machine means an unrelated window — and, worse,
    /// its contents get written into the repository's artefacts. DrawToBitmap renders only this form,
    /// so a screenshot can never pick up anything the tray was not showing.
    ///
    /// Two known blind spots remain, both already recorded in docs/VALIDATION.md and both render blank
    /// rather than wrong: ComboBox items, and ListView selection. Anything else visible in a capture is
    /// a real rendering result.
    ///
    /// This is still not a DPI test: <c>Scale</c> re-lays the window out at a different size, which is a
    /// different thing from Windows reporting a different display DPI. Real 100/125/150% acceptance
    /// remains outstanding.
    /// </summary>
    public static async void Run(Form form, Func<Task>? ready = null)
    {
        var path = Environment.GetEnvironmentVariable("AUTONET_CAPTURE_PATH");
        if (string.IsNullOrEmpty(path)) return;
        try { if (ready is not null) await ready(); } catch (Exception) { }
        if (form.IsDisposed) return;
        if (float.TryParse(Environment.GetEnvironmentVariable("AUTONET_QA_SCALE"), System.Globalization.CultureInfo.InvariantCulture, out var scale) && scale is >= 1 and <= 2)
            form.Scale(new SizeF(scale, scale));
        Settle(form);
        await Task.Delay(150);   // let the resize after Scale() reach the window manager
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path);
        form.BeginInvoke(() => Application.Exit());
    }

    /// <summary>Runs layout for the whole control tree, deepest first, so every cell has real bounds.</summary>
    private static void Settle(Control root)
    {
        foreach (Control child in root.Controls) Settle(child);
        root.PerformLayout();
        root.ResumeLayout(false);   // flush pending layout queued by Dock/Fill assignment
    }
}