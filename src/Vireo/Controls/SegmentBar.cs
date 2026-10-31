using System.Windows;
using System.Windows.Media;
using Vireo.Downloads;

namespace Vireo.Controls;

/// <summary>
/// A progress bar that shows the file the way the engine sees it: each stretch being fetched by its
/// own connection fills from its own start, and the front of every active stretch glows.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    private DownloadItem? _item;
    // Track and fills follow the theme: teal while running, grey when paused, light teal when done, red when failed.
    private static readonly Brush Track = new SolidColorBrush(Color.FromRgb(0x2A, 0x36, 0x34));
    private static readonly Brush Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0xDB, 0xB9));
    private static readonly Brush Done = new SolidColorBrush(Color.FromRgb(0x5F, 0xE6, 0xC8));
    private static readonly Brush Head = new SolidColorBrush(Color.FromRgb(0xC4, 0xF7, 0xEA));
    private static readonly Brush Paused = new SolidColorBrush(Color.FromRgb(0x6B, 0x7C, 0x78));
    private static readonly Brush Failed = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

    static SegmentBar()
    {
        foreach (var b in new[] { Track, Fill, Done, Head, Paused, Failed }) b.Freeze();
    }

    public SegmentBar() => Height = 4;

    public DownloadItem? Item
    {
        get => _item;
        set { _item = value; InvalidateVisual(); }
    }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var round = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), round, round);
        var item = _item;
        if (item is null) return;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), round, round));
        if (item.State == DownloadState.Completed)
        {
            dc.DrawRectangle(Done, null, new Rect(0, 0, w, h));
        }
        else if (item.Total > 0)
        {
            var fill = item.State switch { DownloadState.Paused => Paused, DownloadState.Failed => Failed, _ => Fill };
            List<(long Start, long Done, bool Active)> parts;
            lock (item.Lock)
            {
                if (item.Segments.Count > 0)
                    parts = item.Segments.Select(s => (s.Start, s.Done, s.Active && s.Remaining > 0)).ToList();
                else
                    parts = [(0, Interlocked.Read(ref item.StreamBytes), false)];
            }
            foreach (var (start, done, active) in parts)
            {
                var x = start * w / item.Total;
                var width = Math.Max(0, (done - start) * w / item.Total);
                if (width < 0.5) continue;
                dc.DrawRectangle(fill, null, new Rect(x, 0, width, h));
                if (active && item.State == DownloadState.Downloading) dc.DrawRectangle(Head, null, new Rect(Math.Max(x, x + width - 3), 0, Math.Min(3, width), h));
            }
        }
        dc.Pop();
    }
}
