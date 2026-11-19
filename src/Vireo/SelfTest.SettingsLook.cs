using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>
    /// The whole Settings panel, screen by screen, at the narrowest panel width and the usual one, and a check that no
    /// button or label is wider than the card it sits in (cut off at its edge).
    /// </summary>
    private async Task RunSettingsLookStepsAsync()
    {
        foreach (var width in new[] { 1280, 860 })
        {
            w.Width = width;
            await Task.Delay(400);
            w.ShowSettings();
            await Task.Delay(700);
            var body = w.DrawerContent as DependencyObject;
            var scroll = body as ScrollViewer ?? FindAll<ScrollViewer>(body).FirstOrDefault();
            if (scroll is null) { Check("the settings panel scrolls", false); return; }
            var overflow = new List<string>();
            for (var page = 0; page < 12; page++)
            {
                await Task.Delay(250);
                await Shot($"60-settings-{width}-{page:00}");
                if (scroll.VerticalOffset >= scroll.ScrollableHeight - 1) break;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + scroll.ViewportHeight * 0.85);
            }
            // Anything drawn past the right edge of the panel is cut off.
            var panelRight = scroll.ActualWidth;
            foreach (var fe in FindAll<FrameworkElement>(scroll).Where(f => f is Button or TextBlock && f.IsVisible && f.ActualWidth > 0))
            {
                try
                {
                    var right = fe.TransformToAncestor(scroll).Transform(new Point(fe.ActualWidth, 0)).X;
                    if (right > panelRight + 1) overflow.Add($"{fe.GetType().Name} '{Describe(fe)}' ends at {right:0} of {panelRight:0}");
                }
                catch { /* not in this tree */ }
            }
            // A label whose text is wider than the room it got (and is cut, not wrapped).
            // (Labels meant to shorten with "..." are left out: they say so with TextTrimming.)
            var dpi = VisualTreeHelper.GetDpi(scroll).PixelsPerDip;
            foreach (var tb in FindAll<TextBlock>(scroll).Where(t => t.IsVisible && t.TextWrapping == TextWrapping.NoWrap && t.TextTrimming == TextTrimming.None && t.ActualWidth > 0 && t.Text.Length > 0))
            {
                var natural = new FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture, tb.FlowDirection,
                    new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
                var room = tb.Parent is FrameworkElement p && p.ActualWidth > 0 ? Math.Min(tb.ActualWidth, p.ActualWidth) : tb.ActualWidth;
                var shown = VisualTreeHelper.GetParent(tb) is FrameworkElement holder && holder.ActualWidth > 0 ? Math.Min(room, holder.ActualWidth) : room;
                if (natural > shown + 1.5) overflow.Add($"text '{tb.Text}' needs {natural:0} px, has {shown:0}");
            }
            Check($"nothing in Settings is cut off at a {width} px window", overflow.Count == 0, string.Join(" | ", overflow.Distinct().Take(8)));
            w.CloseDrawer();
            await Task.Delay(400);
        }
        w.Width = 1280;
    }

    private static string Describe(FrameworkElement fe) => fe switch
    {
        TextBlock t => t.Text,
        ContentControl c => c.Content?.ToString() ?? "",
        _ => fe.Name,
    };

    private static IEnumerable<T> FindAll<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) yield break;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var deeper in FindAll<T>(child)) yield return deeper;
        }
    }
}
