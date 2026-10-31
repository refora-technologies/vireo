using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vireo.Services;

namespace Vireo.Controls;

/// <summary>The History and Bookmarks drawers: a search box above a list. Click a row to open it.</summary>
public sealed class LibraryPanel : Grid
{
    private readonly Library _lib;
    private readonly bool _history;
    private readonly Action<string> _open;
    private readonly StackPanel _list = new() { Margin = new Thickness(10, 0, 10, 20) };
    private readonly TextBox _search = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = UiKit.Brush("Text"), CaretBrush = UiKit.Brush("Accent"), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(34, 0, 8, 0) };
    private readonly TextBlock _hint;
    private bool _confirmClear;

    public LibraryPanel(Library library, bool history, Action<string> open)
    {
        _lib = library;
        _history = history;
        _open = open;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        _hint = new TextBlock { Text = history ? "Search history" : "Search bookmarks", Foreground = UiKit.Brush("Text3"), FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(34, 0, 0, 0) };
        var box = new Grid();
        var glass = UiKit.Icon(0xE721, 12, "Text3");
        glass.HorizontalAlignment = HorizontalAlignment.Left;
        glass.Margin = new Thickness(12, 0, 0, 0);
        box.Children.Add(glass);
        box.Children.Add(_hint);
        box.Children.Add(_search);
        var field = new Border { Background = UiKit.Brush("Bg"), BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Height = 36, Child = box };
        _search.GotKeyboardFocus += (_, _) => field.BorderBrush = UiKit.Brush("Accent");
        _search.LostKeyboardFocus += (_, _) => field.BorderBrush = UiKit.Brush("Line2");

        var top = new Grid { Margin = new Thickness(20, 0, 20, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(field);
        if (history)
        {
            var clear = new Button { Style = (Style)Application.Current.FindResource("GhostButton"), Content = "Clear all", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            clear.Click += (_, _) =>
            {
                if (!_confirmClear) { _confirmClear = true; clear.Content = "Click again to clear"; clear.Foreground = UiKit.Brush("Danger"); return; }
                _lib.ClearHistory();
            };
            Grid.SetColumn(clear, 1);
            top.Children.Add(clear);
        }
        SetRow(top, 0);
        Children.Add(top);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        SetRow(scroll, 1);
        Children.Add(scroll);

        _search.TextChanged += (_, _) => { _hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); };
        library.Changed += OnChanged;
        Unloaded += (_, _) => library.Changed -= OnChanged;
        Fill();
        Loaded += (_, _) => _search.Focus();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Fill);

    private static string DayLabel(DateTime t)
    {
        var today = DateTime.Now.Date;
        return t.Date == today ? "Today" : t.Date == today.AddDays(-1) ? "Yesterday" : t.Year == today.Year ? t.ToString("dddd, d MMMM") : t.ToString("d MMMM yyyy");
    }

    private void Fill()
    {
        _list.Children.Clear();
        var query = _search.Text.Trim();
        var rows = new List<(string Url, string Title, DateTime? When)>();
        if (query.Length > 0)
        {
            var hits = _lib.Search(query, 80);
            rows.AddRange(hits.Where(h => !_history || !h.IsBookmark || true).Select(h => (h.Url, h.Title, (DateTime?)null)));
            if (!_history) rows = rows.Where(r => _lib.IsBookmarked(r.Url)).ToList();
        }
        else if (_history) rows.AddRange(_lib.History.Take(400).Select(h => (h.Url, h.Title, (DateTime?)h.LastVisit)));
        else rows.AddRange(_lib.Bookmarks.Select(b => (b.Url, b.Title, (DateTime?)b.AddedAt)));

        if (rows.Count == 0)
        {
            _list.Children.Add(_history
                ? UiKit.Empty(0xE81C, query.Length > 0 ? "No matches" : "No history yet", query.Length > 0 ? "Try different words." : "Pages you visit show up here. Private windows leave nothing.")
                : UiKit.Empty(0xE734, query.Length > 0 ? "No matches" : "No bookmarks yet", query.Length > 0 ? "Try different words." : "Press the star in the address bar (Ctrl+D) to keep a page."));
            return;
        }

        string? day = null;
        foreach (var (url, title, when) in rows)
        {
            if (_history && when is { } w && query.Length == 0 && DayLabel(w) != day)
            {
                day = DayLabel(w);
                _list.Children.Add(new TextBlock { Text = day, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("Text2"), Margin = new Thickness(10, 16, 0, 6) });
            }
            _list.Children.Add(Row(url, title, when));
        }
    }

    private UIElement Row(string url, string title, DateTime? when)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority + (u.PathAndQuery.Length > 1 ? u.PathAndQuery : "") : url;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        text.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(title) ? host : title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = host, FontSize = 11.5, Foreground = UiKit.Brush("Text3"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });

        var remove = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = ((char)0xE711).ToString(), Width = 26, Height = 26, FontSize = 10, Opacity = 0, ToolTip = "Remove" };
        remove.Click += (_, _) => { if (_history) _lib.RemoveHistory(url); else _lib.RemoveBookmark(url); };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // The site's own icon when Vireo has seen it; otherwise a plain globe.
        UIElement mark = Services.SiteIcons.For(url) is { } img
            ? new Image { Source = img, Width = 16, Height = 16, Stretch = Stretch.Uniform }
            : UiKit.Icon(0xE774, 13, "Text3");
        RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
        var icon = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(6), Background = UiKit.Brush("Surface2"), Margin = new Thickness(0, 0, 12, 0), Child = mark };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        if (_history && when is { } t)
        {
            var time = new TextBlock { Text = t.ToString("HH:mm"), FontSize = 11.5, FontFamily = UiKit.Mono, Foreground = UiKit.Brush("Text3"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(time, 2);
            time.HorizontalAlignment = HorizontalAlignment.Right;
            grid.Children.Add(time);
        }
        grid.Children.Add(remove);

        var card = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 4, 7), Child = grid, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = url };
        card.MouseEnter += (_, _) => { card.Background = UiKit.Brush("Hover"); remove.Opacity = 1; };
        card.MouseLeave += (_, _) => { card.Background = Brushes.Transparent; remove.Opacity = 0; };
        card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Button && !IsInside<Button>(e.OriginalSource)) _open(url); };
        return card;
    }

    private static bool IsInside<T>(object source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is T) return true;
        return false;
    }
}
