using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vireo.Downloads;
using Vireo.Models;
using Vireo.Services;

namespace Vireo.Controls;

/// <summary>IDM-grade Page Grabber: collects all downloadable media, images, documents, and archives on the page.</summary>
public sealed class BatchGrabberPanel : Grid
{
    private readonly MainWindow _window;
    private readonly List<GrabbedItem> _allItems;
    private readonly StackPanel _list = new() { Margin = new Thickness(16, 4, 16, 20) };
    private readonly TextBlock _summary = new() { FontSize = 12, Foreground = UiKit.Brush("Text3"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = UiKit.Brush("Text"), CaretBrush = UiKit.Brush("Accent"), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _searchHint = new() { Text = "Filter by name or extension…", Foreground = UiKit.Brush("Text3"), FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly Button _downloadBtn;
    private readonly CheckBox _selectAll;
    private readonly TextBlock _selectCount = new() { FontSize = 12, Foreground = UiKit.Brush("Text2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

    private const int RowLimit = 250;
    private GrabCategory _activeCategory = GrabCategory.All;
    private string _filterText = "";

    public BatchGrabberPanel(MainWindow window, List<GrabbedItem> items)
    {
        _window = window;
        _allItems = items;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Category tabs
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Search field
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Selection header & download button
        RowDefinitions.Add(new RowDefinition());                            // Scrollable items list

        // 1. Category selector
        var catPanel = BuildCategorySelector();
        SetRow(catPanel, 0);
        Children.Add(catPanel);

        // 2. Search / Filter box
        var searchBox = new Grid();
        searchBox.Children.Add(_searchHint);
        _search.Margin = new Thickness(14, 0, 4, 0);
        searchBox.Children.Add(_search);
        var searchField = new Border { Background = UiKit.Brush("Bg"), BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Height = 36, Child = searchBox, Margin = new Thickness(20, 0, 20, 10) };
        _search.TextChanged += (_, _) =>
        {
            _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _filterText = _search.Text.Trim().ToLowerInvariant();
            RefreshList();
        };
        SetRow(searchField, 1);
        Children.Add(searchField);

        // 3. Selection row & Download button
        var selectRow = new Grid { Margin = new Thickness(20, 0, 20, 8) };
        selectRow.ColumnDefinitions.Add(new ColumnDefinition());
        selectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var selectLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _selectAll = new CheckBox { Content = "Select All", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Foreground = UiKit.Brush("Text") };
        _selectAll.Click += (_, _) =>
        {
            var isChecked = _selectAll.IsChecked == true;
            foreach (var item in GetFilteredItems()) item.IsSelected = isChecked;
            RefreshList();
        };
        selectLeft.Children.Add(_selectAll);
        selectLeft.Children.Add(_selectCount);
        selectRow.Children.Add(selectLeft);

        _downloadBtn = new Button
        {
            Style = (Style)Application.Current.FindResource("PrimaryButton"),
            Content = "Download Selected",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _downloadBtn.Click += async (_, _) => await DownloadSelectedAsync();
        Grid.SetColumn(_downloadBtn, 1);
        selectRow.Children.Add(_downloadBtn);

        SetRow(selectRow, 2);
        Children.Add(selectRow);

        // 4. Scrollable list
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        SetRow(scroll, 3);
        Children.Add(scroll);

        RefreshList();
    }

    private UIElement BuildCategorySelector()
    {
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Margin = new Thickness(20, 0, 20, 10) };
        var wrap = new StackPanel { Orientation = Orientation.Horizontal };

        var mediaCount = _allItems.Count(i => i.Category == GrabCategory.Media);
        var imgCount = _allItems.Count(i => i.Category == GrabCategory.Images);
        var docCount = _allItems.Count(i => i.Category == GrabCategory.Documents);
        var arcCount = _allItems.Count(i => i.Category == GrabCategory.Archives);

        Button CatBtn(string name, GrabCategory cat, int count)
        {
            var btn = new Button
            {
                Content = $"{name} {count}",
                Margin = new Thickness(0, 0, 2, 0),
                Padding = new Thickness(10, 5, 10, 5),
                Style = (Style)Application.Current.FindResource("GhostButton")
            };
            void Mark(Button b, bool on) { b.Background = on ? UiKit.Brush("Surface4") : null; b.Foreground = UiKit.Brush(on ? "Text" : "Text2"); }
            Mark(btn, cat == _activeCategory);
            btn.Click += (_, _) =>
            {
                _activeCategory = cat;
                foreach (Button b in wrap.Children) Mark(b, b == btn);
                RefreshList();
            };
            return btn;
        }

        wrap.Children.Add(CatBtn("All", GrabCategory.All, _allItems.Count));
        if (mediaCount > 0) wrap.Children.Add(CatBtn("Media", GrabCategory.Media, mediaCount));
        if (imgCount > 0) wrap.Children.Add(CatBtn("Images", GrabCategory.Images, imgCount));
        if (docCount > 0) wrap.Children.Add(CatBtn("Docs", GrabCategory.Documents, docCount));
        if (arcCount > 0) wrap.Children.Add(CatBtn("Archives", GrabCategory.Archives, arcCount));

        scroll.Content = wrap;
        return scroll;
    }

    private IEnumerable<GrabbedItem> GetFilteredItems()
    {
        var query = _allItems.AsEnumerable();
        if (_activeCategory != GrabCategory.All)
        {
            query = query.Where(i => i.Category == _activeCategory);
        }
        if (!string.IsNullOrWhiteSpace(_filterText))
        {
            query = query.Where(i => i.FileName.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
                                  || i.Extension.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
                                  || i.Url.Contains(_filterText, StringComparison.OrdinalIgnoreCase));
        }
        return query;
    }

    private void RefreshList()
    {
        _list.Children.Clear();
        var visible = GetFilteredItems().ToList();
        var selectedCount = visible.Count(i => i.IsSelected);

        _selectCount.Text = $"{selectedCount} selected";
        _selectAll.IsChecked = visible.Count > 0 && selectedCount == visible.Count ? true : selectedCount > 0 ? null : false;
        _downloadBtn.IsEnabled = selectedCount > 0;
        _downloadBtn.Content = selectedCount > 0 ? $"Download ({selectedCount})" : "Download";

        if (visible.Count == 0)
        {
            _list.Children.Add(UiKit.Empty(0xE714, "No items found", "Try selecting another category or clearing the search filter."));
            return;
        }

        // A page can list thousands of images; drawing them all would make the panel crawl.
        foreach (var item in visible.Take(RowLimit))
        {
            _list.Children.Add(CreateItemRow(item));
        }
        if (visible.Count > RowLimit)
            _list.Children.Add(new TextBlock { Text = $"Showing the first {RowLimit} of {visible.Count}. Type in the box above to narrow the list; Select All still covers all {visible.Count}.", TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = UiKit.Brush("Text3"), Margin = new Thickness(4, 6, 4, 0) });
    }

    private UIElement CreateItemRow(GrabbedItem item)
    {
        var card = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(10, 7, 10, 7),
        };
        card.MouseEnter += (_, _) => card.Background = UiKit.Brush("Hover");
        card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Checkbox
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Category Icon
        grid.ColumnDefinitions.Add(new ColumnDefinition());                          // Text (Name + URL)
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Extension badge

        var chk = new CheckBox
        {
            IsChecked = item.IsSelected,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        chk.Click += (_, _) =>
        {
            item.IsSelected = chk.IsChecked == true;
            UpdateSelectionCounts();
        };
        Grid.SetColumn(chk, 0);
        grid.Children.Add(chk);

        var (iconCode, iconFg) = item.Category switch
        {
            GrabCategory.Media => (0xE714, "Accent"),
            GrabCategory.Images => (0xEB9F, "Accent"),
            GrabCategory.Documents => (0xE8A5, "Text"),
            GrabCategory.Archives => (0xE8B7, "Accent"),
            _ => (0xE71B, "Text2")
        };

        var iconHost = UiKit.TypeBadge(item.FileName.Contains('.') ? item.FileName : "x." + item.Extension, "Text2", 30);
        iconHost.Margin = new Thickness(0, 0, 10, 0);
        iconHost.VerticalAlignment = VerticalAlignment.Center;
        _ = (iconCode, iconFg);
        Grid.SetColumn(iconHost, 1);
        grid.Children.Add(iconHost);

        var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        textPanel.Children.Add(new TextBlock
        {
            Text = item.FileName,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = UiKit.Brush("Text")
        });

        var host = Uri.TryCreate(item.Url, UriKind.Absolute, out var u) ? u.Host : item.Url;
        textPanel.Children.Add(new TextBlock
        {
            Text = host,
            FontSize = 11,
            Foreground = UiKit.Brush("Text3"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(textPanel, 2);
        grid.Children.Add(textPanel);


        card.Child = grid;
        return card;
    }

    private void UpdateSelectionCounts()
    {
        var visible = GetFilteredItems().ToList();
        var selectedCount = visible.Count(i => i.IsSelected);
        _selectCount.Text = $"{selectedCount} selected";
        _selectAll.IsChecked = visible.Count > 0 && selectedCount == visible.Count ? true : selectedCount > 0 ? null : false;
        _downloadBtn.IsEnabled = selectedCount > 0;
        _downloadBtn.Content = selectedCount > 0 ? $"Download ({selectedCount})" : "Download";
    }

    internal IReadOnlyList<GrabbedItem> Items => _allItems;
    internal Task DownloadForTestAsync() => DownloadSelectedAsync();

    private async Task DownloadSelectedAsync()
    {
        var selected = _allItems.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        var tab = _window.Active;
        var core = tab?.View?.CoreWebView2;
        var referer = core?.Source ?? tab?.Url ?? "";
        var agent = core?.Settings.UserAgent;

        int queued = 0;
        foreach (var item in selected)
        {
            if (item.StreamMedia is not null)
            {
                var best = item.StreamMedia.Choices.FirstOrDefault(c => !c.AudioOnly) ?? item.StreamMedia.Choices.FirstOrDefault();
                if (best is not null && tab is not null)
                {
                    _ = _window.StartChoiceAsync(tab, item.StreamMedia, best);
                    queued++;
                    continue;
                }
            }

            string? cookie = null;
            if (core is not null)
            {
                try
                {
                    var cookies = await core.CookieManager.GetCookiesAsync(item.Url);
                    if (cookies.Count > 0)
                    {
                        cookie = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                    }
                }
                catch { /* proceed without cookies */ }
            }

            _window.Downloads.Add(new DownloadRequest(item.Url, item.FileName, referer, agent, cookie));
            queued++;
        }

        _window.ShowToast("Downloading", $"{queued} files queued", _window.ShowDownloads);
    }
}
