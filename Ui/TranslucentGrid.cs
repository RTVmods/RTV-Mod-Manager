// A DataGridView that lets the form's painted background show through.
//
// A DataGridView paints its background and every cell opaque, so the
// decorations MainForm paints behind its controls (watermark, noise)
// stop at the grid's edge. This grid draws the matching slice of the
// form's backdrop bitmap under its own background and under each cell,
// and paints the cell colour over it at CellAlpha, so the decorations
// read through faintly while text stays on a dark field.

namespace VostokModManager.Ui;

public class TranslucentGrid : DataGridView
{
    /// <summary>Returns the form-sized backdrop and this grid's offset
    /// within it (client coordinates of the form). Null image = draw
    /// nothing extra.</summary>
    public Func<(Bitmap? Image, Point Offset)>? Backdrop { get; set; }

    /// <summary>Opacity of a cell's own colour over the backdrop,
    /// 0–255. 255 is an ordinary opaque grid.</summary>
    public int CellAlpha { get; set; } = 170;

    /// <summary>Opacity of the grid's BackgroundColor over the backdrop
    /// in the empty area below the rows.</summary>
    public int EmptyAlpha { get; set; } = 120;

    protected override void PaintBackground(Graphics graphics, Rectangle clipBounds, Rectangle gridBounds)
    {
        base.PaintBackground(graphics, clipBounds, gridBounds);
        if (!DrawBackdrop(graphics, clipBounds)) return;
        using var tint = new SolidBrush(Color.FromArgb(EmptyAlpha, BackgroundColor));
        graphics.FillRectangle(tint, clipBounds);
    }

    protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
    {
        // Subscribers go first; a handler that paints the cell itself
        // (the conflicts grid's banner rows) keeps its result.
        base.OnCellPainting(e);
        if (e.Handled || e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics == null) return;
        if (!DrawBackdrop(e.Graphics, e.CellBounds)) return;

        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        var style = e.CellStyle ?? DefaultCellStyle;
        var back = selected ? style.SelectionBackColor : style.BackColor;
        using (var brush = new SolidBrush(Color.FromArgb(CellAlpha, back)))
            e.Graphics.FillRectangle(brush, e.CellBounds);
        e.Paint(e.ClipBounds,
            e.PaintParts & ~(DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground));
        e.Handled = true;
    }

    /// <summary>Draws the backdrop slice behind `area` (grid client
    /// coordinates). False when there is no backdrop to draw.</summary>
    private bool DrawBackdrop(Graphics g, Rectangle area)
    {
        var bd = Backdrop?.Invoke();
        if (bd == null || bd.Value.Image == null) return false;
        var (img, off) = bd.Value;
        var src = new Rectangle(off.X + area.X, off.Y + area.Y, area.Width, area.Height);
        src.Intersect(new Rectangle(0, 0, img.Width, img.Height));
        if (src.IsEmpty) return false;
        var dst = new Rectangle(src.X - off.X, src.Y - off.Y, src.Width, src.Height);
        g.DrawImage(img, dst, src, GraphicsUnit.Pixel);
        return true;
    }
}
