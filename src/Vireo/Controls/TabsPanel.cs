using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vireo.Models;
using Vireo.Services;

namespace Vireo.Controls;

/// <summary>
/// Every open tab in one place: search them, see which are awake or asleep, switch to one, put one to sleep or close it.
/// (The menu keeps only the memory total; this list can be as long as it needs to be.)
/// </summary>
public sealed class TabsPanel : Grid
{
    private readonly MainWindow _window;
    private readonly StackPanel _list = new() { Margin = new Thickness(10, 0, 10, 20) };
    private readonly TextBox _search = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = UiKit.Brush("Text"), CaretBrush = UiKit.Brush("Accent"), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(34, 0, 8, 0) };
    private readonly TextBlock _summary = new() { FontSize = 12, Foreground = UiKit.Brush("Text3"), TextWrapping = TextWrapping.Wrap };

    public TabsPanel(MainWindow window)
    {
        _window = window;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var hint = new TextBlock { Text = "Search open tabs", Foreground = UiKit.Brush("Text3"), FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(34, 0, 0, 0) };
        var glass = UiKit.Icon(0xE721, 12, "Text3");
        glass.HorizontalAlignment = HorizontalAlignment.Left;
        glass.Margin = new Thickness(12, 0, 0, 0);
        var box = new Grid();
        box.Children.Add(glass);
        box.Children.Add(hint);
        box.Children.Add(_search);
        var field = new Border { Background = UiKit.Brush("Bg"), BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Height = 36, Child = box };
        _search.TextChanged += (_, _) => { hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); };

        var free = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Free memory now", ToolTip = "Puts every hidden tab to sleep" };
        free.Click += async (_, _) => { await _window.FreeMemoryNowAsync(); Fill(); };
        var top = new StackPanel { Margin = new Thickness(20, 0, 20, 8) };
        top.Children.Add(field);
        var line = new DockPanel { Margin = new Thickness(2, 10, 0, 0) };
        DockPanel.SetDock(free, Dock.Right);
        line.Children.Add(free);
        _summary.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(_summary);
        top.Children.Add(line);
        SetRow(top, 0);
        Children.Add(top);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        SetRow(scroll, 1);
        Children.Add(scroll);
        Loaded += (_, _) => _search.Focus();
        Fill();
    }

    private static string StateOf(BrowserTab t, BrowserTab? active) =>
        t == active ? "Showing" : t.Life switch { TabLife.Active or TabLife.Background => "Awake", TabLife.Suspended => "Asleep", _ => "Freed" };

    private void Fill()
    {
        _list.Children.Clear();
        _rows = 0;
        var tabs = _window.AllTabs.ToList();
        var active = _window.Active;
        var (bytes, processes) = _window.MeasureMemory();
        var awake = tabs.Count(t => t.Life is TabLife.Active or TabLife.Background);
        _summary.Text = $"{tabs.Count} tabs · {awake} awake · {bytes / 1048576.0:0} MB";
        _summary.ToolTip = $"{processes} browser processes";

        var q = _search.Text.Trim();
        var shown = tabs.Where(t => q.Length == 0 || t.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Url.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (shown.Count == 0) { _list.Children.Add(UiKit.Empty(0xE721, "No tab matches", "Try other words.")); return; }

        // Awake tabs first (they are the ones using memory), then the sleeping ones.
        foreach (var group in shown.GroupBy(t => StateOf(t, active) is "Showing" or "Awake").OrderByDescending(g => g.Key))
        {
            _list.Children.Add(new TextBlock { Text = group.Key ? "Awake" : "Asleep (using little or no memory)", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("Text2"), Margin = new Thickness(10, 14, 0, 6) });
            foreach (var t in group) _list.Children.Add(Row(t, StateOf(t, active)));
        }
    }

    private UIElement Row(BrowserTab tab, string state)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        UIElement mark = tab.Favicon is { } img ? new Image { Source = img, Width = 16, Height = 16 } : UiKit.Icon(0xE774, 13, "Text3");
        var icon = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(6), Background = UiKit.Brush("Surface2"), Margin = new Thickness(0, 0, 12, 0), Child = mark };
        grid.Children.Add(icon);

        var host = Uri.TryCreate(tab.Url, UriKind.Absolute, out var u) ? u.Host : "Start page";
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        text.Children.Add(new TextBlock { Text = tab.DisplayTitle, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = UiKit.Brush(state is "Asleep" or "Freed" ? "Text2" : "Text") });
        text.Children.Add(new TextBlock { Text = host, FontSize = 11.5, Foreground = UiKit.Brush("Text3"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var chip = new TextBlock { Text = state, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = UiKit.Brush(state == "Showing" ? "Accent" : "Text3") };
        Grid.SetColumn(chip, 2);
        grid.Children.Add(chip);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0 };
        if (state == "Awake")
        {
            var sleep = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = ((char)0xE708).ToString(), Width = 28, Height = 28, FontSize = 12, ToolTip = "Put to sleep" };
            sleep.Click += async (_, _) => { await _window.SuspendAsync(tab); Fill(); };
            actions.Children.Add(sleep);
        }
        var close = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = ((char)0xE711).ToString(), Width = 28, Height = 28, FontSize = 11, ToolTip = "Close tab" };
        close.Click += async (_, _) => { await _window.CloseTabAsync(tab); Fill(); };
        actions.Children.Add(close);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 4, 7), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = grid, ToolTip = tab.Url };
        row.MouseEnter += (_, _) => { row.Background = UiKit.Brush("Hover"); actions.Opacity = 1; };
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; actions.Opacity = 0; };
        row.MouseLeftButtonUp += async (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInButton(d)) return;
            await OpenAsync(tab);
        };
        _rows++;
        return row;
    }

    private int _rows;
    /// <summary>How many tabs the list shows now.</summary>
    internal int RowCount => _rows;
    internal void Search(string text) => _search.Text = text;

    /// <summary>Switches to the tab and gets the panel out of the way.</summary>
    internal async Task OpenAsync(BrowserTab tab)
    {
        await _window.ActivateAsync(tab);
        _window.CloseDrawer();
    }

    private static bool IsInButton(DependencyObject d)
    {
        for (var n = d; n is not null; n = n is Visual ? VisualTreeHelper.GetParent(n) : LogicalTreeHelper.GetParent(n))
            if (n is Button) return true;
        return false;
    }
}
