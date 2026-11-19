using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Vireo.Downloads;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

// Several windows in one Vireo: they share one set of everything (settings, history and bookmarks, the download engine,
// the browser engine and the filter lists), and a tab can be moved from one window to another with its page alive.
public partial class MainWindow
{
    /// <summary>The normal (not private) windows, in the order they were opened. The first one owns the saved session.</summary>
    private static readonly List<MainWindow> s_windows = [];
    private static Settings? s_settings;
    private static Library? s_library;
    private static DownloadEngine? s_downloads;
    private static Task<CoreWebView2Environment>? s_env;

    /// <summary>The window most recently in front: where links from other programs and download notices go.</summary>
    internal static MainWindow? LastActive { get; private set; }

    internal static IReadOnlyList<MainWindow> NormalWindows => s_windows;

    /// <summary>What a newly opened window does once it is ready (show a tab it was given, bring back a saved window, ...).</summary>
    private Func<MainWindow, Task>? _fill;

    private bool IsFirstWindow => !_private && s_windows.Count > 0 && s_windows[0] == this;

    private void JoinWindows()
    {
        Activated += (_, _) => { if (!_private) LastActive = this; };
        if (_private) return;
        s_windows.Add(this);
        LastActive ??= this;
        Closed += (_, _) =>
        {
            var wasFirst = s_windows.Count > 0 && s_windows[0] == this;
            if (wasFirst) SaveSession(); // the session is saved by the first window, with every window in it
            s_windows.Remove(this);
            if (LastActive == this) LastActive = s_windows.LastOrDefault();
            if (s_windows.Count > 0)
            {
                // The next window takes over saving the session.
                if (wasFirst) s_windows[0].StartSession();
                return;
            }
            // The last window: the shared things go away with it (and are made afresh if a window opens again in this process).
            s_library?.Dispose();
            s_library = null;
            if (s_keepDownloading && s_downloads is { } engine) { EnterBackground(engine); return; } // the downloads go on
            LeaveTray();
            s_downloads?.Dispose();
            s_downloads = null;
        };
    }

    /// <summary>One environment for every window of this profile (the engine allows only one set of options per folder).</summary>
    private Task<CoreWebView2Environment> EnvironmentFor(string data, bool lowEnd)
    {
        var arguments = BrowserArguments(lowEnd);
        return s_env ??= Create();
        async Task<CoreWebView2Environment> Create()
        {
            var options = new CoreWebView2EnvironmentOptions(arguments) { AreBrowserExtensionsEnabled = true };
            return await CoreWebView2Environment.CreateAsync(null, data, options);
        }
    }

    /// <summary>A new normal window beside this one (or at <paramref name="at"/>, in screen units), doing <paramref name="fill"/> once ready.</summary>
    internal MainWindow OpenWindow(Func<MainWindow, Task>? fill = null, Point? at = null)
    {
        var win = new MainWindow(false) { _fill = fill, Width = Width, Height = Height, WindowStartupLocation = WindowStartupLocation.Manual };
        if (at is { } p) { win.Left = p.X; win.Top = p.Y; }
        else { win.Left = Left + 36; win.Top = Top + 36; }
        if (WindowState == WindowState.Maximized && at is null) win.WindowState = WindowState.Maximized;
        win.Show();
        return win;
    }

    private void OnMenuNewWindow(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; OpenWindow(); }

    // ---- Moving a tab to a window of its own ----

    /// <summary>
    /// Takes the tab out of this window into a new one, with its page as it is (scroll position, what was typed, back and forward
    /// history). <paramref name="screen"/> is where the mouse let go, in screen pixels.
    /// </summary>
    internal async Task<MainWindow?> MoveToNewWindowAsync(BrowserTab tab, Point? screen = null)
    {
        if (_private || !_tabs.Contains(tab) || _tabs.Count < 2) return null;
        Point? at = null;
        if (screen is { } s)
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            at = new Point(s.X / dpi.DpiScaleX - 120, s.Y / dpi.DpiScaleY - 18); // the tab lands under the mouse
        }
        var done = new TaskCompletionSource<MainWindow>();
        OpenWindow(async w => { await w.AdoptAsync(tab, this); done.TrySetResult(w); }, at);
        return await done.Task;
    }

    /// <summary>Takes a tab (and its page) from another window.</summary>
    private async Task AdoptAsync(BrowserTab tab, MainWindow from)
    {
        await from.ReleaseTabAsync(tab);
        tab.Owner = this;
        _tabs.Add(tab);
        AttachHeader(tab);
        if (tab.View is { } view) Pages.Children.Add(view);
        await ActivateAsync(tab);
        SaveAnySession();
    }

    /// <summary>Lets go of a tab that moves to another window: its page is kept alive, only taken out of this window.</summary>
    private async Task ReleaseTabAsync(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        if (Active == tab && _tabs.Count > 1)
        {
            var next = index + 1 < _tabs.Count ? _tabs[index + 1] : _tabs[index - 1];
            await ActivateAsync(next);
        }
        _tabs.Remove(tab);
        if (_headers.Remove(tab, out var header)) Tabs.Children.Remove(header);
        if (tab.View is { } view) Pages.Children.Remove(view);
        if (Active == tab) Active = null;
    }

    // ---- Dragging a tab between windows ----

    private Window? _ghost;
    private System.Windows.Controls.Image? _ghostPicture;
    private BrowserTab? _ghostTab;

    /// <summary>While a tab is dragged away from the strip, a small picture of it follows the mouse.</summary>
    private void OnTabDragMoving(BrowserTab tab, Point screen, bool outside)
    {
        if (!outside || _private) { HideGhost(); return; }
        if (_ghost is null || _ghostTab != tab) ShowGhost(tab);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        _ghost!.Left = screen.X / dpi.DpiScaleX - 24;
        _ghost.Top = screen.Y / dpi.DpiScaleY - 18;
    }

    private void ShowGhost(BrowserTab tab)
    {
        HideGhost();
        _ghostTab = tab;
        _ghostPicture = new System.Windows.Controls.Image { Width = 240, Height = 135, Stretch = System.Windows.Media.Stretch.UniformToFill };
        var title = new System.Windows.Controls.TextBlock { Text = tab.DisplayTitle, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 7, 10, 8), MaxWidth = 220 };
        var card = new System.Windows.Controls.StackPanel();
        card.Children.Add(new System.Windows.Controls.Border { CornerRadius = new CornerRadius(9, 9, 0, 0), ClipToBounds = true, Background = UiKit.Brush("Surface1"), Child = _ghostPicture, Height = 135, Width = 240 });
        card.Children.Add(title);
        _ghost = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, Topmost = true,
            ShowActivated = false, Focusable = false, IsHitTestVisible = false, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = this,
            Content = new System.Windows.Controls.Border
            {
                Margin = new Thickness(16), CornerRadius = new CornerRadius(10), Background = UiKit.Brush("Surface2"), BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1),
                Opacity = 0.94, Child = card, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.5 },
            },
        };
        _ghost.Show();
        _ = PictureOfAsync(tab);
    }

    /// <summary>A small picture of the page for the dragged tab (it arrives a moment later; the title shows meanwhile).</summary>
    private async Task PictureOfAsync(BrowserTab tab)
    {
        if (tab.View?.CoreWebView2 is not { } core) return;
        try
        {
            using var stream = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
            stream.Position = 0;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 480;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            if (_ghostTab == tab && _ghostPicture is not null) _ghostPicture.Source = image;
        }
        catch { /* the title alone will do */ }
    }

    internal void PreviewTabDrag(BrowserTab tab, Point? screen) { if (screen is { } p) OnTabDragMoving(tab, p, true); else HideGhost(); }
    internal bool DragPreviewShown => _ghost is { IsVisible: true };

    private void HideGhost()
    {
        _ghost?.Close();
        _ghost = null;
        _ghostPicture = null;
        _ghostTab = null;
    }

    /// <summary>
    /// A tab let go away from its strip: onto another window's tabs, it joins that window there (and a window left with no
    /// tabs closes); anywhere else, it gets a window of its own.
    /// </summary>
    private async Task OnTabDragEndedAsync(BrowserTab tab, Point screen, bool outside)
    {
        HideGhost();
        if (!outside || _private || !_tabs.Contains(tab)) return;
        if (WindowWithTabsAt(screen) is { } target) await DropTabAsync(tab, target, screen);
        else if (_tabs.Count > 1) await MoveToNewWindowAsync(tab, screen);
    }

    /// <summary>The other normal window whose tab strip is under this point of the screen (pixels).</summary>
    internal MainWindow? WindowWithTabsAt(Point screen)
    {
        foreach (var w in s_windows.ToList())
        {
            if (w == this || !w.IsVisible || w.WindowState == WindowState.Minimized) continue;
            if (w.TabStripScreenRect() is { } r && r.Contains(screen)) return w;
        }
        return null;
    }

    /// <summary>Where this window's tabs are on the screen, in pixels, with a little room around them.</summary>
    internal Rect? TabStripScreenRect()
    {
        if (!Tabs.IsVisible || Tabs.ActualWidth < 1) return null;
        var topLeft = Tabs.PointToScreen(new Point(0, 0));
        var bottomRight = Tabs.PointToScreen(new Point(Tabs.ActualWidth, Tabs.ActualHeight));
        var r = new Rect(topLeft, bottomRight);
        r.Inflate(24, 24);
        return r;
    }

    /// <summary>Moves a tab, page alive, into <paramref name="target"/>'s strip where it was dropped.</summary>
    internal async Task DropTabAsync(BrowserTab tab, MainWindow target, Point screen)
    {
        if (target._private || !_tabs.Contains(tab)) return;
        var lastOne = _tabs.Count == 1;
        await target.AdoptAsync(tab, this);
        // Its place among the target's tabs: where the mouse was.
        var at = target.Tabs.PointFromScreen(screen);
        target.DragTab(tab, at);
        target.Activate();
        if (lastOne) Close();
    }

    /// <summary>The session is written by the first window, whichever window changed.</summary>
    private void SaveAnySession()
    {
        if (s_windows.Count > 0) s_windows[0].SaveSession();
    }
}
