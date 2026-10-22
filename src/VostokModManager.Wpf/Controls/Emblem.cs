using System.Windows;
using System.Windows.Media;

namespace VostokModManager.Wpf.Controls;

/// <summary>The Hannunvaakuna (⌘, St. John's Arms) in steel: the emblem
/// every mod's art carries. Same geometry as the WinForms build and the
/// banner SVG — a square with a 270° loop at each corner, in a 14.5-unit
/// half-size space — scaled to fit the element.</summary>
public sealed class Emblem : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Emblem),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD3)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    private static readonly Geometry Shape = Build();

    private static Geometry Build()
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(-6.9, -4.2), false, true);
            ctx.LineTo(new Point(6.9, -4.2), true, true);
            Arc(ctx, 6.9, -6.9, 90, -270);
            ctx.LineTo(new Point(4.2, 6.9), true, true);
            Arc(ctx, 6.9, 6.9, 180, -270);
            ctx.LineTo(new Point(-6.9, 4.2), true, true);
            Arc(ctx, -6.9, 6.9, 270, -270);
            ctx.LineTo(new Point(-4.2, -6.9), true, true);
            Arc(ctx, -6.9, -6.9, 0, -270);
        }
        g.Freeze();
        return g;
    }

    /// <summary>An arc of radius 2.7 around (cx, cy), angles in degrees
    /// measured clockwise in y-down space (GDI's convention).</summary>
    private static void Arc(StreamGeometryContext ctx, double cx, double cy, double start, double sweep)
    {
        const double r = 2.7;
        var end = (start + sweep) * Math.PI / 180;
        ctx.ArcTo(new Point(cx + r * Math.Cos(end), cy + r * Math.Sin(end)), new Size(r, r), 0,
            Math.Abs(sweep) > 180,
            sweep < 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise, true, true);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        // The loops reach 9.6 units from the centre; leave a little air.
        var scale = size / 2 / 10.4;
        dc.PushTransform(new TranslateTransform(ActualWidth / 2, ActualHeight / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, new Pen(Stroke, 1.7) { LineJoin = PenLineJoin.Round }, Shape);
        dc.Pop();
        dc.Pop();
    }
}
