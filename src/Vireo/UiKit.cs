using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Vireo;

/// <summary>Small builders so every panel (settings, downloads, history...) looks the same.</summary>
internal static class UiKit
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public static FontFamily Mono => (FontFamily)Application.Current.FindResource("MonoFont");

    /// <summary>A section title: sentence case, quiet, with room above it.</summary>
    public static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold,
        Foreground = Brush("Text2"), Margin = new Thickness(2, 22, 0, 8),
    };

    public static TextBlock Muted(string text, double size = 12) => new()
    {
        Text = text, FontSize = size, Foreground = Brush("Text3"), TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>A number that changes (sizes, counts, speeds) in the mono face, so it does not jiggle.</summary>
    public static TextBlock Number(string text, double size = 12, string brush = "Text3") => new()
    {
        Text = text, FontSize = size, FontFamily = Mono, Foreground = Brush(brush), VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A group of rows: an outline and hairlines between rows, no filled box.</summary>
    public static Border Card(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = Brush("Line") });
            stack.Children.Add(rows[i]);
        }
        return new Border { BorderBrush = Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = stack };
    }

    /// <summary>A label (and optional hint) on the left, a control on the right.</summary>
    public static Grid Row(string label, string? hint, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(14, 11, 12, 11) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // The words always keep room to be read; a control that wants more than half the row belongs in a Block.
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), MinWidth = 150 };
        text.Children.Add(new TextBlock { Text = label, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        if (hint is not null) text.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = Brush("Text3"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), LineHeight = 17 });
        grid.Children.Add(text);
        Grid.SetColumn((FrameworkElement)control, 1);
        ((FrameworkElement)control).VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>A control that sits under its label, using the full width.</summary>
    public static StackPanel Block(string label, string? hint, UIElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(14, 11, 12, 12) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 13 });
        if (hint is not null) panel.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = Brush("Text3"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), LineHeight = 17 });
        ((FrameworkElement)control).Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(control);
        return panel;
    }

    public static ToggleButton Switch(bool on, Action<bool> changed)
    {
        var t = new ToggleButton { Style = (Style)Application.Current.FindResource("Switch"), IsChecked = on };
        t.Click += (_, _) => changed(t.IsChecked == true);
        return t;
    }

    /// <summary>A row of choices where exactly one is picked.</summary>
    public static Border Segmented<T>(IReadOnlyList<(string Label, T Value)> options, T current, Action<T> changed)
    {
        // Each choice gets a share of the width in proportion to its label (plus room for padding), so a long one
        // ("DuckDuckGo") is never cut while short ones ("Bing") still fill the strip evenly.
        var grid = new Grid();
        var group = Guid.NewGuid().ToString();
        foreach (var (label, value) in options)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(label.Length + 5, GridUnitType.Star) });
            var r = new RadioButton { Style = (Style)Application.Current.FindResource("Segment"), Content = label, GroupName = group, IsChecked = EqualityComparer<T>.Default.Equals(value, current) };
            var chosen = value;
            r.Checked += (_, _) => changed(chosen);
            Grid.SetColumn(r, grid.Children.Count);
            grid.Children.Add(r);
        }
        return Track(grid);
    }

    /// <summary>The sunken strip a segmented choice sits in.</summary>
    public static Border Track(UIElement choices) => new() { Background = Brush("Bg"), CornerRadius = new CornerRadius(7), Padding = new Thickness(2), Child = choices };

    /// <summary>A number with minus and plus buttons.</summary>
    public static StackPanel Stepper(int value, int min, int max, int step, Func<int, string> format, Action<int> changed)
    {
        var shown = new TextBlock { Text = format(value), FontSize = 12.5, MinWidth = 84, FontFamily = Mono, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Button Make(string glyph, int delta)
        {
            var b = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = glyph, Width = 28, Height = 28, FontSize = 11 };
            b.Click += (_, _) =>
            {
                value = Math.Clamp(value + delta * step, min, max);
                shown.Text = format(value);
                changed(value);
            };
            return b;
        }
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Make(((char)0xE738).ToString(), -1)); // Remove
        panel.Children.Add(shown);
        panel.Children.Add(Make(((char)0xE710).ToString(), 1));  // Add
        return panel;
    }

    /// <summary>A scrolling page of sections, with the slim scrollbar.</summary>
    public static ScrollViewer Page(params UIElement[] children)
    {
        var stack = new StackPanel { Margin = new Thickness(18, 0, 18, 28) };
        foreach (var c in children) stack.Children.Add(c);
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    public static TextBlock Icon(int code, double size = 14, string brush = "Text2") => new()
    {
        Text = ((char)code).ToString(), FontFamily = (FontFamily)Application.Current.FindResource("Icons"),
        FontSize = size, Foreground = Brush(brush), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>The empty state shown when a list has nothing in it: an outline icon, a line, a hint. No bubble.</summary>
    public static StackPanel Empty(int icon, string title, string hint)
    {
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(30, 56, 30, 0) };
        p.Children.Add(Icon(icon, 26, "Text3"));
        p.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4), HorizontalAlignment = HorizontalAlignment.Center });
        p.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = Brush("Text3"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 260, LineHeight = 17 });
        return p;
    }

    /// <summary>A small square badge with a file's type in letters (MP4, ZIP...), the way a download list shows it.</summary>
    public static Border TypeBadge(string fileName, string brush = "Text2", double size = 36)
    {
        var ext = System.IO.Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        if (ext.Length is 0 or > 4) ext = "FILE";
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(6), BorderBrush = Brush("Line2"), BorderThickness = new Thickness(1), Background = Brush("Surface2"),
            Child = new TextBlock { Text = ext, FontFamily = Mono, FontSize = ext.Length > 3 ? 9 : 10, FontWeight = FontWeights.Medium, Foreground = Brush(brush), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    /// <summary>A thin line across a list.</summary>
    public static Border Rule(double left = 0) => new() { Height = 1, Background = Brush("Line"), Margin = new Thickness(left, 0, 0, 0) };
}
