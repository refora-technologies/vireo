using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Vireo.Downloads;
using Vireo.Services;

namespace Vireo.Controls;

/// <summary>The Downloads drawer: paste a link, see the speed, and the list of transfers.</summary>
public sealed class DownloadsPanel : Grid
{
    private readonly DownloadEngine _engine;
    private readonly Dictionary<DownloadItem, DownloadRow> _rows = [];
    private readonly StackPanel _list = new() { Margin = new Thickness(10, 0, 10, 20) };
    private readonly TextBlock _summary = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("Text2"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _clear;
    private readonly TextBox _url = new() { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Foreground = UiKit.Brush("Text"), CaretBrush = UiKit.Brush("Accent"), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
    private readonly TextBlock _hint = new() { Text = "Paste a link to download", Foreground = UiKit.Brush("Text3"), FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _field;
    private readonly UIElement _empty = UiKit.Empty(0xE896, "No downloads yet", "Files you download appear here. Big files are fetched over several connections at once, and pick up where they stopped after a break.");

    public DownloadsPanel(DownloadEngine engine)
    {
        _engine = engine;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        // Paste a link: one field with its button inside it.
        var add = new Button { Style = (Style)Application.Current.FindResource("PrimaryButton"), Content = "Download", Padding = new Thickness(12, 4, 12, 4), FontSize = 12, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        add.Click += (_, _) => Submit();
        var box = new Grid();
        box.ColumnDefinitions.Add(new ColumnDefinition());
        box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        box.Children.Add(_hint);
        _url.Margin = new Thickness(12, 0, 4, 0);
        box.Children.Add(_url);
        SetColumn(add, 1);
        box.Children.Add(add);
        _field = new Border { Background = UiKit.Brush("Bg"), BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Height = 38, Child = box, Margin = new Thickness(20, 0, 20, 16) };
        _url.GotKeyboardFocus += (_, _) => _field.BorderBrush = UiKit.Brush("Accent");
        _url.LostKeyboardFocus += (_, _) => _field.BorderBrush = UiKit.Brush("Line2");
        _url.TextChanged += (_, _) => { _hint.Visibility = _url.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; _url.Foreground = UiKit.Brush("Text"); };
        _url.KeyDown += (_, e) => { if (e.Key == Key.Enter) Submit(); };
        SetRow(_field, 0);
        Children.Add(_field);

        // Live speed
        var speed = SpeedStrip();
        SetRow(speed, 1);
        Children.Add(speed);
        var sampler = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        sampler.Tick += (_, _) => Sample();
        sampler.Start();
        Unloaded += (_, _) => sampler.Stop();
        Sample();

        // The list's title line
        _clear = new Button { Style = (Style)Application.Current.FindResource("GhostButton"), Content = "Clear finished", FontSize = 11.5, Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Right };
        _clear.Click += (_, _) => ClearFinished();
        var header = new Grid { Margin = new Thickness(20, 4, 14, 6) };
        header.Children.Add(_summary);
        header.Children.Add(_clear);
        SetRow(header, 2);
        Children.Add(header);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        SetRow(scroll, 3);
        Children.Add(scroll);

        // The engine lists newest first, and each row goes in at the top: so the oldest goes in first, and the newest ends on top.
        foreach (var item in engine.Items.Reverse()) AddRow(item, animate: false);
        UpdateSummary();

        engine.ItemAdded += OnAdded;
        engine.ItemRemoved += OnRemoved;
        engine.ItemChanged += OnChanged;
        Unloaded += (_, _) =>
        {
            engine.ItemAdded -= OnAdded;
            engine.ItemRemoved -= OnRemoved;
            engine.ItemChanged -= OnChanged;
        };

        // A link already on the clipboard is probably what you want to download.
        try
        {
            if (Clipboard.ContainsText() && Uri.TryCreate(Clipboard.GetText().Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https") _url.Text = u.ToString();
        }
        catch { /* the clipboard can be busy */ }
    }

    // ---- Speed: a number, what is running, and a minute of history that only shows once something has moved ----

    private readonly List<double> _speeds = [];
    private readonly SpeedGraph _graph = new() { Height = 40, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _speedNumber = new() { FontSize = 28, FontFamily = UiKit.Mono, FontWeight = FontWeights.Medium, Foreground = UiKit.Brush("Text"), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _speedUnit = new() { FontSize = 13, FontFamily = UiKit.Mono, Foreground = UiKit.Brush("Text3"), Margin = new Thickness(6, 0, 0, 4), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _speedNote = new() { FontSize = 12, Foreground = UiKit.Brush("Text3"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4), TextAlignment = TextAlignment.Right, LineHeight = 17 };

    private UIElement SpeedStrip()
    {
        var numbers = new Grid();
        var big = new StackPanel { Orientation = Orientation.Horizontal };
        big.Children.Add(_speedNumber);
        big.Children.Add(_speedUnit);
        numbers.Children.Add(big);
        numbers.Children.Add(_speedNote);
        var stack = new StackPanel { Margin = new Thickness(20, 0, 20, 14) };
        stack.Children.Add(numbers);
        stack.Children.Add(_graph);
        var wrap = new StackPanel();
        wrap.Children.Add(stack);
        wrap.Children.Add(UiKit.Rule());
        return wrap;
    }

    private void Sample()
    {
        var active = _engine.Items.Where(i => i.State == DownloadState.Downloading).ToList();
        var speed = active.Sum(i => i.Speed);
        _speeds.Add(speed);
        if (_speeds.Count > 60) _speeds.RemoveAt(0);
        _graph.Show(_speeds);
        _graph.Visibility = _speeds.Any(s => s > 1) ? Visibility.Visible : Visibility.Collapsed;
        var text = Format.Speed(speed);
        var space = text.IndexOf(' ');
        (_speedNumber.Text, _speedUnit.Text) = space > 0 ? (text[..space], text[(space + 1)..]) : ("0", "KB/s");
        _speedNumber.Foreground = UiKit.Brush(active.Count > 0 ? "Text" : "Text3");
        var connections = active.Sum(i => i.ActiveConnections);
        var waiting = _engine.Items.Count(i => i.State == DownloadState.Queued);
        _speedNote.Text = active.Count == 0 ? (waiting > 0 ? $"{waiting} waiting" : "Idle")
            : $"{active.Count} downloading{(waiting > 0 ? $", {waiting} waiting" : "")}\n{connections} connection{(connections == 1 ? "" : "s")}";
    }

    private void Submit()
    {
        var text = _url.Text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            _url.Foreground = UiKit.Brush("Danger");
            _field.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(420),
                KeyFrames = { new LinearDoubleKeyFrame(0.4, TimeSpan.FromMilliseconds(80)), new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(220)) },
            });
            return;
        }
        _url.Clear();
        _engine.Add(new DownloadRequest(u.ToString()));
    }

    private void ClearFinished()
    {
        foreach (var item in _engine.Items.Where(i => i.State is DownloadState.Completed or DownloadState.Cancelled or DownloadState.Failed).ToList())
            _engine.Remove(item);
    }

    /// <summary>For tests: the downloads in the order the list shows them, top first.</summary>
    internal IReadOnlyList<DownloadItem> Shown => _list.Children.OfType<DownloadRow>().Select(r => r.Item).ToList();

    private void AddRow(DownloadItem item, bool animate)
    {
        var row = new DownloadRow(item, _engine);
        _rows[item] = row;
        _list.Children.Insert(0, row);
        if (animate) row.AnimateIn();
        _list.Children.Remove(_empty);
    }

    private void OnAdded(DownloadItem item) => Dispatcher.BeginInvoke(() =>
    {
        if (_rows.ContainsKey(item)) return;
        AddRow(item, animate: true);
        UpdateSummary();
    });

    private void OnRemoved(DownloadItem item) => Dispatcher.BeginInvoke(() =>
    {
        if (!_rows.Remove(item, out var row)) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => { _list.Children.Remove(row); UpdateSummary(); };
        row.BeginAnimation(OpacityProperty, fade);
    });

    private void OnChanged(DownloadItem item) => Dispatcher.BeginInvoke(() =>
    {
        if (_rows.TryGetValue(item, out var row)) row.Refresh();
        UpdateSummary();
    });

    private void UpdateSummary()
    {
        var items = _engine.Items;
        var running = items.Count(i => i.State is DownloadState.Downloading or DownloadState.Queued or DownloadState.Paused);
        var finished = items.Count(i => i.State == DownloadState.Completed);
        _summary.Text = items.Count == 0 ? "" : running > 0 ? $"In progress · {running}{(finished > 0 ? $"   Finished · {finished}" : "")}" : $"Finished · {finished}";
        _clear.Visibility = items.Any(i => i.State is DownloadState.Completed or DownloadState.Cancelled or DownloadState.Failed) ? Visibility.Visible : Visibility.Collapsed;
        if (_rows.Count == 0 && !_list.Children.Contains(_empty)) _list.Children.Add(_empty);
    }
}
