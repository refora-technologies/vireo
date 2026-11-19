using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Vireo.Controls;
using Vireo.Downloads;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

// Downloads started by pages are taken over by Vireo's own download engine.
public partial class MainWindow
{
    private DownloadEngine? _downloads;
    private Popup? _toast;
    private DispatcherTimer? _toastTimer;
    private readonly Dictionary<DownloadItem, DownloadState> _lastItemStates = new();

    // A private window's list is kept in memory only: no record of what it downloaded is written anywhere.
    public DownloadEngine Downloads => _downloads ??= _private
        ? new DownloadEngine(null, OptionsFromSettings()) { ChooseFolder = PrivateDownloadFolder }
        : SharedDownloads(_dataFolder, _settings);

    /// <summary>The profile's own settings (a private window's copy is never saved), for the choices a private window keeps.</summary>
    private Settings SavedSettings => s_settings ?? (_savedSettings ??= Settings.Load(_dataFolder));
    private Settings? _savedSettings;

    /// <summary>For tests: answers the "where should private downloads go?" question instead of a person.</summary>
    internal static Func<string?>? AnswerPrivateFolderQuestion;
    internal int PrivateFolderQuestionsAsked { get; private set; }

    /// <summary>Where this private window's downloads go: asked the first time ever, then always the same place.</summary>
    private string? PrivateDownloadFolder()
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(PrivateDownloadFolder);
        var saved = SavedSettings;
        var choice = saved.PrivateDownloadFolder;
        if (choice.Length == 0)
        {
            PrivateFolderQuestionsAsked++;
            if (AnswerPrivateFolderQuestion is { } answer) choice = answer() ?? "";
            else
            {
                var ask = new Controls.PrivateFolderDialog(this, saved.DownloadFolder);
                ask.ShowDialog();
                choice = ask.Choice ?? "";
            }
            if (choice.Length == 0) return null; // "Not now": the normal folder this time, asked again next time
            saved.PrivateDownloadFolder = choice;
            saved.Commit();
        }
        if (choice == Controls.PrivateFolderDialog.SameAsNormal) return saved.DownloadFolder;
        try { Directory.CreateDirectory(choice); return choice; }
        catch { return saved.DownloadFolder; } // the drive is gone (a USB stick taken out): the normal folder rather than failing
    }

    private DownloadOptions OptionsFromSettings() => OptionsFrom(_settings);

    private void StartDownloads()
    {
        _ = Downloads;
        foreach (var i in Downloads.Items) _lastItemStates[i] = i.State;
        // A private window's own engine follows the settings here; the shared one follows them by itself (SharedDownloads).
        if (_private) _settings.Changed += () => ApplyOptions(Downloads.Options, _settings);
        void Changed(DownloadItem i) => Dispatcher.BeginInvoke(() => OnDownloadItemChanged(i), DispatcherPriority.Background);
        void Added(DownloadItem i) => Dispatcher.BeginInvoke(() => OnDownloadItemAdded(i), DispatcherPriority.Background);
        void Removed(DownloadItem i) => Dispatcher.BeginInvoke(() => OnDownloadItemRemoved(i), DispatcherPriority.Background);
        Downloads.ItemChanged += Changed;
        Downloads.ItemAdded += Added;
        Downloads.ItemRemoved += Removed;
        Closed += (_, _) =>
        {
            Downloads.ItemChanged -= Changed;
            Downloads.ItemAdded -= Added;
            Downloads.ItemRemoved -= Removed;
            if (_private) _downloads?.Dispose(); // a normal window's engine is shared: the last window closes it
        };
        UpdateDownloadsButton();
    }

    private void OnDownloadItemChanged(DownloadItem item)
    {
        using var _crumb = Breadcrumbs.Scope("download changed");
        UpdateDownloadsButton();
        var prev = _lastItemStates.TryGetValue(item, out var s) ? s : DownloadState.Queued;
        _lastItemStates[item] = item.State;
        if (prev != item.State)
        {
            // With several windows open, the notice appears once: in the window in front.
            if (!_private && LastActive is { } front && front != this) return;
            if (item.State == DownloadState.Completed)
            {
                var path = item.FinalPath;
                ShowToast("Download complete", item.FileName, ShowDownloads, actions: [("Open", () => Format.Open(path)), ("Show in folder", () => Format.ShowInFolder(path))]);
            }
            else if (item.State == DownloadState.Failed && item.Parent is null)
            {
                ShowToast("Download failed", item.FileName.Length > 0 ? item.FileName : item.Url, ShowDownloads, isError: true);
            }
        }
    }

    private void OnDownloadItemAdded(DownloadItem item)
    {
        _lastItemStates[item] = item.State;
        UpdateDownloadsButton();
    }

    private void OnDownloadItemRemoved(DownloadItem item)
    {
        _lastItemStates.Remove(item);
        UpdateDownloadsButton();
    }

    internal void ShowDownloads() => OpenDrawer("Downloads", new DownloadsPanel(Downloads));

    /// <summary>The toolbar button shows how many downloads are running, and the speed on hover.</summary>
    private void UpdateDownloadsButton()
    {
        if (_downloads is null) return;
        var active = _downloads.Items.Where(i => i.State is DownloadState.Downloading or DownloadState.Queued).ToList();
        var speed = active.Sum(i => i.Speed);
        DownloadBadge.Text = active.Count.ToString();
        var wasHidden = DownloadBadgeHost.Visibility != Visibility.Visible;
        DownloadBadgeHost.Visibility = active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (active.Count > 0 && wasHidden) PopIn(DownloadBadgeHost);
        DownloadsButton.ToolTip = active.Count > 0 ? $"Downloads · {active.Count} active · {Format.Speed(speed)}" : "Downloads (Ctrl+J)";
        // The ring: how far all running downloads together have got (spinning while their sizes are not known yet)
        var sized = active.Where(i => i.Total > 0).ToList();
        DownloadRing.Visibility = active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadRing.Value = sized.Count == 0 ? -1 : sized.Sum(i => (double)i.Downloaded) / Math.Max(1, sized.Sum(i => (double)i.Total));
        DownloadRing.InvalidateVisual();
    }

    private static void PopIn(FrameworkElement e)
    {
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(0.4, 0.4);
        e.RenderTransform = scale;
        var anim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    // ---- A page started a download ----

    private readonly List<(string Site, DateTime At)> _autoDownloads = [];

    private void OnDownloadStarting(BrowserTab tab, CoreWebView2 core, CoreWebView2DownloadStartingEventArgs e)
    {
        var op = e.DownloadOperation;
        // Only plain web addresses can be fetched again by the engine; leave blob: and data: to the browser.
        if (!Uri.TryCreate(op.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        // A site that starts download after download by itself is held back after the third in a minute, as other browsers ask first.
        var site = Blocking.FilterEngine.SiteOf(HostOnly(core.Source));
        var now = DateTime.UtcNow;
        _autoDownloads.RemoveAll(x => now - x.At > TimeSpan.FromMinutes(1));
        if (_autoDownloads.Count(x => x.Site == site) >= 3 && !_settings.AutoDownloadSites.Contains(site))
        {
            e.Cancel = true;
            e.Handled = true;
            ShowToast("Downloads blocked", $"{site} kept starting downloads. Click to allow it.", () =>
            {
                _settings.AutoDownloadSites.Add(site);
                _settings.Commit();
            }, isError: true);
            return;
        }
        _autoDownloads.Add((site, now));
        e.Cancel = true;
        e.Handled = true;
        var name = Path.GetFileName(e.ResultFilePath);
        var referer = core.Source;
        var agent = core.Settings.UserAgent;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            // The page's own cookies go along, so downloads from signed-in sites work.
            string? cookie = null;
            try { cookie = string.Join("; ", (await core.CookieManager.GetCookiesAsync(uri.ToString())).Select(c => $"{c.Name}={c.Value}")); } catch { /* download without cookies */ }
            var item = Downloads.Add(new DownloadRequest(uri.ToString(), string.IsNullOrWhiteSpace(name) ? null : name, referer, agent, string.IsNullOrEmpty(cookie) ? null : cookie), out var continued);
            // The same file again while it is unfinished: the download carries on, no second copy.
            if (continued) ShowToast("Carrying on", $"{item.FileName} continues from where it stopped", ShowDownloads);
            else ShowToast("Downloading", string.IsNullOrEmpty(name) ? uri.Host : name, ShowDownloads);

            // Clean tab management: if this tab was opened solely to trigger the download (e.g. target="_blank")
            // and has no navigation history, close the orphaned blank tab.
            if (!core.CanGoBack && !core.CanGoForward && _tabs.Count > 1 && (tab.IsStartPage || string.IsNullOrWhiteSpace(tab.Url) || tab.Url == uri.ToString()))
            {
                await CloseTabAsync(tab);
            }
        });
    }

    // ---- A small notice under the toolbar ----

    /// <summary>The latest notice that came while another program was in front: shown when Vireo is brought back.</summary>
    private (string Title, string Detail, Action? Click, bool IsError, (string Label, Action Do)[]? Actions)? _waitingToast;
    private bool _toastHooks;
    /// <summary>For tests: whether a notice is on screen now, and whether one is waiting for the window to come forward.</summary>
    internal bool ToastShowing => _toast is { IsOpen: true };
    internal bool ToastWaiting => _waitingToast is not null;
    /// <summary>For tests: behaves as if another program were in front (a test window is not always the foreground one).</summary>
    internal bool? ForegroundForTest;

    private bool IsForeground => ForegroundForTest ?? (IsActive && WindowState != WindowState.Minimized);

    /// <summary>
    /// A notice at the bottom right of the page. <paramref name="click"/> is what clicking it does; <paramref name="actions"/>
    /// are buttons on it ("Open", "Show in folder").
    /// </summary>
    internal void ShowToast(string title, string detail, Action? click = null, bool isError = false, (string Label, Action Do)[]? actions = null)
    {
        if (!_toastHooks)
        {
            _toastHooks = true;
            // Another program comes in front: the notice goes at once (it must never sit over someone else's window).
            Deactivated += (_, _) => { if (ForegroundForTest is null) HideToast(); };
            // Back to Vireo: what finished meanwhile is shown now.
            Activated += (_, _) => ShowWaitingToast();
        }
        // Vireo is behind another program, or minimized: no popup over their work. The taskbar button lights up instead,
        // and the notice waits until Vireo is brought forward.
        if (!IsForeground)
        {
            _waitingToast = (title, detail, click, isError, actions);
            Native.FlashTaskbar(this);
            return;
        }
        _toastTimer?.Stop();
        // Bottom-right of the page, the way notices from the system arrive; it never covers the toolbar or the drawer's title.
        _toast ??= new Popup { AllowsTransparency = true, StaysOpen = true, PopupAnimation = PopupAnimation.None, Placement = PlacementMode.Relative, PlacementTarget = Pages };
        var text = new StackPanel { Margin = new Thickness(12, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 11.5, Foreground = UiKit.Brush(isError ? "Danger" : "Text3") });
        text.Children.Add(new TextBlock { Text = detail, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 13, MaxWidth = 250, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = UiKit.Brush("Text") });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var iconCode = isError ? 0xE711
            : title.StartsWith("Downloading", StringComparison.Ordinal) || title.StartsWith("Recording", StringComparison.Ordinal) ? 0xE896
            : title.Contains("blocked", StringComparison.OrdinalIgnoreCase) ? 0xEA18
            : title.Contains("Extension", StringComparison.OrdinalIgnoreCase) ? 0xEA86
            : 0xE73E; // done, added, cleared, saved
        var iconFg = isError ? "Danger" : iconCode == 0xE73E ? "Info" : "Accent";
        row.Children.Add(new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), Background = UiKit.Brush("Surface3"), Child = UiKit.Icon(iconCode, 14, iconFg) });
        row.Children.Add(text);
        if (actions is { Length: > 0 })
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, -6, 0) };
            foreach (var (label, act) in actions)
            {
                var b = new Button { Content = label, Style = (Style)FindResource("GhostButton"), FontSize = 12, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(2, 0, 0, 0) };
                b.Click += (_, e) => { e.Handled = true; _toast!.IsOpen = false; act(); };
                buttons.Children.Add(b);
            }
            row.Children.Add(buttons);
        }
        var card = new Border
        {
            Margin = new Thickness(18), Padding = new Thickness(8, 8, 14, 8), CornerRadius = new CornerRadius(8), Background = UiKit.Brush("Surface2"), Cursor = System.Windows.Input.Cursors.Hand,
            BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), Child = row, Opacity = 0, RenderTransform = new TranslateTransform(0, 8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.5, Color = Colors.Black },
        };
        card.MouseLeftButtonUp += (_, _) => { _toast!.IsOpen = false; click?.Invoke(); }; // a button on it handles its own click
        _toast.Child = card;
        // Measured here, in the window's own units, so the corner is exact whatever the screen's scaling.
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _toast.IsOpen = false;
        _toast.HorizontalOffset = Math.Max(0, Pages.ActualWidth - card.DesiredSize.Width);
        _toast.VerticalOffset = Math.Max(0, Pages.ActualHeight - card.DesiredSize.Height);
        _toast.IsOpen = true;
        Native.KeepJustAbove(_toast, this);
        Motion.CacheWhileMoving(card, 280); // its shadow is drawn once while it slides in
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        card.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        // A notice with buttons stays longer, so there is time to use them (and while the pointer is on it).
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(actions is { Length: > 0 } ? 7 : 3.5) };
        card.MouseEnter += (_, _) => _toastTimer?.Stop();
        card.MouseLeave += (_, _) => _toastTimer?.Start();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer!.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => _toast.IsOpen = false;
            Motion.CacheWhileMoving(card, 220);
            card.BeginAnimation(OpacityProperty, fade);
        };
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer?.Stop();
        if (_toast is not null) _toast.IsOpen = false;
    }

    internal void ShowWaitingToastForTest() => ShowWaitingToast();

    private void ShowWaitingToast()
    {
        if (_waitingToast is not { } t) return;
        _waitingToast = null;
        // After the window has settled in front (activation comes before it is drawn there).
        Dispatcher.BeginInvoke(() => ShowToast(t.Title, t.Detail, t.Click, t.IsError, t.Actions), DispatcherPriority.ApplicationIdle);
    }
}
