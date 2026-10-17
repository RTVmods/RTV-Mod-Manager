// The progress band the mods grid paints over the row of a mod whose
// archive is being downloaded: a translucent fill up to the fraction
// received with a bright leading edge, or, while the total size is
// unknown, a short band sliding along the row. Kept apart from the
// form so it can be drawn onto any Graphics for a look.

namespace VostokModManager.Ui;

public static class DownloadBand
{
    /// <summary>Phase period for the sliding (unknown-total) band; the
    /// band enters from the left at 0 and has left on the right at
    /// Period.</summary>
    public const int Period = 120;

    public static readonly Color Fill = Color.FromArgb(95, 110, 190, 240);
    public static readonly Color Edge = Color.FromArgb(230, 140, 205, 250);

    /// <param name="b">The row's bounds, already clipped to the visible width.</param>
    /// <param name="done">Bytes received.</param>
    /// <param name="total">Total bytes, or 0 when unknown.</param>
    /// <param name="phase">0..Period-1, advanced by a timer while the total is unknown.</param>
    public static void Paint(Graphics g, Rectangle b, long done, long total, int phase)
    {
        if (b.Width <= 0 || b.Height <= 0) return;
        using var fill = new SolidBrush(Fill);
        using var edge = new SolidBrush(Edge);
        if (total > 0)
        {
            var frac = Math.Clamp((double)done / total, 0, 1);
            var w = (int)Math.Round(b.Width * frac);
            if (w > 0) g.FillRectangle(fill, b.X, b.Y, w, b.Height);
            g.FillRectangle(edge, b.X + Math.Max(0, w - 2), b.Y, 3, b.Height);
        }
        else
        {
            var bandW = Math.Max(40, b.Width / 6);
            var travel = b.Width + bandW;
            var x = b.X - bandW + (int)(travel * ((phase % Period) / (double)Period));
            var r = Rectangle.Intersect(new Rectangle(x, b.Y, bandW, b.Height), b);
            if (!r.IsEmpty) g.FillRectangle(fill, r);
            var ex = x + bandW - 2;
            if (ex >= b.X && ex < b.Right) g.FillRectangle(edge, ex, b.Y, 3, b.Height);
        }
    }
}
