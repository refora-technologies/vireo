using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Vireo.Services;

/// <summary>
/// How Vireo moves things on screen, cheaply enough for a low-end laptop.
/// <list type="bullet">
/// <item>Only opacity and render transforms are animated: the graphics card moves an already drawn picture, nothing is laid
/// out again. (Animating a width re-lays out everything beside it every frame; next to a web page, that means the page too.)</item>
/// <item>While something moves it is kept as a cached picture, so a soft shadow or a long list is drawn once, not every frame;
/// the cache is dropped when it stops, so text is crisp again.</item>
/// <item>Without graphics acceleration (software drawing) animations run at a steady 30 frames a second rather than an
/// uneven 60.</item>
/// </list>
/// </summary>
public static class Motion
{
    private static readonly ConditionalWeakTable<UIElement, object> Current = new();

    /// <summary>Windows draws without a graphics card (rendering tier 0): the slowest machines.</summary>
    public static bool SoftwareDrawing => RenderCapability.Tier >> 16 == 0;

    /// <summary>Call once at start: a steady 30 frames a second where the machine cannot keep 60.</summary>
    public static void TuneForThisMachine()
    {
        if (!SoftwareDrawing) return;
        try { Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = 30 }); }
        catch (ArgumentException) { /* already set */ }
    }

    public static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(ms));

    /// <summary>
    /// Slides <paramref name="element"/> (through <paramref name="slide"/>) and fades it, drawn as a cached picture while it
    /// moves. A newer call on the same element takes over; the older one's <paramref name="done"/> is then never run.
    /// </summary>
    public static void Run(UIElement element, TranslateTransform slide, double fromX, double toX, double fromOpacity, double toOpacity,
        Duration duration, IEasingFunction ease, Action? done = null, double fromY = 0, double toY = 0)
    {
        var token = new object();
        Current.AddOrUpdate(element, token);
        var keep = element.CacheMode is BitmapCache ? null : element.CacheMode;
        element.CacheMode = new BitmapCache { EnableClearType = true, SnapsToDevicePixels = true };

        var fade = new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (!Current.TryGetValue(element, out var now) || now != token) return; // a newer move took over
            // Settle on the end values without animations holding them, and draw crisply again.
            element.BeginAnimation(UIElement.OpacityProperty, null);
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            element.Opacity = toOpacity;
            slide.X = toX;
            slide.Y = toY;
            element.CacheMode = keep;
            Current.Remove(element);
            done?.Invoke();
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        if (fromX != toX || slide.X != toX) slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, toX, duration) { EasingFunction = ease });
        if (fromY != toY || slide.Y != toY) slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, toY, duration) { EasingFunction = ease });
    }

    /// <summary>Keeps <paramref name="element"/> a cached picture for <paramref name="forMs"/> (an animation on it that is not a slide).</summary>
    public static void CacheWhileMoving(UIElement element, double forMs)
    {
        var keep = element.CacheMode is BitmapCache ? null : element.CacheMode;
        element.CacheMode = new BitmapCache { EnableClearType = true, SnapsToDevicePixels = true };
        var token = new object();
        Current.AddOrUpdate(element, token);
        _ = Task.Delay(TimeSpan.FromMilliseconds(forMs + 30)).ContinueWith(_ => element.Dispatcher.BeginInvoke(() =>
        {
            if (!Current.TryGetValue(element, out var now) || now != token) return;
            element.CacheMode = keep;
            Current.Remove(element);
        }), TaskScheduler.Default);
    }
}
