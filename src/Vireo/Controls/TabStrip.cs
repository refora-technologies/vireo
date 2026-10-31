using System.Windows;
using System.Windows.Controls;

namespace Vireo.Controls;

/// <summary>
/// Lays tabs out in a row. Tabs share the width: they start wide and shrink as more are opened,
/// and a tab being opened or closed grows or shrinks smoothly through <see cref="PresenceProperty"/>
/// (0 = not there, 1 = full size). The last child is the "new tab" button and is never squeezed.
/// </summary>
public sealed class TabStrip : Panel
{
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(TabStrip),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Horizontal: tabs share the width (top and bottom). Vertical: a list, one tab per row (left and right).</summary>
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    public const double RowHeight = 38;

    public const double MaxTabWidth = 240;
    public const double MinTabWidth = 24;
    private const double Gap = 2;

    public static readonly DependencyProperty PresenceProperty = DependencyProperty.RegisterAttached(
        "Presence", typeof(double), typeof(TabStrip),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static double GetPresence(DependencyObject d) => (double)d.GetValue(PresenceProperty);
    public static void SetPresence(DependencyObject d, double value) => d.SetValue(PresenceProperty, value);

    private double TabWidth(double available, out double trailing)
    {
        var count = InternalChildren.Count;
        trailing = count > 0 ? InternalChildren[count - 1].DesiredSize.Width : 0;
        double weight = 0;
        for (var i = 0; i < count - 1; i++) weight += GetPresence(InternalChildren[i]);
        if (weight <= 0) return MaxTabWidth;
        var room = Math.Max(0, available - trailing - Gap * Math.Max(0, count - 2));
        return Math.Clamp(room / weight, MinTabWidth, MaxTabWidth);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Orientation == Orientation.Vertical) return MeasureVertical(availableSize);
        var count = InternalChildren.Count;
        if (count == 0) return default;
        var last = InternalChildren[count - 1];
        last.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var width = TabWidth(availableSize.Width, out var trailing);
        double used = trailing;
        for (var i = 0; i < count - 1; i++)
        {
            var child = InternalChildren[i];
            var w = width * GetPresence(child);
            child.Measure(new Size(Math.Max(0, w), availableSize.Height));
            used += w + Gap;
        }
        return new Size(Math.Min(used, double.IsInfinity(availableSize.Width) ? used : availableSize.Width), availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Orientation == Orientation.Vertical) return ArrangeVertical(finalSize);
        var count = InternalChildren.Count;
        if (count == 0) return finalSize;
        var width = TabWidth(finalSize.Width, out var trailing);
        double x = 0;
        for (var i = 0; i < count - 1; i++)
        {
            var child = InternalChildren[i];
            var w = Math.Max(0, width * GetPresence(child));
            child.Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w + Gap;
        }
        InternalChildren[count - 1].Arrange(new Rect(x, 0, trailing, finalSize.Height));
        return finalSize;
    }

    // ---- Vertical list: every tab is a full-width row; a tab opening or closing grows or shrinks its row. ----

    private Size MeasureVertical(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 220 : available.Width;
        double y = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var isTrailing = i == InternalChildren.Count - 1;
            var h = isTrailing ? RowHeight : RowHeight * GetPresence(child);
            child.Measure(new Size(width, Math.Max(0, h)));
            y += Math.Max(0, h);
        }
        return new Size(width, y);
    }

    private Size ArrangeVertical(Size finalSize)
    {
        double y = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var isTrailing = i == InternalChildren.Count - 1;
            var h = Math.Max(0, isTrailing ? RowHeight : RowHeight * GetPresence(child));
            child.Arrange(new Rect(0, y, finalSize.Width, h));
            y += h;
        }
        return finalSize;
    }
}
