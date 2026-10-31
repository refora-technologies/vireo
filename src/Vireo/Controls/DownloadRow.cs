using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vireo.Downloads;
using Vireo.Services;

namespace Vireo.Controls;

/// <summary>
/// One transfer in the list: the file's type, its name and percentage, the bar (one stretch per connection),
/// and a status line in figures. The buttons that fit its state sit on the right.
/// </summary>
public sealed class DownloadRow : Border
{
    private readonly DownloadItem _item;
    private readonly DownloadEngine _engine;
    private readonly TextBlock _name = new() { FontSize = 13, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pct = new() { FontSize = 12, FontFamily = UiKit.Mono, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 11.5, FontFamily = UiKit.Mono, Foreground = UiKit.Brush("Text3"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
    private readonly SegmentBar _bar = new() { Margin = new Thickness(0, 7, 0, 0) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly ContentControl _badge = new() { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0), Focusable = false };
    private DownloadState? _shownState;
    private string _badgeKey = "";

    public DownloadItem Item => _item;

    public DownloadRow(DownloadItem item, DownloadEngine engine)
    {
        _item = item;
        _engine = engine;
        Background = Brushes.Transparent;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(10, 10, 6, 10);
        Margin = new Thickness(0, 0, 0, 2);
        RenderTransformOrigin = new Point(0.5, 0.5);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(_name);
        Grid.SetColumn(_pct, 1);
        title.Children.Add(_pct);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(_bar);
        text.Children.Add(_status);
        _bar.Item = item;

        Grid.SetColumn(text, 1);
        Grid.SetColumn(_buttons, 2);
        grid.Children.Add(_badge);
        grid.Children.Add(text);
        grid.Children.Add(_buttons);
        Child = grid;

        MouseEnter += (_, _) => Background = UiKit.Brush("Hover");
        MouseLeave += (_, _) => Background = Brushes.Transparent;
        MouseLeftButtonUp += (_, e) => { if (item.State == DownloadState.Completed && e.OriginalSource is not Button) Open(); };
        Refresh();
    }

    /// <summary>Slides and fades the row in, for a download that has just started.</summary>
    public void AnimateIn()
    {
        Opacity = 0;
        RenderTransform = new TranslateTransform(0, 8);
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    public void Refresh()
    {
        var i = _item;
        _name.Text = string.IsNullOrEmpty(i.FileName) ? i.Url : i.FileName;
        _name.ToolTip = i.State == DownloadState.Completed ? i.FinalPath : i.Url;
        _name.Foreground = UiKit.Brush(i.State is DownloadState.Cancelled ? "Text3" : "Text");

        // The badge says what kind of file; its letters turn green when done and red when it failed.
        var tone = i.State switch { DownloadState.Completed => "Info", DownloadState.Failed => "Danger", DownloadState.Cancelled => "Text3", _ => "Text2" };
        var key = i.FileName + "|" + tone;
        if (key != _badgeKey) { _badgeKey = key; _badge.Content = UiKit.TypeBadge(i.FileName, tone); }

        var fraction = i.State == DownloadState.Completed ? 1 : i.Total > 0 ? Math.Clamp((double)i.Downloaded / i.Total, 0, 1) : -1;
        _pct.Text = i.State is DownloadState.Downloading or DownloadState.Paused && fraction >= 0 ? $"{fraction * 100:0}%" : "";
        _pct.Foreground = UiKit.Brush(i.State == DownloadState.Downloading ? "Accent" : "Text3");

        _status.Text = StatusText(i);
        _status.Foreground = UiKit.Brush(i.State == DownloadState.Failed ? "Danger" : "Text3");
        _status.FontFamily = i.State == DownloadState.Failed ? (FontFamily)Application.Current.FindResource("UiFont") : UiKit.Mono;
        // Finished and cancelled rows need no bar: the green letters say it.
        _bar.Visibility = i.State is DownloadState.Cancelled or DownloadState.Completed ? Visibility.Collapsed : Visibility.Visible;
        _bar.Refresh();
        if (_shownState != i.State) { _shownState = i.State; BuildButtons(); }
    }

    private static string StatusText(DownloadItem i)
    {
        var size = i.Total > 0 ? Format.Bytes(i.Total) : "";
        switch (i.State)
        {
            case DownloadState.Completed:
                return $"{size}{(size.Length > 0 ? " · " : "")}{Format.When(i.CompletedAt ?? i.CreatedAt)}";
            case DownloadState.Failed:
                return i.Error ?? "Failed";
            case DownloadState.Cancelled:
                return "Cancelled";
            case DownloadState.Paused:
                return $"Paused · {Format.Bytes(i.Downloaded)}{(i.Total > 0 ? " / " + size : "")}{(i.CanResume || i.Total < 0 ? "" : " · restarts from the beginning")}";
            case DownloadState.Queued:
                return "Waiting for a free slot";
            default:
            {
                var parts = new List<string> { $"{Format.Bytes(i.Downloaded)}{(i.Total > 0 ? " / " + size : "")}" };
                if (i.Speed > 1) parts.Add(Format.Speed(i.Speed));
                var eta = Format.Eta(i.Eta);
                if (eta.Length > 0) parts.Add(eta);
                if (i.ActiveConnections > 1) parts.Add($"×{i.ActiveConnections}");
                return string.Join(" · ", parts);
            }
        }
    }

    private void BuildButtons()
    {
        _buttons.Children.Clear();
        var i = _item;
        switch (i.State)
        {
            case DownloadState.Downloading:
            case DownloadState.Queued:
                Add(0xE769, "Pause", () => _engine.Pause(i));
                Add(0xE711, "Cancel", () => _engine.Cancel(i));
                break;
            case DownloadState.Paused:
                Add(0xE768, "Resume", () => _engine.Resume(i));
                Add(0xE711, "Cancel", () => _engine.Cancel(i));
                break;
            case DownloadState.Completed:
                Add(0xE838, "Show in folder", ShowInFolder);
                Add(0xE74D, "Remove from the list (the file stays)", () => _engine.Remove(i));
                break;
            case DownloadState.Failed:
                Add(0xE72C, "Try again", () => _engine.Resume(i));
                Add(0xE74D, "Remove from the list", () => _engine.Remove(i));
                break;
            default:
                Add(0xE74D, "Remove from the list", () => _engine.Remove(i));
                break;
        }
    }

    private void Add(int glyph, string tip, Action click)
    {
        var b = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = ((char)glyph).ToString(), ToolTip = tip, Width = 28, Height = 28, FontSize = 12 };
        b.Click += (_, _) => click();
        _buttons.Children.Add(b);
    }

    private void Open()
    {
        try
        {
            if (File.Exists(_item.FinalPath))
            {
                Process.Start(new ProcessStartInfo(_item.FinalPath) { UseShellExecute = true });
            }
            else if (Directory.Exists(_item.Folder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", _item.Folder) { UseShellExecute = true });
            }
        }
        catch { /* no program for this type */ }
    }

    private void ShowInFolder()
    {
        try
        {
            if (File.Exists(_item.FinalPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_item.FinalPath}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(_item.Folder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", _item.Folder) { UseShellExecute = true });
            }
        }
        catch { /* explorer unavailable */ }
    }
}
