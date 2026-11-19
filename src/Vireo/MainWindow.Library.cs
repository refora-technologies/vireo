using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Vireo.Controls;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

// Bookmarks, history, suggestions under the address bar, and finding text in a page.
public partial class MainWindow
{
    private Library _library = new(null);
    private Popup? _suggest;
    private StackPanel? _suggestList;
    private List<Suggestion> _suggestions = [];
    private int _suggestIndex = -1;
    private bool _suggesting = true;
    private CancellationTokenSource? _suggestCts;
    /// <summary>Lets the self-test stand in for the search engine.</summary>
    internal Func<string, CancellationToken, Task<List<string>>>? SuggestSource;
    private static readonly HttpClient SuggestHttp = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2), AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(2) };

    public Library Library => _library;

    private void StartLibrary(string folder)
    {
        _library = _private ? new Library(null) : (s_library ??= new Library(Path.Combine(folder, "library")));
        void Changed() => Dispatcher.BeginInvoke(UpdateBookmarkButton);
        _library.Changed += Changed;
        Closed += (_, _) => { _library.Changed -= Changed; if (_private) _library.Dispose(); };
    }

    internal void ShowHistory() => OpenDrawer("History", new LibraryPanel(_library, history: true, OpenInActive));
    internal void ShowBookmarks() => OpenDrawer("Bookmarks", new LibraryPanel(_library, history: false, OpenInActive));

    private void OpenInActive(string url)
    {
        if (Active is { } tab) Navigate(tab, url);
        else _ = OpenTabAsync(url);
    }

    internal void BookmarkActive()
    {
        if (Active is not { IsStartPage: false } tab || string.IsNullOrEmpty(tab.Url) || !tab.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        var added = _library.ToggleBookmark(tab.Url, tab.Title);
        PopIn(BookmarkButton);
        ShowToast(added ? "Bookmarked" : "Bookmark removed", tab.DisplayTitle, added ? ShowBookmarks : null);
    }

    private void UpdateBookmarkButton()
    {
        var url = Active?.Url ?? "";
        var on = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && _library.IsBookmarked(url);
        BookmarkButton.Content = on ? Glyph.StarFilled : Glyph.Star;
        BookmarkButton.Foreground = on ? UiKit.Brush("Accent") : UiKit.Brush("Text2");
        BookmarkButton.ToolTip = on ? "Remove bookmark (Ctrl+D)" : "Bookmark this page (Ctrl+D)";
    }

    /// <summary>Remembers a page once it has loaded.</summary>
    private void RecordVisit(BrowserTab tab, CoreWebView2 core)
    {
        if (_private || tab.IsStartPage || !core.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        using var _crumb = Breadcrumbs.Scope("history");
        _library.Visit(core.Source, core.DocumentTitle);
    }

    // ---- Suggestions under the address bar ----

    private void EnsureSuggest()
    {
        if (_suggest is not null) return;
        _suggestList = new StackPanel { Margin = new Thickness(5) };
        var card = new Border
        {
            Background = UiKit.Brush("Surface2"), CornerRadius = new CornerRadius(10), Child = _suggestList, Margin = new Thickness(12),
            BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Opacity = 0.5, Color = Colors.Black },
        };
        _suggest = new Popup { AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Bottom, PlacementTarget = OmniBox, PopupAnimation = PopupAnimation.None, Child = card, Focusable = false };
    }

    private string GetEngineName() => SearchEngines.For(_settings.SearchTemplate)?.Name ?? "Search";

    private async Task<List<string>> FetchEngineSuggestionsAsync(string query, CancellationToken ct)
    {
        var url = SearchEngines.SuggestUrl(_settings.SearchTemplate, query);
        if (url is null) return [];
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130.0.0.0");
            using var res = await SuggestHttp.SendAsync(req, ct);
            return res.IsSuccessStatusCode ? SearchEngines.ParseSuggestions(await res.Content.ReadAsStringAsync(ct)) : [];
        }
        catch { return []; }
    }

    private void UpdateSuggestions()
    {
        if (!_suggesting || (!Address.IsKeyboardFocused && !SelfTesting)) { CloseSuggestions(); return; }
        var text = Address.Text.Trim();
        if (text.Length == 0) { CloseSuggestions(); return; }

        _suggestCts?.Cancel();
        _suggestCts = new CancellationTokenSource();
        var ct = _suggestCts.Token;

        var local = _library.Search(text, 5).ToList();
        // What was typed comes first as a search of its own (unless it is plainly an address), so there is always something to pick at once.
        var asSearch = UrlInput.Resolve(text, _settings.SearchTemplate);
        if (asSearch is not null && asSearch.StartsWith(_settings.SearchTemplate[.._settings.SearchTemplate.IndexOf("{0}", StringComparison.Ordinal)], StringComparison.OrdinalIgnoreCase))
            local.Insert(Math.Min(local.Count, 3), new Suggestion(asSearch, text, false, IsSearch: true)); // after the best of your own pages
        _suggestions = [.. local];
        RenderSuggestions();

        if (_settings.SearchSuggestions && !_private && text.Length >= 2 && !text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(90, ct);
                    var engineQueries = await (SuggestSource?.Invoke(text, ct) ?? FetchEngineSuggestionsAsync(text, ct));
                    if (ct.IsCancellationRequested || engineQueries.Count == 0) return;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || (!Address.IsKeyboardFocused && !SelfTesting) || Address.Text.Trim() != text) return;
                        var combined = new List<Suggestion>(local);
                        foreach (var q in engineQueries)
                        {
                            if (!combined.Any(s => s.Title.Equals(q, StringComparison.OrdinalIgnoreCase)))
                            {
                                combined.Add(new Suggestion(string.Format(_settings.SearchTemplate, Uri.EscapeDataString(q)), q, false, IsSearch: true));
                            }
                            if (combined.Count >= 8) break;
                        }
                        _suggestions = combined;
                        RenderSuggestions();
                    });
                }
                catch { }
            }, ct);
        }
    }

    private void RenderSuggestions()
    {
        if (_suggestions.Count == 0) { HideSuggestions(); return; }
        EnsureSuggest();
        _suggestList!.Children.Clear();
        _suggestIndex = -1;
        for (var i = 0; i < _suggestions.Count; i++) _suggestList.Children.Add(SuggestionRow(_suggestions[i], i));
        ((Border)_suggest!.Child).Width = Math.Max(300, OmniBox.ActualWidth + 24);
        if (!_suggest.IsOpen)
        {
            _suggest.IsOpen = true;
            Native.KeepJustAbove(_suggest, this);
            var card = (Border)_suggest.Child;
            card.Opacity = 0;
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        }
    }

    private UIElement SuggestionRow(Suggestion s, int index)
    {
        var isSearch = s.IsSearch;
        var host = isSearch
            ? $"Search with {GetEngineName()}"
            : (Uri.TryCreate(s.Url, UriKind.Absolute, out var u) ? u.Authority + (u.PathAndQuery.Length > 1 ? u.PathAndQuery : "") : s.Url);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = s.Title, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 13, Foreground = UiKit.Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = host, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 11.5, Foreground = UiKit.Brush("Text3"), TextTrimming = TextTrimming.CharacterEllipsis });

        var iconCode = isSearch ? 0xE721 : s.IsBookmark ? 0xE735 : 0xE81C;
        var iconFg = s.IsBookmark ? "Accent" : "Text3";
        // A page Vireo has an icon for shows that icon; searches and the rest show a glyph.
        UIElement mark = !isSearch && Services.SiteIcons.For(s.Url) is { } img ? new Image { Source = img, Width = 16, Height = 16 } : UiKit.Icon(iconCode, 13, iconFg);
        var icon = new Border { Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0), Child = mark };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(text, 1);
        row.Children.Add(icon);
        row.Children.Add(text);
        var card = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 5, 8, 5), Child = row, Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = index };
        card.MouseEnter += (_, _) => Highlight(index);
        card.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _suggesting = false;
            CloseSuggestions();
            OpenInActive(s.Url);
            Active?.View?.Focus();
            _suggesting = true;
        };
        return card;
    }

    private void Highlight(int index)
    {
        _suggestIndex = index;
        if (_suggestList is null) return;
        foreach (var b in _suggestList.Children.OfType<Border>())
            b.Background = (int)b.Tag == index ? UiKit.Brush("Surface3") : Brushes.Transparent;
    }

    /// <summary>Closes the list and stops any request for ideas that is still on its way.</summary>
    private void CloseSuggestions()
    {
        _suggestCts?.Cancel();
        HideSuggestions();
    }

    /// <summary>Closes the list only. (The request for the engine's ideas must survive this: it is what fills the list again.)</summary>
    private void HideSuggestions()
    {
        if (_suggest is { IsOpen: true }) _suggest.IsOpen = false;
        _suggestIndex = -1;
    }

    /// <summary>Arrow keys move through suggestions; Enter on a highlighted one opens it. Returns true when the key was used.</summary>
    private bool SuggestionKey(Key key)
    {
        if (_suggest is not { IsOpen: true } || _suggestions.Count == 0) return false;
        switch (key)
        {
            case Key.Down: Highlight((_suggestIndex + 1) % _suggestions.Count); return true;
            case Key.Up: Highlight(_suggestIndex <= 0 ? _suggestions.Count - 1 : _suggestIndex - 1); return true;
            case Key.Enter when _suggestIndex >= 0:
                var url = _suggestions[_suggestIndex].Url;
                CloseSuggestions();
                OpenInActive(url);
                Active?.View?.Focus();
                return true;
            case Key.Delete when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _suggestIndex >= 0 && !_suggestions[_suggestIndex].IsSearch:
                // Shift+Delete: forget this page, as other browsers do.
                _library.RemoveHistory(_suggestions[_suggestIndex].Url);
                UpdateSuggestions();
                return true;
            case Key.Escape: CloseSuggestions(); return false;
        }
        return false;
    }

    // ---- Find in page ----

    private Popup? _findPopup;
    private TextBox? _findBox;
    private TextBlock? _findCount;

    private void UpdateFindPosition()
    {
        if (_findPopup is not null) _findPopup.HorizontalOffset = Math.Max(0, Pages.ActualWidth - 410);
    }

    internal void ShowFind()
    {
        if (Active?.View?.CoreWebView2 is not { } core) return;
        if (_findPopup is null) BuildFind();
        _findPopup!.PlacementTarget = Pages;
        UpdateFindPosition();
        _findPopup.VerticalOffset = 6;
        _findPopup.IsOpen = true;
        Native.KeepJustAbove(_findPopup, this);
        var card = (Border)_findPopup.Child;
        card.Opacity = 0;
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        _findBox!.Focus();
        _findBox.SelectAll();
        if (_findBox.Text.Length > 0) _ = StartFindAsync(core);
    }

    private void BuildFind()
    {
        _findBox = new TextBox { Width = 190, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = UiKit.Brush("Text"), CaretBrush = UiKit.Brush("Accent"), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("UiFont"), Margin = new Thickness(12, 0, 6, 0) };
        _findCount = new TextBlock { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 11.5, Foreground = UiKit.Brush("Text3"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 42, TextAlignment = TextAlignment.Center };
        Button Make(int glyph, string tip, Action click)
        {
            var b = new Button { Style = (Style)FindResource("IconButton"), Content = ((char)glyph).ToString(), ToolTip = tip, Width = 28, Height = 28, FontSize = 11 };
            b.Click += (_, _) => click();
            return b;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 36 };
        row.Children.Add(_findBox);
        row.Children.Add(_findCount);
        row.Children.Add(Make(0xE70E, "Previous (Shift+Enter)", () => Active?.View?.CoreWebView2?.Find.FindPrevious()));
        row.Children.Add(Make(0xE70D, "Next (Enter)", () => Active?.View?.CoreWebView2?.Find.FindNext()));
        row.Children.Add(Make(0xE711, "Close (Esc)", CloseFind));
        var card = new Border
        {
            Background = UiKit.Brush("Surface2"), CornerRadius = new CornerRadius(8), Child = row, Margin = new Thickness(14), Padding = new Thickness(0, 0, 4, 0),
            BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Opacity = 0.5, Color = Colors.Black },
        };
        _findPopup = new Popup { AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Relative, PopupAnimation = PopupAnimation.None, Child = card };
        Pages.SizeChanged += (_, _) => UpdateFindPosition();

        _findBox.TextChanged += async (_, _) =>
        {
            if (Active?.View?.CoreWebView2 is not { } core) return;
            if (_findBox.Text.Length == 0) { core.Find.Stop(); _findCount.Text = ""; return; }
            await StartFindAsync(core);
        };
        _findBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Active?.View?.CoreWebView2?.Find.FindPrevious();
                else Active?.View?.CoreWebView2?.Find.FindNext();
                e.Handled = true;
            }
        };
    }

    private async Task StartFindAsync(CoreWebView2 core)
    {
        var options = _env!.CreateFindOptions();
        options.FindTerm = _findBox!.Text;
        options.SuppressDefaultFindDialog = true;
        options.ShouldHighlightAllMatches = true;
        await core.Find.StartAsync(options);
        UpdateFindCount(core);
        core.Find.MatchCountChanged -= OnFindChanged;
        core.Find.ActiveMatchIndexChanged -= OnFindChanged;
        core.Find.MatchCountChanged += OnFindChanged;
        core.Find.ActiveMatchIndexChanged += OnFindChanged;
    }

    private void OnFindChanged(object? sender, object e) => Dispatcher.BeginInvoke(() => { if (Active?.View?.CoreWebView2 is { } c) UpdateFindCount(c); });

    private void UpdateFindCount(CoreWebView2 core)
    {
        if (_findCount is null) return;
        var total = core.Find.MatchCount;
        _findCount.Text = _findBox!.Text.Length == 0 ? "" : total == 0 ? "No matches" : $"{Math.Max(1, core.Find.ActiveMatchIndex)} of {total}";
        _findCount.Foreground = UiKit.Brush(total == 0 && _findBox.Text.Length > 0 ? "Danger" : "Text3");
    }

    /// <summary>The find bar was open when another program came in front: it comes back when Vireo does.</summary>
    private bool _findWaiting;

    internal void CloseFind()
    {
        _findWaiting = false;
        if (_findPopup is { IsOpen: true }) _findPopup.IsOpen = false;
        Active?.View?.CoreWebView2?.Find.Stop();
        Active?.View?.Focus();
    }

    internal bool FindOpen => _findPopup is { IsOpen: true };
    internal string FindCountText => _findCount?.Text ?? "";
    internal void TypeInFind(string text) { if (_findBox is not null) _findBox.Text = text; }
}
