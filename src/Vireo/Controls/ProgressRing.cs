using System.Windows;
using System.Windows.Media;

namespace Vireo.Controls;

/// <summary>A thin ring that fills clockwise: drawn around the toolbar's download button while something downloads.</summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 to 1; below zero shows a short spinning arc (the size is not known).</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4) return;
        const double thickness = 2.2;
        var r = (size - thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var track = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), thickness);
        track.Freeze();
        dc.DrawEllipse(null, track, c, r, r);

        var accent = (Color)Application.Current.FindResource("AccentColor");
        var value = Value;
        double start = -90, sweep;
        if (value < 0) { start = (Environment.TickCount64 / 4 % 360) - 90; sweep = 90; }
        else sweep = Math.Clamp(value, 0, 1) * 360;
        if (sweep <= 0.5) return;
        if (sweep >= 359.5) { var full = new Pen(new SolidColorBrush(accent), thickness); full.Freeze(); dc.DrawEllipse(null, full, c, r, r); return; }

        Point At(double degrees) => new(c.X + r * Math.Cos(degrees * Math.PI / 180), c.Y + r * Math.Sin(degrees * Math.PI / 180));
        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(At(start), false, false);
            ctx.ArcTo(At(start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, true);
        }
        arc.Freeze();
        var pen = new Pen(new SolidColorBrush(accent), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }
}
