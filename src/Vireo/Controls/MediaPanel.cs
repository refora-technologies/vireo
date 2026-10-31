using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Vireo.Downloads;
using Vireo.Media;
using Vireo.Models;
using Vireo.Services;

namespace Vireo.Controls;

/// <summary>The Media drawer: everything the current page is playing, with a download for each.</summary>
public sealed class MediaPanel : Grid
{
    private readonly MainWindow _window;
    private readonly StackPanel _list = new() { Margin = new Thickness(10, 0, 10, 20) };
    private readonly TextBlock _summary = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("Text2"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _downloadAll;
    private readonly HashSet<string> _open = [];
    private readonly Dictionary<DownloadItem, MediaProgressView> _progressViews = [];
    private BrowserTab? _tab;

    public MediaPanel(MainWindow window)
    {
        _window = window;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new Grid { Margin = new Thickness(20, 0, 14, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(_summary);
        _downloadAll = new Button
        {
            Style = (Style)Application.Current.FindResource("SecondaryButton"),
            Content = "Download all",
            FontSize = 11.5,
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed
        };
        _downloadAll.Click += (_, _) =>
        {
            var usableItems = _tab?.Media.Where(m => m.Status == MediaStatus.Ready).ToList() ?? [];
            foreach (var item in usableItems)
            {
                var best = item.Choices.FirstOrDefault(c => !c.AudioOnly) ?? item.Choices.FirstOrDefault();
                if (best is not null) Start(item, best);
            }
        };

        var grabber = new Button
        {
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Content = "Save all from page…",
            FontSize = 11.5,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Pictures, videos and files linked from this page (Ctrl+Shift+G)"
        };
        grabber.Click += async (_, _) => await _window.ShowBatchGrabberAsync();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_downloadAll);
        buttons.Children.Add(grabber);
        Grid.SetColumn(buttons, 1);
        head.Children.Add(buttons);
        SetRow(head, 0);
        Children.Add(head);
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        SetRow(scroll, 1);
        Children.Add(scroll);

        // The switch for the buttons on the page lives here, where you find out about them.
        var host = _window.Active is { } t ? (Uri.TryCreate(t.Url, UriKind.Absolute, out var u) ? u.Host : "") : "";
        var footer = UiKit.Card(
            UiKit.Row("Buttons on videos and audio", "A Download button appears on a video or audio when you move over it.", UiKit.Switch(_window.Settings.ShowDownloadButtons, on => { _window.Settings.ShowDownloadButtons = on; _window.Settings.Commit(); })),
            host.Length > 0 ? UiKit.Row($"Hide them on {host}", null, UiKit.Switch(_window.Settings.NoButtonSites.Contains(host), on =>
            {
                _window.Settings.NoButtonSites.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
                if (on) _window.Settings.NoButtonSites.Add(host);
                _window.Settings.Commit();
            })) : new Border());
        footer.Margin = new Thickness(20, 8, 20, 18);
        SetRow(footer, 2);
        Children.Add(footer);

        Attach(_window.Active);
        Unloaded += (_, _) => Detach();
    }

    private void Detach()
    {
        if (_tab is not null) _tab.MediaChanged -= OnChanged;
        _window.Downloads.ItemChanged -= OnDownloadUpdated;
        _window.Downloads.ItemRemoved -= OnDownloadRemoved;
    }

    private void Attach(BrowserTab? tab)
    {
        Detach();
        _tab = tab;
        if (tab is not null) tab.MediaChanged += OnChanged;
        _window.Downloads.ItemChanged += OnDownloadUpdated;
        _window.Downloads.ItemRemoved += OnDownloadRemoved;
        Fill();
    }

    private bool _fillPending;
    private void OnChanged()
    {
        if (_fillPending) return;
        _fillPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _fillPending = false;
            Fill();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnDownloadUpdated(DownloadItem item) => Dispatcher.BeginInvoke(() =>
    {
        if (_progressViews.TryGetValue(item, out var pv))
        {
            pv.Refresh();
        }
        else if (_tab?.Media.Any(m => m.Download == item || m.Url == item.Url) == true)
        {
            OnChanged();
        }
    }, System.Windows.Threading.DispatcherPriority.Background);

    private void OnDownloadRemoved(DownloadItem item) => Dispatcher.BeginInvoke(() =>
    {
        if (_progressViews.Remove(item))
        {
            OnChanged();
        }
    }, System.Windows.Threading.DispatcherPriority.Background);

    private DownloadItem? FindDownload(DetectedMedia m)
    {
        if (m.Download is { } d && _window.Downloads.Items.Contains(d))
            return d;
        var match = _window.Downloads.Items.FirstOrDefault(i =>
            i.Url == m.Url ||
            (i.Stream != null && (i.Stream.PlaylistUrl == m.Url || m.Choices.Any(c => c.Url == i.Stream.PlaylistUrl))) ||
            m.Choices.Any(c => c.Url == i.Url || (c.AudioUrl != null && c.AudioUrl == i.Url)));
        if (match != null) m.Download = match;
        return match;
    }

    private void Fill()
    {
        using var _crumb = Breadcrumbs.Scope("media panel");
        _progressViews.Clear();
        _list.Children.Clear();
        var items = _tab?.Media.Where(m => !(m.Quiet && m.Status == MediaStatus.Probing)).ToList() ?? [];
        var usable = items.Count(m => m.Showable);
        _summary.Text = items.Count == 0 ? "" : usable == 1 ? "1 found on this page" : $"{usable} found on this page";
        _downloadAll.Visibility = usable > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count == 0)
        {
            _list.Children.Add(UiKit.Empty(0xE714, "Nothing to save on this page yet", "Open a page with a video or song. Vireo notices what a page plays (often before you press play) and offers every quality it finds."));
            return;
        }
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) _list.Children.Add(UiKit.Rule());
            _list.Children.Add(Card(items[i]));
        }
    }

    private UIElement Card(DetectedMedia m)
    {
        var stack = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());

        var thumb = new Border { Width = 104, Height = 58, CornerRadius = new CornerRadius(6), Background = UiKit.Brush("Surface2"), BorderBrush = UiKit.Brush("Line"), BorderThickness = new Thickness(1), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
        var fallback = UiKit.Icon(m.IsAudio ? 0xE8D6 : 0xE714, 18, "Text3");
        var layers = new Grid();
        layers.Children.Add(fallback);
        var picture = !string.IsNullOrEmpty(m.Poster) ? m.Poster : _tab?.PageImage;
        if (!string.IsNullOrEmpty(picture) && Uri.TryCreate(picture, UriKind.Absolute, out var p))
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = p;
                bi.CacheOption = BitmapCacheOption.OnDemand;
                bi.CreateOptions = BitmapCreateOptions.DelayCreation;
                bi.EndInit();
                var img = new Image { Stretch = Stretch.UniformToFill, Source = bi };
                layers.Children.Add(img);
            }
            catch { /* the picture is optional */ }
        }
        thumb.Child = layers;
        top.Children.Add(thumb);

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(m.Title) ? m.Url : m.Title, FontSize = 13, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36, LineHeight = 18 });
        // What it is, in one line of figures: kind · length · height · size
        var facts = new List<string> { m.KindLabel };
        if (m.DurationSec is { } d && d > 0) facts.Add(TimeSpan.FromSeconds(d).ToString(d >= 3600 ? "h\\:mm\\:ss" : "m\\:ss"));
        if (m.Height is { } h) facts.Add($"{h}p");
        if (m.Choices.FirstOrDefault(c => !c.AudioOnly && c.SizeBytes is not null) is { } big) facts.Add((m.Kind == MediaKind.File ? "" : "~") + Format.Bytes(big.SizeBytes!.Value));
        var line = new TextBlock { FontSize = 11.5, FontFamily = UiKit.Mono, Foreground = UiKit.Brush("Text3"), Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        if (m.Live) line.Inlines.Add(new System.Windows.Documents.Run("● LIVE  ") { Foreground = UiKit.Brush("Danger") });
        line.Inlines.Add(new System.Windows.Documents.Run(string.Join(" · ", facts)));
        text.Children.Add(line);
        Grid.SetColumn(text, 1);
        top.Children.Add(text);
        stack.Children.Add(top);

        switch (m.Status)
        {
            case MediaStatus.Probing:
                stack.Children.Add(UiKit.Muted("Reading the stream…", 11.5));
                break;
            case MediaStatus.Protected:
                stack.Children.Add(new TextBlock { Text = "Protected by DRM. Vireo does not download protected content.", FontSize = 11.5, Foreground = UiKit.Brush("Danger"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
                break;
            case MediaStatus.Failed:
                stack.Children.Add(new TextBlock { Text = m.Error ?? "Could not read this.", FontSize = 11.5, Foreground = UiKit.Brush("Danger"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
                var retry = new Button
                {
                    Style = (Style)Application.Current.FindResource("SecondaryButton"),
                    Content = "Try again",
                    Margin = new Thickness(0, 8, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                retry.Click += (_, _) =>
                {
                    if (_tab is not null) _window.RetryProbe(_tab, m);
                };
                stack.Children.Add(retry);
                break;
            default:
                var dl = FindDownload(m);
                if (dl is not null && dl.State is not DownloadState.Cancelled)
                {
                    var pv = new MediaProgressView(m, dl, _window.Downloads, () => Dispatcher.BeginInvoke(Fill), () => ToggleOptions(m));
                    _progressViews[dl] = pv;
                    stack.Children.Add(pv);
                    if (_open.Contains(m.Id))
                        stack.Children.Add(ChoicesList(m));
                }
                else
                {
                    stack.Children.Add(Actions(m));
                }
                break;
        }
        return new Border { Padding = new Thickness(10, 14, 10, 14), Child = stack };
    }

    private void ToggleOptions(DetectedMedia m)
    {
        if (!_open.Remove(m.Id)) _open.Add(m.Id);
        Fill();
    }

    private UIElement ChoicesList(DetectedMedia m)
    {
        // A small table of every quality: name, details, a download button. The row lights up under the mouse.
        var list = new Border { BorderBrush = UiKit.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(4) };
        var rows = new StackPanel();
        list.Child = rows;
        foreach (var c in m.Choices)
        {
            var r = new Grid();
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            r.ColumnDefinitions.Add(new ColumnDefinition());
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            r.Children.Add(new TextBlock { Text = c.Label, FontWeight = FontWeights.Medium, FontFamily = UiKit.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = UiKit.Brush(c.AudioOnly ? "Text2" : "Text") });
            var detail = new TextBlock { Text = c.Detail + (c.SizeBytes is { } b ? (c.Detail.Length > 0 ? " · " : "") + (m.Kind == MediaKind.File ? "" : "~") + Format.Bytes(b) : ""), FontSize = 11.5, Foreground = UiKit.Brush("Text3"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(detail, 1);
            r.Children.Add(detail);
            var b2 = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = ((char)0xE896).ToString(), Width = 28, Height = 28, FontSize = 12, ToolTip = "Download this" };
            var choice = c;
            b2.Click += (_, _) => Start(m, choice);
            Grid.SetColumn(b2, 2);
            r.Children.Add(b2);
            var cell = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 1, 2, 1), Background = Brushes.Transparent, Child = r };
            cell.MouseEnter += (_, _) => cell.Background = UiKit.Brush("Hover");
            cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
            rows.Children.Add(cell);
        }
        return list;
    }

    private UIElement Actions(DetectedMedia m)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var best = m.Choices.FirstOrDefault(c => !c.AudioOnly) ?? m.Choices.FirstOrDefault();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (best is not null)
        {
            var go = new Button { Style = (Style)Application.Current.FindResource("PrimaryButton"), Content = m.Live ? "Record" : best.AudioOnly || m.IsAudio ? "Download audio" : $"Download {best.Label}" };
            go.Click += (_, _) => Start(m, best);
            row.Children.Add(go);
        }
        if (m.Choices.Count > 1)
        {
            var more = new Button { Style = (Style)Application.Current.FindResource("GhostButton"), Content = _open.Contains(m.Id) ? "Hide qualities" : $"All {m.Choices.Count} qualities", Margin = new Thickness(6, 0, 0, 0) };
            more.Click += (_, _) => ToggleOptions(m);
            row.Children.Add(more);
        }
        wrap.Children.Add(row);

        if (_open.Contains(m.Id))
        {
            wrap.Children.Add(ChoicesList(m));
        }
        return wrap;
    }

    private void Start(DetectedMedia m, MediaChoice c)
    {
        if (_tab is { } tab) _ = _window.StartChoiceAsync(tab, m, c);
    }
}

/// <summary>Progress bar and status for a media item actively downloading or finished.</summary>
public sealed class MediaProgressView : StackPanel
{
    private readonly DetectedMedia _media;
    private readonly DownloadItem _item;
    private readonly DownloadEngine _engine;
    private readonly Action _onRebuild;
    private readonly Action _onToggleOptions;
    private readonly SegmentBar _bar;
    private readonly TextBlock _status;
    private readonly TextBlock _pct;
    private readonly StackPanel _buttons;
    private DownloadState? _shownState;

    public MediaProgressView(DetectedMedia media, DownloadItem item, DownloadEngine engine, Action onRebuild, Action onToggleOptions)
    {
        _media = media;
        _item = item;
        _engine = engine;
        _onRebuild = onRebuild;
        _onToggleOptions = onToggleOptions;
        Margin = new Thickness(0, 8, 0, 0);

        _bar = new SegmentBar { Item = item, Margin = new Thickness(0, 2, 0, 6) };

        var statusGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition());
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _status = new TextBlock { FontSize = 11.5, FontFamily = UiKit.Mono, Foreground = UiKit.Brush("Text2"), TextTrimming = TextTrimming.CharacterEllipsis };
        _pct = new TextBlock { FontSize = 11.5, FontFamily = UiKit.Mono, Foreground = UiKit.Brush("Accent"), Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(_pct, 1);

        statusGrid.Children.Add(_status);
        statusGrid.Children.Add(_pct);

        _buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        Children.Add(_bar);
        Children.Add(statusGrid);
        Children.Add(_buttons);

        Refresh();
    }

    public void Refresh()
    {
        var i = _item;
        _bar.Visibility = i.State is DownloadState.Cancelled ? Visibility.Collapsed : Visibility.Visible;
        _bar.Refresh();

        var size = i.Total > 0 ? Format.Bytes(i.Total) : "";
        var downloaded = i.Downloaded;
        var total = i.Total;
        var fraction = i.State == DownloadState.Completed ? 1 : total > 0 ? Math.Clamp((double)downloaded / total, 0, 1) : 0;
        var eta = i.Speed > 1 && total > 0 ? TimeSpan.FromSeconds(Math.Max(0, (total - downloaded) / i.Speed)) : (TimeSpan?)null;

        switch (i.State)
        {
            case DownloadState.Completed:
                _status.Text = $"Saved · {size}";
                _status.Foreground = UiKit.Brush("Info");
                _pct.Text = "";
                break;
            case DownloadState.Failed:
                _status.Text = i.Error ?? "Download failed";
                _status.Foreground = UiKit.Brush("Danger");
                _pct.Text = "";
                break;
            case DownloadState.Cancelled:
                _status.Text = "Cancelled";
                _status.Foreground = UiKit.Brush("Text3");
                _pct.Text = "";
                break;
            case DownloadState.Paused:
                _status.Text = $"Paused · {Format.Bytes(downloaded)}{(total > 0 ? " of " + size : "")}";
                _status.Foreground = UiKit.Brush("Text3");
                _pct.Text = total > 0 ? $"{(int)(fraction * 100)}%" : "";
                _pct.Foreground = UiKit.Brush("Text3");
                break;
            case DownloadState.Queued:
                _status.Text = "Waiting for a free slot…";
                _status.Foreground = UiKit.Brush("Text3");
                _pct.Text = "";
                break;
            default:
            {
                var parts = new List<string> { $"{Format.Bytes(downloaded)}{(total > 0 ? " of " + size : "")}" };
                if (i.Speed > 1) parts.Add(Format.Speed(i.Speed));
                var etaStr = Format.Eta(eta);
                if (etaStr.Length > 0) parts.Add(etaStr);
                _status.Text = string.Join(" · ", parts);
                _status.Foreground = UiKit.Brush("Text2");
                _pct.Text = total > 0 ? $"{(int)(fraction * 100)}%" : "";
                _pct.Foreground = UiKit.Brush("Accent");
                break;
            }
        }

        if (_shownState != i.State)
        {
            _shownState = i.State;
            BuildButtons();
        }
    }

    private void BuildButtons()
    {
        _buttons.Children.Clear();
        var i = _item;

        void AddIconBtn(int glyph, string tip, Action click)
        {
            var b = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = ((char)glyph).ToString(), ToolTip = tip, Width = 30, Height = 30, FontSize = 12.5, Margin = new Thickness(0, 0, 4, 0) };
            b.Click += (_, _) => click();
            _buttons.Children.Add(b);
        }

        switch (i.State)
        {
            case DownloadState.Downloading:
            case DownloadState.Queued:
                AddIconBtn(0xE769, "Pause", () => _engine.Pause(i));
                AddIconBtn(0xE711, "Cancel", () => { _engine.Cancel(i); _media.Download = null; _onRebuild(); });
                break;

            case DownloadState.Paused:
                AddIconBtn(0xE768, "Resume", () => _engine.Resume(i));
                AddIconBtn(0xE711, "Cancel", () => { _engine.Cancel(i); _media.Download = null; _onRebuild(); });
                break;

            case DownloadState.Completed:
                var openBtn = new Button
                {
                    Style = (Style)Application.Current.FindResource("SecondaryButton"),
                    Content = "Open",
                    Margin = new Thickness(0, 0, 4, 0)
                };
                openBtn.Click += (_, _) => OpenFile();
                _buttons.Children.Add(openBtn);
                AddIconBtn(0xE838, "Show in folder", ShowInFolder);
                break;

            case DownloadState.Failed:
                var retryBtn = new Button
                {
                    Style = (Style)Application.Current.FindResource("SecondaryButton"),
                    Content = "Try again",
                    Margin = new Thickness(0, 0, 4, 0)
                };
                retryBtn.Click += (_, _) => _engine.Resume(i);
                _buttons.Children.Add(retryBtn);
                AddIconBtn(0xE711, "Dismiss", () => { _media.Download = null; _onRebuild(); });
                break;
        }

        if (_media.Choices.Count > 1)
        {
            var optBtn = new Button
            {
                Style = (Style)Application.Current.FindResource("GhostButton"),
                Content = "Other qualities",
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Download another quality or audio"
            };
            optBtn.Click += (_, _) => _onToggleOptions();
            _buttons.Children.Add(optBtn);
        }
    }

    private void OpenFile()
    {
        try
        {
            if (File.Exists(_item.FinalPath))
                Process.Start(new ProcessStartInfo(_item.FinalPath) { UseShellExecute = true });
            else if (Directory.Exists(_item.Folder))
                Process.Start(new ProcessStartInfo("explorer.exe", _item.Folder) { UseShellExecute = true });
        }
        catch { }
    }

    private void ShowInFolder()
    {
        try
        {
            if (File.Exists(_item.FinalPath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_item.FinalPath}\"") { UseShellExecute = true });
            else if (Directory.Exists(_item.Folder))
                Process.Start(new ProcessStartInfo("explorer.exe", _item.Folder) { UseShellExecute = true });
        }
        catch { }
    }
}
