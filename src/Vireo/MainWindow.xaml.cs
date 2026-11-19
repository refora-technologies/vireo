using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vireo.Blocking;
using Vireo.Controls;
using Vireo.Downloads;
using Vireo.Models;
using Vireo.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Vireo;

public partial class MainWindow : Window
{
    private const string StartHost = "vireo.test";

    private readonly List<BrowserTab> _tabs = [];
    private readonly Dictionary<BrowserTab, TabHeader> _headers = [];
    private readonly Stack<string> _closed = new();
    private readonly DispatcherTimer _sweep = new() { Interval = TimeSpan.FromSeconds(5) };
    private Button _newTabButton = null!;
    private CoreWebView2Environment? _env;
    private bool _fullscreen;
    private WindowState _beforeFullscreen;
    private Storyboard? _loadStory;
    private string _dataFolder = "";
    private bool _lowEnd;
    internal UiWatchdog? Watchdog { get; private set; }

    public BrowserTab? Active { get; private set; }
    public IReadOnlyList<BrowserTab> AllTabs => _tabs;
    public Task Ready => _ready.Task;
    private readonly TaskCompletionSource _ready = new();

    private void Construct()
    {
        _newTabButton = new Button { Style = (Style)FindResource("IconButton"), Content = Glyph.Add, ToolTip = "New tab (Ctrl+T)" };
        _newTabButton.Click += async (_, _) => await OpenTabAsync(null);
        Tabs.Children.Add(_newTabButton);
        BuildSideChooser();

        SourceInitialized += (_, _) => Native.StyleWindow(this);
        StateChanged += (_, _) => OnStateChanged();
        PreviewKeyDown += (_, e) => { if (HandleShortcut(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)) e.Handled = true; };
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _sweep.Stop();
        SizeChanged += (_, _) => { if (_drawerOpen) Drawer.Width = TargetDrawerWidth; };
        _sweep.Tick += async (_, _) => await SweepNowAsync();
        MenuPopup.Opened += (_, _) => OnMenuOpened();
        MenuPopup.Closed += (_, _) => _menuClosedAt = MenuButton.IsMouseOver ? Environment.TickCount64 : 0;
        // A click anywhere else closes an extension's popup, as in other browsers.
        PreviewMouseDown += (_, e) => { if (_extensionPopup is { } p && !IsWithin(e.OriginalSource as DependencyObject, p.PlacementTarget)) CloseExtensionPopup(); };
        Deactivated += (_, _) => CloseExtensionPopup();
        Native.KeepWithWindow(MenuPopup, this);
        // Vireo's floating parts (menu, suggestions, find bar) are windows Windows keeps on top of everything: they must never
        // stay over another program. Switching away hides them; coming back brings the find bar back where it was.
        Deactivated += (_, _) =>
        {
            MenuPopup.IsOpen = false;
            HideSuggestions();
            if (_findPopup is { IsOpen: true }) { _findPopup.IsOpen = false; _findWaiting = true; }
        };
        Activated += (_, _) =>
        {
            if (_findWaiting && _findPopup is not null) { _findPopup.IsOpen = true; Native.KeepJustAbove(_findPopup, this); }
            _findWaiting = false;
        };
        // The mouse's side buttons over Vireo's own toolbar and tabs (inside the page the engine handles them itself).
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) { OnBack(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2) { OnForward(this, new RoutedEventArgs()); e.Handled = true; }
        };
        Address.TextChanged += (_, _) => { UpdateHint(); UpdateAddressView(); if (_typed) UpdateSuggestions(); };
        Address.PreviewTextInput += (_, _) => _typed = true;
        Address.PreviewMouseLeftButtonDown += OnAddressMouseDown;
        Address.ContextMenu = AddressMenu();
        System.Windows.DataObject.AddPastingHandler(Address, (_, _) => _typed = true);
        AnimateOmni(false);
    }

    // ---- Startup ----

    private async Task StartAsync()
    {
        var data = App.ProfileFolder();
        Directory.CreateDirectory(data);
        ShowProfile();
        _dataFolder = data;
        // Every normal window works on the same settings; a private window has its own copy that is never saved.
        _settings = _private ? Settings.Load(data) : (s_settings ??= Settings.Load(data));
        if (_private) { _settings.Detach(); Title = "Vireo (Private)"; PrivatePill.Visibility = Visibility.Visible; }
        else SiteIcons.Use(data);
        ApplyTabSide(_settings.TabSide, animate: false);
        StartLibrary(data);
        StartDownloads();
        StartBlocking();
        StartMedia();
        _lowEnd = SuspensionPolicy.ForMemory(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) == SuspensionSettings.LowEnd;
        // Every window of a profile shares one engine (it allows one set of options per folder); private windows simply do not offer extensions.
        _env = await EnvironmentFor(data, _lowEnd);
        _sweep.Start();

        var args = Environment.GetCommandLineArgs();
        var selfTest = SelfTest.TryParse(args, out var outDir, out var baseUrl);
        SelfTesting = selfTest;
        if (_fill is not null || !IsFirstWindow)
        {
            // A window opened for something (a tab moved here, a window of the last session) or a plain new window.
            if (_fill is not null) await _fill(this);
            if (_tabs.Count == 0) await OpenTabAsync(null);
        }
        else
        {
            var first = selfTest ? null : args.Skip(1).FirstOrDefault(a => !a.StartsWith("--") && !App.IsOptionValue(a));
            var restored = !selfTest && !_private && _settings.RestoreTabs && await RestoreSessionAsync();
            if (first is not null) await OpenTabAsync(UrlInput.Resolve(first, _settings.SearchTemplate));
            else if (!restored) await OpenTabAsync(null);
        }
        if (IsFirstWindow) { StartSession(); StartSync(); StartUpdates(); }
        StartHome();
        if (IsFirstWindow) { Watchdog = new UiWatchdog(Dispatcher); Closed += (_, _) => Watchdog?.Dispose(); }
        _ = RefreshExtensionsAsync();
        _settings.Changed += () => Dispatcher.BeginInvoke(ApplyEngineSettings);
        _ready.TrySetResult();
        if (selfTest && IsFirstWindow) _ = new SelfTest(this, outDir, baseUrl).RunAsync();
    }

    // ---- Tabs ----

    /// <summary>Opens a tab. With no address it shows the start page.</summary>
    /// <summary>
    /// <paramref name="lazy"/> makes a sleeping tab that shows <paramref name="title"/> and loads when clicked (used to bring back a
    /// session without starting a page process for every tab).
    /// </summary>
    public async Task<BrowserTab> OpenTabAsync(string? url, bool activate = true, CoreWebView2NewWindowRequestedEventArgs? popup = null, bool lazy = false, string? title = null, bool pinned = false)
    {
        var tab = new BrowserTab { IsStartPage = url is null && popup is null, Url = url ?? "", OpenedByPage = popup is not null, IsPinned = pinned, Owner = this };
        _tabs.Add(tab);
        AttachHeader(tab);

        if (lazy)
        {
            tab.Title = string.IsNullOrWhiteSpace(title) ? (Uri.TryCreate(url, UriKind.Absolute, out var lu) ? lu.Host : url ?? "") : title;
            tab.Life = TabLife.Discarded;
            return tab;
        }
        await EnsureViewAsync(tab);
        if (popup is not null && tab.View?.CoreWebView2 is { } adopted) popup.NewWindow = adopted; // the page that asked loads into this tab
        else Navigate(tab, url);
        if (activate) await ActivateAsync(tab);
        return tab;
    }

    /// <summary>The tab's header in the strip, with everything it can do.</summary>
    private void AttachHeader(BrowserTab tab, bool grow = true)
    {
        var header = new TabHeader(tab);
        header.Selected += async t => await ActivateAsync(t);
        header.CloseRequested += async t => await CloseTabAsync(t);
        header.DuplicateRequested += t => _ = OpenTabAsync(t.Url);
        header.PinToggled += TogglePin;
        // Pinned tabs stay when the others are closed in bulk, as in other browsers.
        header.CloseOthersRequested += async t => await CloseOthersAsync(t);
        header.CloseRightRequested += async t => await CloseRightAsync(t);
        header.MoveToWindowRequested += t => _ = MoveToNewWindowAsync(t);
        header.CanMoveToWindow = () => !_private && _tabs.Count > 1;
        header.ApplyLayout(_settings.TabSide, Compact);
        _headers[tab] = header;
        header.Dragged += DragTab;
        header.DragMoving += OnTabDragMoving;
        header.DragEnded += (t, screen, outside) => _ = OnTabDragEndedAsync(t, screen, outside);
        TabStrip.SetPresence(header, 0);
        Tabs.Children.Insert(Tabs.Children.Count - 1, header);
        if (grow) GrowHeader(tab);
    }

    /// <summary>The tab's header grows into its place in the strip (once).</summary>
    private void GrowHeader(BrowserTab tab)
    {
        if (!_headers.TryGetValue(tab, out var header) || TabStrip.GetPresence(header) > 0) return;
        header.BeginAnimation(TabStrip.PresenceProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private readonly PopupPolicy _popups = new();

    /// <summary>
    /// A page asked for a new window. Allowed ones open as a tab that stays connected to the page that asked
    /// (so "sign in with ..." pop-ups can hand their answer back); blocked ones are counted on the shield.
    /// </summary>
    private void OnNewWindowRequested(BrowserTab tab, CoreWebView2 core, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Uri)) { e.Handled = true; return; }
        var frame = e.OriginalSourceFrameInfo;
        var fromFrame = frame?.ParentFrameInfo is not null;
        var frameSource = frame?.Source;
        var otherSite = fromFrame && Uri.TryCreate(frameSource, UriKind.Absolute, out var fu) && Uri.TryCreate(core.Source, UriKind.Absolute, out var pu)
            && !Media.MediaProbe.RootDomainOf(fu.Host).Equals(Media.MediaProbe.RootDomainOf(pu.Host), StringComparison.OrdinalIgnoreCase);
        var listed = _settings.BlockAds && !IsAllowedSite(core.Source)
            && (_filters.Check(e.Uri, core.Source, Blocking.ResourceType.Popup).Block || _filters.Check(e.Uri, core.Source, Blocking.ResourceType.Other).Block);

        var verdict = _popups.Decide(new PopupRequest(e.IsUserInitiated, fromFrame, otherSite, listed), _settings.BlockPopups, _settings.BlockAds);
        if (verdict != PopupVerdict.Open)
        {
            e.Handled = true;
            RecordPopupBlocked(tab, e.Uri);
            return;
        }

        // Open it as a tab, in front when the person clicked for it, and keep the link to the page that asked.
        var deferral = e.GetDeferral();
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try { await OpenTabAsync(null, activate: e.IsUserInitiated, popup: e); }
            catch { e.Handled = true; }
            finally { deferral.Complete(); }
        });
    }

    public async Task ActivateAsync(BrowserTab tab)
    {
        using var _crumb = Breadcrumbs.Scope("switching tab");
        CloseExtensionPopup();
        if (Active == tab || !_tabs.Contains(tab)) return;
        var previous = Active;
        if (previous is not null)
        {
            if (previous.View is { } pv)
            {
                pv.Visibility = Visibility.Collapsed;
                if (pv.CoreWebView2 is { } pc) TrySetMemory(pc, CoreWebView2MemoryUsageTargetLevel.Low);
            }
            previous.HiddenSince = DateTime.UtcNow;
            previous.Life = TabLife.Background;
            _headers[previous].SetActive(false);
        }

        Active = tab;
        tab.HiddenSince = null;
        var wasFreed = tab.View is null;
        if (wasFreed) await EnsureViewAsync(tab);
        if (tab.View?.CoreWebView2 is { } core)
        {
            if (tab.Life == TabLife.Suspended)
            {
                if (tab.View is not null && ControllerOf(tab.View) is { } controller) controller.IsVisible = true;
                core.Resume();
            }
            TrySetMemory(core, CoreWebView2MemoryUsageTargetLevel.Normal);
        }
        if (wasFreed && !string.IsNullOrEmpty(tab.Url)) Navigate(tab, tab.Url);
        if (tab.View is { } view) view.Visibility = Visibility.Visible;
        tab.Life = TabLife.Active;
        _headers[tab].SetActive(true);
        if (FindOpen) CloseFind();
        RefreshToolbar();
        UpdateShield();
        UpdateMediaButton();
        if ((tab.IsStartPage || string.IsNullOrEmpty(tab.Url)) && !tab.OpenedByPage) Address.Focus();
        else tab.View?.Focus();
    }

    public async Task CloseTabAsync(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        var index = _tabs.IndexOf(tab);
        if (_tabs.Count == 1)
        {
            // Closing the last tab ends the visit; the next start is a fresh one, as in other browsers.
            _noSessionSave = true;
            SessionStore.Delete(SessionPath);
            Close();
            return;
        }
        if (!tab.IsStartPage && !string.IsNullOrEmpty(tab.Url)) _closed.Push(tab.Url);

        _tabs.Remove(tab);
        if (Active == tab)
        {
            Active = null;
            await ActivateAsync(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }

        var header = _headers[tab];
        _headers.Remove(tab);
        var shrink = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        // The page goes out of sight (and silent) at once; its engine is freed after the header has shrunk away, so the work of
        // freeing it does not land in the middle of the animation.
        if (tab.View is { } closing)
        {
            closing.Visibility = Visibility.Collapsed;
            try { if (closing.CoreWebView2 is { } c) c.IsMuted = true; } catch { /* not started */ }
        }
        shrink.Completed += (_, _) => { Tabs.Children.Remove(header); DisposeView(tab); };
        header.IsHitTestVisible = false;
        header.BeginAnimation(TabStrip.PresenceProperty, shrink);
    }

    private async Task ReopenClosedAsync()
    {
        if (_closed.TryPop(out var url)) await OpenTabAsync(url);
    }

    private void DisposeView(BrowserTab tab)
    {
        if (tab.View is not { } view) return;
        tab.View = null;
        Pages.Children.Remove(view);
        view.Dispose();
    }

    // ---- Pages ----

    /// <summary>The page's corners follow its card (square in full screen). The web page is a window of its own, so it is cut to shape.</summary>
    private void RoundPage(WebView2 view)
    {
        if (!view.IsVisible || view.ActualWidth < 2) return;
        var dpi = VisualTreeHelper.GetDpi(view);
        Native.RoundWindow(view.Handle, (int)Math.Ceiling(view.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(view.ActualHeight * dpi.DpiScaleY),
            _fullscreen ? 0 : (int)Math.Round(11 * dpi.DpiScaleX));
    }

    private async Task EnsureViewAsync(BrowserTab tab)
    {
        if (tab.View is not null || _env is null) return;
        CoreWebView2ControllerOptions? controllerOptions = null;
        if (_private)
        {
            controllerOptions = _env.CreateCoreWebView2ControllerOptions();
            controllerOptions.IsInPrivateModeEnabled = true;
        }
        // Right after the engine of an earlier run was killed (a crash, the task manager) it can still be letting go of the profile:
        // "the resource is in use". That passes in a second or two, so the view is made again a few times before giving up.
        WebView2 view;
        for (var attempt = 1; ; attempt++)
        {
            view = new WebView2 { Visibility = Visibility.Collapsed, DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 22, 29, 28) };
            var shown = view;
            view.SizeChanged += (_, _) => (tab.Owner ?? this).RoundPage(shown);
            view.IsVisibleChanged += (_, _) => (tab.Owner ?? this).RoundPage(shown);
            Pages.Children.Add(view);
            tab.View = view;
            try { await view.EnsureCoreWebView2Async(_env, controllerOptions); break; }
            catch (System.Runtime.InteropServices.COMException e) when (attempt < 8 && (e.HResult == unchecked((int)0x800700AA) || e.HResult == unchecked((int)0x8007139F)))
            {
                Pages.Children.Remove(view);
                tab.View = null;
                try { view.Dispose(); } catch { /* never started */ }
                await Task.Delay(600 * attempt);
            }
            catch
            {
                ShowToast("Browser engine", "Could not start. Close other Vireo windows and try again.", null, isError: true);
                throw;
            }
        }
        var core = view.CoreWebView2;
        // The start page's files are for the start page: other sites may not load or frame them.
        core.SetVirtualHostNameToFolderMapping(StartHost, Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Deny);
        // Microsoft Defender SmartScreen: known phishing and malware sites and downloads are stopped with a warning.
        try { core.Settings.IsReputationCheckingRequired = true; } catch { /* an older engine has it on by default */ }

        // Every event of the page goes to the window the tab is in *now*: a tab can be dragged to another window.
        MainWindow O() => tab.Owner ?? this;
        AttachBlocking(tab, core);
        AttachMedia(tab, core);
        core.NavigationStarting += (_, e) => O().OnPrivacyNavigation(tab, core, e);
        core.NavigationCompleted += (_, e) => O().OnPrivacyNavigated(tab, core, e);
        ApplyEngineSettings();
        core.ContextMenuRequested += (_, e) => O().OnContextMenuRequested(tab, core, e);
        core.ProcessFailed += (_, e) => O().OnProcessFailed(tab, core, e);
        core.NavigationCompleted += (_, _) => O().ApplySiteZoom(tab);
        view.ZoomFactorChanged += (_, _) => O().OnZoomChanged(tab);
        view.GotFocus += (_, _) => O().CloseExtensionPopup(); // a click into the page
        core.DownloadStarting += (_, e) => O().OnDownloadStarting(tab, core, e);
        core.NewWindowRequested += (_, e) => O().OnNewWindowRequested(tab, core, e);
        core.WindowCloseRequested += (_, _) => Dispatcher.InvokeAsync(() => O().CloseTabAsync(tab));
        core.DocumentTitleChanged += (_, _) => { tab.Title = core.DocumentTitle; if (!_private && !tab.IsStartPage) _library.UpdateTitle(core.Source, core.DocumentTitle); };
        core.SourceChanged += (_, _) => O().OnSource(tab, core.Source);
        core.NavigationStarting += (_, _) => { tab.IsLoading = true; if (tab == O().Active) O().RefreshToolbar(); };
        core.NavigationCompleted += (_, e) => { tab.IsLoading = false; UpdateHistory(tab, core); if (e.IsSuccess) O().RecordVisit(tab, core); if (tab == O().Active) O().RefreshToolbar(); };
        core.HistoryChanged += (_, _) => { UpdateHistory(tab, core); if (tab == O().Active) O().RefreshToolbar(); };
        core.IsDocumentPlayingAudioChanged += (_, _) => tab.IsPlayingAudio = core.IsDocumentPlayingAudio;
        core.FaviconChanged += async (_, _) =>
        {
            tab.Favicon = await LoadFaviconAsync(core);
            if (!_private) SiteIcons.Remember(core.Source, tab.Favicon);
        };
        core.ContainsFullScreenElementChanged += (_, _) => O().SetFullscreen(core.ContainsFullScreenElement);
        view.CoreWebView2InitializationCompleted += (_, _) => { };

        // Shortcuts must work while the page has the keyboard too.
        if (ControllerOf(view) is { } controller)
        {
            controller.AcceleratorKeyPressed += (_, e) =>
            {
                if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)) return;
                var key = KeyInterop.KeyFromVirtualKey((int)e.VirtualKey);
                if (O().HandleShortcut(key, Keyboard.Modifiers)) e.Handled = true;
            };
        }
    }

    /// <summary>The WPF control keeps its controller internal; read it so keys pressed inside a page still reach us.</summary>
    private static CoreWebView2Controller? ControllerOf(WebView2 view)
    {
        const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        try { return view.GetType().GetProperty("CoreWebView2Controller", any)?.GetValue(view) as CoreWebView2Controller; }
        catch { return null; }
    }

    private void Navigate(BrowserTab tab, string? url)
    {
        if (tab.View?.CoreWebView2 is not { } core) return;
        core.Navigate(string.IsNullOrEmpty(url) ? $"https://{StartHost}/newtab.html" : url);
    }

    private void OnSource(BrowserTab tab, string source)
    {
        using var _crumb = Breadcrumbs.Scope("address changed");
        var onStart = Uri.TryCreate(source, UriKind.Absolute, out var u) && u.Host == StartHost;
        tab.IsStartPage = onStart;
        tab.Url = onStart ? "" : source;
        if (onStart) tab.Title = "";
        if (tab == Active) RefreshToolbar();
    }

    private static void UpdateHistory(BrowserTab tab, CoreWebView2 core)
    {
        tab.CanGoBack = core.CanGoBack;
        tab.CanGoForward = core.CanGoForward;
    }

    private static void TrySetMemory(CoreWebView2 core, CoreWebView2MemoryUsageTargetLevel level)
    {
        try { core.MemoryUsageTargetLevel = level; } catch { /* older runtime: no memory hint */ }
    }

    private static async Task<ImageSource?> LoadFaviconAsync(CoreWebView2 core)
    {
        try
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ---- Freezing and freeing hidden tabs ----

    public async Task SweepNowAsync()
    {
        using var _crumb = Breadcrumbs.Scope("putting tabs to sleep");
        foreach (var tab in _tabs.ToList())
        {
            if (tab == Active || tab.HiddenSince is not { } since) continue;
            var action = SuspensionPolicy.Decide(tab.Life, DateTime.UtcNow - since, tab.Facts, Suspension);
            if (action == TabAction.Suspend) await SuspendAsync(tab);
            else if (action == TabAction.Discard) Discard(tab);
            else continue;
            // One tab at a time, with a breath in between: closing a page's engine takes the window a moment,
            // and clicks and keys must get in between, not wait for the whole sweep.
            await Task.Delay(30);
        }
    }

    public async Task SuspendAsync(BrowserTab tab)
    {
        if (tab.Life != TabLife.Background || tab.View?.CoreWebView2 is not { } core) return;
        try
        {
            if (tab.View is not null && ControllerOf(tab.View) is { } controller) controller.IsVisible = false;
            TrySetMemory(core, CoreWebView2MemoryUsageTargetLevel.Low);
            if (await core.TrySuspendAsync()) tab.Life = TabLife.Suspended;
        }
        catch { /* stays awake */ }
    }

    public void Discard(BrowserTab tab)
    {
        if (tab == Active || tab.Life == TabLife.Discarded) return;
        DisposeView(tab);
        tab.IsLoading = false;
        tab.IsPlayingAudio = false;
        tab.Life = TabLife.Discarded;
        // (Vireo's own memory is not squeezed here: the pages live in the engine's processes, and forcing the window's memory
        // out only makes the next click slower.)
    }

    /// <summary>Puts every hidden tab to sleep now, except music, loading and pinned tabs.</summary>
    public async Task FreeNowAsync()
    {
        foreach (var tab in _tabs.ToList())
        {
            if (tab == Active) continue;
            var f = tab.Facts;
            if (f.IsPlayingAudio || f.IsLoading || f.IsPinned) continue;
            if (f.HasAddress) Discard(tab);
            else await SuspendAsync(tab);
        }
    }

    /// <summary>Total memory used by the pages (all WebView2 processes of this browser), in bytes.</summary>
    public (long Bytes, int Processes) MeasureMemory()
    {
        long bytes = 0;
        var count = 0;
        if (_env is null) return (0, 0);
        foreach (var info in _env.GetProcessInfos())
        {
            if (Native.WorkingSetOf(info.ProcessId) is not { } used) continue; // the process ended
            bytes += used;
            count++;
        }
        return (bytes, count);
    }

    // ---- Toolbar and address bar ----

    private void RefreshToolbar()
    {
        using var _crumb = Breadcrumbs.Scope("toolbar");
        var tab = Active;
        if (tab is null) return;
        BackButton.IsEnabled = tab.CanGoBack;
        ForwardButton.IsEnabled = tab.CanGoForward;
        ReloadButton.Content = tab.IsLoading ? Glyph.Stop : Glyph.Reload;
        ReloadButton.ToolTip = tab.IsLoading ? "Stop (Esc)" : "Reload (F5)";
        if (!Address.IsKeyboardFocused) Address.Text = UrlInput.Display(tab.Url);
        UpdateHint();
        UpdateBookmarkButton();
        OmniIcon.Text = IconFor(tab.Url);
        SetLoadingBar(tab.IsLoading);
    }

    /// <summary>Lock for secure pages, a warning for plain http on the open internet, a globe for the rest.</summary>
    private static string IconFor(string url)
    {
        if (string.IsNullOrEmpty(url)) return Glyph.Search;
        if (url.StartsWith("https://")) return Glyph.Lock;
        if (!url.StartsWith("http://")) return Glyph.Globe;
        var local = Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.EndsWith(".local"));
        return local ? Glyph.Globe : Glyph.Warning;
    }

    /// <summary>The address as other browsers show it at rest: "youtube.com" bright, "/watch?v=…" quieter; "http://" in amber.</summary>
    private void UpdateAddressView()
    {
        var text = Address.Text;
        if (Address.IsKeyboardFocused || text.Length == 0 || Active is not { } tab || !Uri.TryCreate(tab.Url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            AddressView.Visibility = Visibility.Collapsed;
            Address.Opacity = 1;
            return;
        }
        var host = u.Host + (u.IsDefaultPort ? "" : ":" + u.Port);
        var at = text.IndexOf(host, StringComparison.OrdinalIgnoreCase);
        if (at < 0) { AddressView.Visibility = Visibility.Collapsed; Address.Opacity = 1; return; }
        AddressView.Inlines.Clear();
        if (at > 0) AddressView.Inlines.Add(new System.Windows.Documents.Run(text[..at]) { Foreground = UiKit.Brush(u.Scheme == "http" ? "Warn" : "Text3") });
        AddressView.Inlines.Add(new System.Windows.Documents.Run(host) { Foreground = UiKit.Brush("Text") });
        if (at + host.Length < text.Length) AddressView.Inlines.Add(new System.Windows.Documents.Run(text[(at + host.Length)..]) { Foreground = UiKit.Brush("Text3") });
        AddressView.Visibility = Visibility.Visible;
        Address.Opacity = 0; // still there for the click that starts editing
    }

    private void UpdateHint() => Hint.Visibility = Address.Text.Length == 0 && !Address.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;

    private void OnAddressFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AnimateOmni(true);
        if (Active is { } tab) Address.Text = tab.Url;
        Address.SelectAll();
        UpdateHint();
        UpdateAddressView();
    }

    private void OnAddressBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        AnimateOmni(false);
        _typed = false;
        Dispatcher.BeginInvoke(CloseSuggestions, System.Windows.Threading.DispatcherPriority.Background);
        if (Active is { } tab) Address.Text = UrlInput.Display(tab.Url);
        UpdateHint();
        UpdateAddressView();
    }

    private bool _typed;
    private long _addressFocusClickAt;

    /// <summary>
    /// As in other browsers: the first click into the address selects all of it (so typing replaces it), and a quick second
    /// click keeps it all selected. Once you are editing, clicks place the caret and a double-click picks a word.
    /// </summary>
    private void OnAddressMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Address.IsKeyboardFocusWithin)
        {
            Address.Focus();
            Address.SelectAll();
            _addressFocusClickAt = Environment.TickCount64;
            e.Handled = true;
        }
        else if (e.ClickCount == 2 && Environment.TickCount64 - _addressFocusClickAt < 700)
        {
            Address.SelectAll();
            e.Handled = true;
        }
        else if (e.ClickCount >= 3)
        {
            Address.SelectAll();
            e.Handled = true;
        }
    }

    private ContextMenu AddressMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string? keys, Action run, Func<bool>? enabled = null)
        {
            var m = new MenuItem { Header = header, InputGestureText = keys ?? "" };
            m.Click += (_, _) => run();
            menu.Opened += (_, _) => m.IsEnabled = enabled?.Invoke() ?? true;
            menu.Items.Add(m);
            return m;
        }
        Item("Cut", "Ctrl+X", () => Address.Cut(), () => Address.SelectionLength > 0);
        Item("Copy", "Ctrl+C", () => Address.Copy(), () => Address.SelectionLength > 0);
        Item("Paste", "Ctrl+V", () => Address.Paste(), () => Clipboard.ContainsText());
        Item("Paste and go", null, () =>
        {
            var text = Clipboard.GetText().Trim();
            if (text.Length == 0 || Active is not { } tab) return;
            if (TypedTarget(text) is { } target) { Navigate(tab, target); tab.View?.Focus(); }
        }, () => Clipboard.ContainsText());
        menu.Items.Add(new Separator());
        Item("Select all", "Ctrl+A", () => { Address.Focus(); Address.SelectAll(); });
        return menu;
    }

    private void OnAddressKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Back or Key.Delete) _typed = true;
        if (SuggestionKey(e.Key)) { e.Handled = true; return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Enter)
        {
            var typed = Address.Text.Trim();
            // Ctrl+Enter: "example" becomes www.example.com, as in other browsers.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && typed.Length > 0 && !typed.Contains('.') && !typed.Contains(' ') && !typed.Contains('/'))
                typed = $"https://www.{typed}.com/";
            var target = TypedTarget(typed);
            if (target is not null && Active is { } tab)
            {
                // Alt+Enter: in a new tab, leaving this one as it is.
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    Address.Text = UrlInput.Display(tab.Url);
                    _ = OpenTabAsync(target);
                }
                else
                {
                    Navigate(tab, target);
                    tab.View?.Focus();
                }
            }
            CloseSuggestions();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (Active is { } tab) Address.Text = tab.Url;
            Active?.View?.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Where typed text goes: an address as it is, anything else to the search engine chosen in Settings.</summary>
    internal string? TypedTarget(string? typed) => UrlInput.Resolve(typed, _settings.SearchTemplate);

    public void NavigateActive(string typed)
    {
        var target = TypedTarget(typed);
        if (target is not null && Active is { } tab) Navigate(tab, target);
    }

    private void AnimateOmni(bool focused)
    {
        var accent = (Color)FindResource("AccentColor");
        var to = focused ? Color.FromArgb(0xC0, accent.R, accent.G, accent.B) : (Color)FindResource("LineColor");
        OmniBorder.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(140)));
        OmniBox.Background = (Brush)FindResource(focused ? "Surface2" : "Bg");
    }

    private void SetLoadingBar(bool on)
    {
        if (on && _loadStory is null)
        {
            var slide = new DoubleAnimation(-180, Math.Max(ActualWidth, 800), TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Storyboard.SetTarget(slide, LoadBar);
            Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));
            _loadStory = new Storyboard();
            _loadStory.Children.Add(slide);
            _loadStory.Begin(this, true);
            LoadTrack.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        }
        else if (!on && _loadStory is not null)
        {
            _loadStory.Stop(this);
            _loadStory = null;
            LoadTrack.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(280)));
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) { if (Active?.View?.CoreWebView2 is { CanGoBack: true } c) c.GoBack(); }
    private void OnForward(object sender, RoutedEventArgs e) { if (Active?.View?.CoreWebView2 is { CanGoForward: true } c) c.GoForward(); }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (Active?.View?.CoreWebView2 is not { } c) return;
        if (Active.IsLoading) c.Stop();
        else c.Reload();
    }

    // ---- Keyboard ----

    /// <summary>Browser shortcuts. Returns true when the key was used.</summary>
    public bool HandleShortcut(Key key, ModifierKeys mods)
    {
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var alt = mods.HasFlag(ModifierKeys.Alt);

        if (ctrl && !alt)
        {
            switch (key)
            {
                case Key.T when shift: _ = ReopenClosedAsync(); return true;
                case Key.N when shift: OpenPrivateWindow(); return true;
                case Key.W when shift: Close(); return true;
                case Key.N: OpenWindow(); return true;
                case Key.I when shift: OpenDevTools(); return true;
                case Key.J: ToggleDrawer("Downloads", ShowDownloads); return true;
                case Key.M when shift: ToggleDrawer("Media", ShowMedia); return true;
                case Key.G when shift: _ = ShowBatchGrabberAsync(); return true;
                case Key.E when shift: ToggleDrawer("Extensions", ShowExtensions); return true;
                case Key.A when shift: ToggleDrawer("Tabs", ShowAllTabs); return true;
                case Key.H: ToggleDrawer("History", ShowHistory); return true;
                case Key.D: BookmarkActive(); return true;
                case Key.F: ShowFind(); return true;
                case Key.P: PrintActive(); return true;
                case Key.OemPlus or Key.Add: ZoomActive(1.15); return true;
                case Key.OemMinus or Key.Subtract: ZoomActive(1.0 / 1.15); return true;
                case Key.D0 or Key.NumPad0: ResetZoomActive(); return true;
                case Key.O when shift: ToggleDrawer("Bookmarks", ShowBookmarks); return true;
                case Key.OemComma: ToggleDrawer("Settings", ShowSettings); return true;
                case Key.T: _ = OpenTabAsync(null); return true;
                case Key.W: if (Active is { } w) _ = CloseTabAsync(w); return true;
                case Key.L: Address.Focus(); return true;
                case Key.R: OnReload(this, new RoutedEventArgs()); return true;
                case Key.Tab: _ = StepTabAsync(shift ? -1 : 1); return true;
                case >= Key.D1 and <= Key.D9:
                    var n = key - Key.D1;
                    var target = key == Key.D9 ? _tabs[^1] : n < _tabs.Count ? _tabs[n] : null;
                    if (target is not null) _ = ActivateAsync(target);
                    return true;
            }
        }
        if (alt && !ctrl)
        {
            switch (key)
            {
                case Key.Left: OnBack(this, new RoutedEventArgs()); return true;
                case Key.Right: OnForward(this, new RoutedEventArgs()); return true;
                case Key.D: Address.Focus(); return true;
            }
        }
        switch (key)
        {
            case Key.F3:
                if (_findPopup?.IsOpen == true)
                {
                    if (shift) Active?.View?.CoreWebView2?.Find.FindPrevious();
                    else Active?.View?.CoreWebView2?.Find.FindNext();
                }
                else ShowFind();
                return true;
            case Key.F12: OpenDevTools(); return true;
            case Key.F5: OnReload(this, new RoutedEventArgs()); return true;
            case Key.F6: Address.Focus(); return true;
            case Key.F11: SetFullscreen(!_fullscreen); return true;
            case Key.Escape when _extensionPopup is not null: CloseExtensionPopup(); return true;
            case Key.Escape when _findPopup?.IsOpen == true: CloseFind(); return true;
            case Key.Escape when _drawerOpen && Drawer.IsKeyboardFocusWithin: CloseDrawer(); return true;
            case Key.Escape when Active?.IsLoading == true: Active.View?.CoreWebView2?.Stop(); return true;
        }
        return false;
    }

    private async Task StepTabAsync(int step)
    {
        if (Active is null || _tabs.Count < 2) return;
        var i = (_tabs.IndexOf(Active) + step + _tabs.Count) % _tabs.Count;
        await ActivateAsync(_tabs[i]);
    }

    // ---- Window ----

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged()
    {
        MaxButton.Content = WindowState == WindowState.Maximized ? Glyph.Restore : Glyph.Maximize;
        // A maximized window with a custom frame overhangs the screen by its invisible border.
        Frame.Margin = WindowState == WindowState.Maximized && !_fullscreen ? new Thickness(8 / VisualTreeHelper.GetDpi(this).DpiScaleX) : new Thickness(0);
    }

    private void SetFullscreen(bool on)
    {
        if (_fullscreen == on) return;
        _fullscreen = on;
        if (on)
        {
            _beforeFullscreen = WindowState;
            Frame.RowDefinitions[0].Height = new GridLength(0);
            Frame.RowDefinitions[1].Height = new GridLength(0);
            Frame.RowDefinitions[3].Height = new GridLength(0);
            Body.Margin = new Thickness(0);
            PageCard.Visibility = Visibility.Collapsed;
            Pages.Margin = new Thickness(0);
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            PageCard.Visibility = Visibility.Visible;
            Pages.Margin = new Thickness(1);
            ApplyTabSide(_settings.TabSide, animate: false);
            WindowState = _beforeFullscreen;
        }
        OnStateChanged();
        foreach (var t in _tabs) if (t.View is { } v) RoundPage(v);
    }

    // ---- Menu ----

    private long _menuClosedAt;

    private void OnMenu(object sender, RoutedEventArgs e)
    {
        // A popup that stays open only while used closes on the mouse-down outside it, which for this button comes just before
        // its click: without this, a click meant to close the menu would open it again.
        if (!MenuPopup.IsOpen && Environment.TickCount64 - _menuClosedAt < 300) return;
        MenuPopup.IsOpen = !MenuPopup.IsOpen;
    }

    internal void ShowMenu() => MenuPopup.IsOpen = true;
    internal void HideMenu() => MenuPopup.IsOpen = false;
    internal double MenuHeight => MenuCard.ActualHeight;
    internal string MemoryText => MemoryTotal.Text;
    internal double TabStripWidth => Tabs.DesiredSize.Width;

    private void OnMenuOpened()
    {
        MenuCard.Opacity = 0;
        MenuScale.ScaleX = MenuScale.ScaleY = 0.94;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Motion.CacheWhileMoving(MenuCard, 220); // its soft shadow is drawn once, not every frame of the opening
        MenuCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        var scale = new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        MenuScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        MenuScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        UpdateMenuZoomText();
        FillMemoryPanel();
    }

    private void FillMemoryPanel()
    {
        var (bytes, processes) = MeasureMemory();
        MemoryTotal.Text = $"{bytes / 1048576.0:0} MB";
        var awake = _tabs.Count(t => t.Life is TabLife.Active or TabLife.Background);
        var asleep = _tabs.Count(t => t.Life == TabLife.Suspended);
        var freed = _tabs.Count(t => t.Life == TabLife.Discarded);
        MemorySummary.Text = $"{awake} awake · {asleep + freed} asleep · {processes} processes";
        AllTabsText.Text = $"See all {_tabs.Count} tab{(_tabs.Count == 1 ? "" : "s")}";
        // Never taller than the screen it opens on.
        MenuScroll.MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 120);
    }

    private void OnMenuAllTabs(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; ShowAllTabs(); }
    internal void ShowAllTabs() => OpenDrawer("Tabs", new Controls.TabsPanel(this));

    /// <summary>Puts every hidden tab to sleep (the menu's and the Tabs panel's "Free now").</summary>
    internal Task FreeMemoryNowAsync() => FreeNowAsync();

    private async void OnMenuNewTab(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; await OpenTabAsync(null); }

    private async void OnMenuFreeNow(object sender, RoutedEventArgs e)
    {
        await FreeNowAsync();
        FillMemoryPanel();
    }

    public void ZoomActive(double factor)
    {
        if (Active?.View is not { } view) return;
        view.ZoomFactor = Math.Clamp(Math.Round(view.ZoomFactor * factor, 2), 0.25, 5.0);
        RememberZoom(Active!);
    }

    public void ResetZoomActive()
    {
        if (Active?.View is not { } view) return;
        view.ZoomFactor = 1.0;
        RememberZoom(Active!);
    }

    private bool _applyingZoom;

    private static string? ZoomKey(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.Host : null;

    /// <summary>A page opens at the zoom the person last chose for its site.</summary>
    private void ApplySiteZoom(BrowserTab tab)
    {
        if (tab.View is not { } view || view.CoreWebView2 is null || ZoomKey(view.CoreWebView2.Source) is not { } host) return;
        var wanted = _settings.SiteZoom.GetValueOrDefault(host, 1.0);
        if (Math.Abs(view.ZoomFactor - wanted) < 0.001) return;
        _applyingZoom = true;
        try { view.ZoomFactor = wanted; }
        finally { _applyingZoom = false; }
        if (tab == Active) UpdateMenuZoomText();
    }

    private void OnZoomChanged(BrowserTab tab)
    {
        if (!_applyingZoom) RememberZoom(tab);
    }

    /// <summary>Zoom changed (menu, keys or Ctrl + wheel): remember it for the site.</summary>
    private void RememberZoom(BrowserTab tab)
    {
        if (tab.View is not { } view || view.CoreWebView2 is null) return;
        if (tab == Active) UpdateMenuZoomText();
        if (ZoomKey(view.CoreWebView2.Source) is not { } host) return;
        var zoom = Math.Round(view.ZoomFactor, 2);
        if (Math.Abs(zoom - 1) < 0.005) _settings.SiteZoom.Remove(host);
        else
        {
            if (!_settings.SiteZoom.ContainsKey(host) && _settings.SiteZoom.Count >= Settings.MaxSiteZooms) _settings.SiteZoom.Remove(_settings.SiteZoom.Keys.First());
            _settings.SiteZoom[host] = zoom;
        }
        _settings.Commit();
    }

    /// <summary>Pinning moves a tab to the front group; unpinning moves it just behind that group.</summary>
    internal async Task CloseOthersAsync(BrowserTab keep)
    {
        foreach (var other in _tabs.Where(x => x != keep && !x.IsPinned).ToList()) await CloseTabAsync(other);
    }

    internal async Task CloseRightAsync(BrowserTab of)
    {
        var index = _tabs.IndexOf(of);
        if (index < 0) return;
        foreach (var right in _tabs.Skip(index + 1).Where(x => !x.IsPinned).ToList()) await CloseTabAsync(right);
    }

    internal void TogglePin(BrowserTab tab)
    {
        tab.IsPinned = !tab.IsPinned;
        var pinnedBefore = _tabs.Count(t => t != tab && t.IsPinned);
        MoveTab(tab, pinnedBefore);
    }

    /// <summary>A dragged tab takes the place the mouse is over; pinned tabs stay in front of the others.</summary>
    private void DragTab(BrowserTab tab, Point at)
    {
        var vertical = Tabs.Orientation == Orientation.Vertical;
        var others = Tabs.Children.OfType<Controls.TabHeader>().Where(h => h.Tab != tab).ToList();
        var index = others.Count(h =>
        {
            var origin = h.TranslatePoint(new Point(0, 0), Tabs);
            return (vertical ? origin.Y + h.ActualHeight / 2 : origin.X + h.ActualWidth / 2) < (vertical ? at.Y : at.X);
        });
        var pinned = _tabs.Count(t => t != tab && t.IsPinned);
        index = tab.IsPinned ? Math.Min(index, pinned) : Math.Max(index, pinned);
        if (index == _tabs.IndexOf(tab)) return;
        MoveTab(tab, index);
        _headers[tab].KeepDragging();
        SaveSession();
    }

    private void MoveTab(BrowserTab tab, int index)
    {
        var from = _tabs.IndexOf(tab);
        if (from < 0 || from == index || !_headers.TryGetValue(tab, out var header)) return;
        _tabs.RemoveAt(from);
        _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), tab);
        Tabs.Children.Remove(header);
        Tabs.Children.Insert(Math.Clamp(index, 0, Tabs.Children.Count - 1), header);
    }

    public void PrintActive()
    {
        if (Active?.View?.CoreWebView2 is not { } core) return;
        try { core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); } catch { }
    }

    public void OpenDevTools()
    {
        if (Active?.View?.CoreWebView2 is not { } core) return;
        try { core.OpenDevToolsWindow(); } catch { }
    }

    private void UpdateMenuZoomText()
    {
        if (MenuZoomText is null) return;
        var z = Active?.View is not null ? Active.View.ZoomFactor : 1.0;
        MenuZoomText.Text = $"{Math.Round(z * 100):0}%";
    }

    private void OnMenuZoomIn(object sender, RoutedEventArgs e) => ZoomActive(1.15);
    private void OnMenuZoomOut(object sender, RoutedEventArgs e) => ZoomActive(1.0 / 1.15);
    private void OnMenuZoomReset(object sender, MouseButtonEventArgs e) => ResetZoomActive();
    private void OnMenuFullscreen(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        SetFullscreen(!_fullscreen);
    }
    private void OnMenuPrint(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        PrintActive();
    }
    private void OnMenuFind(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        ShowFind();
    }
    private void OnMenuDevTools(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenDevTools();
    }
    private void OnMenuGrabber(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        _ = ShowBatchGrabberAsync();
    }

    /// <summary>Adds Vireo's own entries to the page's right-click menu: save a link, image or video, search for the selection, grab everything.</summary>
    private void OnContextMenuRequested(BrowserTab tab, CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (_env is null) return;
        var target = e.ContextMenuTarget;
        var items = e.MenuItems;
        var added = 0;

        CoreWebView2ContextMenuItem Command(string label, Action run)
        {
            var item = _env.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command);
            item.CustomItemSelected += (_, _) => Dispatcher.InvokeAsync(run);
            return item;
        }

        void Save(string url)
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                var cookie = await CookieHeaderAsync(core, url);
                var item = Downloads.Add(new DownloadRequest(url, null, core.Source, core.Settings.UserAgent, string.IsNullOrEmpty(cookie) ? null : cookie));
                ShowToast("Downloading", item.FileName.Length > 0 ? item.FileName : HostOnly(url), ShowDownloads);
            });
        }

        // Newest first at the top: search, then the media, then the link.
        if (target.HasLinkUri && target.LinkUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var link = target.LinkUri;
            items.Insert(0, Command("Download link with Vireo", () => Save(link)));
            added++;
        }
        if (target.HasSourceUri && target.SourceUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var src = target.SourceUri;
            var kind = target.Kind switch
            {
                CoreWebView2ContextMenuTargetKind.Image => "image",
                CoreWebView2ContextMenuTargetKind.Video => "video",
                CoreWebView2ContextMenuTargetKind.Audio => "audio",
                _ => "file",
            };
            items.Insert(0, Command($"Download {kind} with Vireo", () => Save(src)));
            added++;
        }
        if (!string.IsNullOrWhiteSpace(target.SelectionText))
        {
            var text = target.SelectionText.Trim();
            var shown = text.Length > 24 ? text[..24] + "…" : text;
            items.Insert(0, Command($"Search for \"{shown}\"", () => _ = OpenTabAsync(string.Format(_settings.SearchTemplate, Uri.EscapeDataString(text)))));
            added++;
        }
        if (added > 0) items.Insert(added, _env.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));

        // On the page itself (not on a link, picture or selection) offer the bulk tools.
        if (added == 0 && target.Kind == CoreWebView2ContextMenuTargetKind.Page)
        {
            items.Add(_env.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
            items.Add(Command("Download all with Vireo (Page Grabber)…", () => _ = ShowBatchGrabberAsync()));
            items.Add(Command("Videos and audio on this page…", ShowMedia));
        }
    }
}
