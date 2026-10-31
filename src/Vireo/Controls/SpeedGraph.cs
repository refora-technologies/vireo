using System.Windows;
using System.Windows.Media;

namespace Vireo.Controls;

/// <summary>A small live chart of the download speed over the last minute: a soft line with a fading fill under it.</summary>
public sealed class SpeedGraph : FrameworkElement
{
    private IReadOnlyList<double> _values = [];

    public void Show(IReadOnlyList<double> values)
    {
        _values = values;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 4 || h < 4) return;
        var accent = (Color)Application.Current.FindResource("AccentColor");
        var baseline = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
        baseline.Freeze();
        dc.DrawLine(baseline, new Point(0, h - 0.5), new Point(w, h - 0.5));
        if (_values.Count < 2) return;

        var max = Math.Max(1, _values.Max()) * 1.15;
        var points = _values.Select((v, i) => new Point(i * w / (_values.Count - 1), h - 3 - v / max * (h - 8))).ToList();

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (var i = 1; i < points.Count; i++)
            {
                var (a, b) = (points[i - 1], points[i]);
                var mid = (a.X + b.X) / 2;
                ctx.BezierTo(new Point(mid, a.Y), new Point(mid, b.Y), b, true, true);
            }
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            ctx.LineTo(points[0], false, false);
            for (var i = 1; i < points.Count; i++)
            {
                var (a, b) = (points[i - 1], points[i]);
                var mid = (a.X + b.X) / 2;
                ctx.BezierTo(new Point(mid, a.Y), new Point(mid, b.Y), b, false, true);
            }
            ctx.LineTo(new Point(w, h), false, false);
        }
        area.Freeze();

        var fill = new LinearGradientBrush(Color.FromArgb(56, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        var pen = new Pen(new SolidColorBrush(accent), 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
        // the newest point
        dc.DrawEllipse(new SolidColorBrush(accent), null, points[^1], 2.5, 2.5);
    }
}
