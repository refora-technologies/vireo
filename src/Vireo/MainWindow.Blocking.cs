using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Vireo.Blocking;
using Vireo.Controls;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

// Ad and tracker blocking: every request is checked before it leaves, and leftover ad boxes are hidden.
public partial class MainWindow
{
    // One filter engine for every window (it is the biggest thing Vireo keeps in memory).
    private static volatile FilterEngine _filters = new();
    private static FilterLists? _lists;
    private static string _builtIn = "";
    private static bool s_blockingStarted;

    public FilterEngine Filters => _filters;
    internal static string FilterStatus { get; private set; } = "Loading filters…";

    /// <summary>Runs in the page at the start of every top-level document. It reports the class and id names the page uses, and applies the style rules sent back.</summary>
    private const string CosmeticScript = """
        (() => {
          if (window.__vireoCos || window !== window.top) return;
          window.__vireoCos = true;
          const wv = window.chrome && window.chrome.webview;
          if (!wv) return;
          // A constructable stylesheet: strict style policies cannot block it the way they block <style> elements.
          const apply = css => {
            if (!css) return;
            const sheet = new CSSStyleSheet();
            sheet.replaceSync(css);
            document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
          };
          wv.addEventListener('message', e => { if (e.data && e.data.t === 'css') apply(e.data.css); });
          const seen = new Set(); let timer;
          const scan = () => {
            const c = [], i = [];
            for (const el of document.querySelectorAll('[class],[id]')) {
              if (el.id && !seen.has('#' + el.id)) { seen.add('#' + el.id); i.push(el.id); }
              if (el.classList) for (const k of el.classList) if (!seen.has('.' + k)) { seen.add('.' + k); c.push(k); }
            }
            if (c.length || i.length) wv.postMessage({ t: 'names', c: c.slice(0, 1500), i: i.slice(0, 1500) });
          };
          wv.postMessage({ t: 'hello' });
          document.addEventListener('DOMContentLoaded', () => {
            scan();
            new MutationObserver(() => { clearTimeout(timer); timer = setTimeout(scan, 300); })
              .observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'id'] });
          });
        })();
        """;

    /// <summary>For the site survey only: each request the blocker stops.</summary>
    internal static Action<string>? BlockedLog;

    /// <summary>
    /// How a refused request is answered: as a failed connection, the way an ad blocker's refusal looks in other browsers. A plain
    /// "403" would be an answer, and pages (and ad scripts checking whether they got through) would see the request succeed.
    /// A redirect to about:blank is one the engine refuses to follow, which ends the request with a network error.
    /// </summary>
    private CoreWebView2WebResourceResponse BlockedResponse() =>
        _env!.CreateWebResourceResponse(null, 307, "Blocked", "Location: about:blank\r\nCache-Control: no-store");

    /// <summary>Blocked this many times on one page, an address is answered slowly from then on (see <see cref="SlowRefusal"/>).</summary>
    internal const int RepeatsBeforeSlowRefusal = 3;

    /// <summary>
    /// How long to hold back the refusal of an address a page keeps asking for: a quarter of a second more each time, up to five.
    /// Some ad code retries a refused request at once, for ever: on one news site a single ad configuration file was asked for
    /// 71,000 times in four minutes, about 300 a second. That load on the page engine can make Vireo's own window stop answering
    /// (the engine's window sits inside Vireo's, and Windows links their input). Refused slowly, the loop runs a few times a
    /// minute instead; the ad still never loads, and to the page it is still a failed connection.
    /// </summary>
    internal static TimeSpan SlowRefusal(int times) => TimeSpan.FromMilliseconds(Math.Min(5000, 250 * Math.Max(1, times - RepeatsBeforeSlowRefusal)));

    internal bool CommunityListsReady => _filters.BlockedHostCount > 50000;

    private void StartBlocking()
    {
        _settings.Changed += ApplyEngineSettings;
        if (s_blockingStarted) return; // the lists are already loaded (or loading) for an earlier window
        s_blockingStarted = true;
        _lists = new FilterLists(Path.Combine(_dataFolder, "filters"));
        _builtIn = ReadBuiltIn();
        _guardScript = ReadAsset("guard.js");
        _filters = Rebuild();
        FilterStatus = Describe();
        _settings.Changed += OnBlockingSettingsChanged;
        // Community lists are loaded and refreshed in the background so start-up never waits for them.
        _ = Task.Run(async () =>
        {
            if (_settings.UseCommunityLists)
            {
                var fresh = _lists.NeedsUpdate(TimeSpan.FromDays(4)) && await _lists.UpdateAsync();
                var built = Rebuild();
                _ = Dispatcher.InvokeAsync(() => { _filters = built; FilterStatus = Describe(); foreach (var w in s_windows.ToList()) w.UpdateShield(); });
                if (fresh) _settings.ListsUpdatedAt = DateTime.Now;
            }
        });
    }

    private static string _guardScript = "";

    private static string ReadAsset(string name)
    {
        try { return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", name)); } catch { return ""; }
    }

    private static string ReadBuiltIn()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "filters", "vireo-core.txt");
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    private static readonly List<string> _extraRules = [];

    /// <summary>Adds rules of the person's own (or a test's). They survive list refreshes.</summary>
    internal void AddFilterRules(string rules)
    {
        _extraRules.Add(rules);
        _filters.AddList(rules);
    }

    private FilterEngine Rebuild()
    {
        var engine = FilterLists.Build(_builtIn, _settings.UseCommunityLists && _lists is not null ? _lists.LoadCached() : []);
        foreach (var r in _extraRules.ToArray()) engine.AddList(r);
        return engine;
    }

    private string Describe() => $"{_filters.RuleCount:N0} rules" + (_settings.UseCommunityLists ? (_lists?.LastUpdated is { } d ? $" · lists updated {Format.When(d)}" : " · community lists not downloaded yet") : " · built-in list only");

    private static bool _lastCommunity;
    private void OnBlockingSettingsChanged()
    {
        if (_settings.UseCommunityLists == _lastCommunity && _lists is not null) { UpdateShield(); return; }
        _lastCommunity = _settings.UseCommunityLists;
        Task.Run(async () =>
        {
            if (_settings.UseCommunityLists && _lists!.NeedsUpdate(TimeSpan.FromDays(4))) await _lists.UpdateAsync();
            var built = Rebuild();
            _ = Dispatcher.InvokeAsync(() => { _filters = built; FilterStatus = Describe(); UpdateShield(); });
        });
        UpdateShield();
    }

    // ---- Per tab ----

    private static ResourceType TypeOf(CoreWebView2WebResourceContext c) => c switch
    {
        CoreWebView2WebResourceContext.Script => ResourceType.Script,
        CoreWebView2WebResourceContext.Image => ResourceType.Image,
        CoreWebView2WebResourceContext.Stylesheet => ResourceType.Stylesheet,
        CoreWebView2WebResourceContext.Font => ResourceType.Font,
        CoreWebView2WebResourceContext.Media => ResourceType.Media,
        CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Fetch => ResourceType.XmlHttpRequest,
        CoreWebView2WebResourceContext.Ping => ResourceType.Ping,
        CoreWebView2WebResourceContext.Websocket => ResourceType.WebSocket,
        _ => ResourceType.Other,
    };

    private void AttachBlocking(BrowserTab tab, CoreWebView2 core)
    {
        // One filter for everything; the page's own load is skipped in the handler (a blocker never stops you opening a site).
        // Every source: pages and their frames, and also service and shared workers (players and ads both use them).
        try { core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All); }
        catch { core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All); }

        core.WebResourceRequested += (_, e) =>
        {
            using var _crumb = Breadcrumbs.Scope("checking a request");
            var uri = e.Request.Uri;
            // Who asked for it is only worth knowing for video and audio (a page makes hundreds of other requests).
            if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Media.MediaClassifier.LooksLikeMediaUrl(uri))
            {
                try
                {
                    string? refVal = e.Request.Headers.Contains("Referer") ? e.Request.Headers.GetHeader("Referer") : null;
                    if (!string.IsNullOrWhiteSpace(refVal)) tab.RememberReferer(uri, refVal);
                    // Workers' answers never reach the response event: the request is all there is to go on.
                    if (e.RequestedSourceKind is CoreWebView2WebResourceRequestSourceKinds.ServiceWorker or CoreWebView2WebResourceRequestSourceKinds.SharedWorker)
                        (tab.Owner ?? this).OnWorkerRequest(tab, core, uri, refVal);
                }
                catch { }
            }
            // What a <video>/<audio> element loads is media, whatever its answer will call itself. (Download tools generally work this way.)
            if (e.ResourceContext == CoreWebView2WebResourceContext.Media && uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) && tab.MediaElementRequests.Count < 500)
                tab.MediaElementRequests.TryAdd(uri, 0);
            if (e.ResourceContext == CoreWebView2WebResourceContext.Document || !_settings.BlockAds) return;
            if (!uri.StartsWith("http", StringComparison.Ordinal)) return;
            // Never the video itself: what a <video> loads, and stream playlists, are what people come for. Adverts in players
            // are stopped one step earlier, at the ad server's script and XML requests.
            if (e.ResourceContext == CoreWebView2WebResourceContext.Media || uri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || uri.Contains(".mpd", StringComparison.OrdinalIgnoreCase)) return;
            var top = core.Source;
            if (IsAllowedSite(top)) return;
            var verdict = _filters.Check(uri, top, TypeOf(e.ResourceContext));
            if (!verdict.Block) return;
            var times = tab.BlockedAgain(uri);
            if (times > RepeatsBeforeSlowRefusal)
            {
                // A script retrying in a loop: refused all the same, but only after a growing pause (see SlowRefusal).
                var deferral = e.GetDeferral();
                var args = e;
                _ = Task.Delay(SlowRefusal(times)).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
                {
                    try { args.Response = BlockedResponse(); } catch { /* the page went away meanwhile */ }
                    finally { try { deferral.Complete(); } catch { } }
                }), TaskScheduler.Default);
            }
            else e.Response = BlockedResponse();
            BlockedLog?.Invoke($"BLOCKED {e.ResourceContext} {uri}");
            // Counted, and the toolbar told, only when it is a new address; and the toolbar at most a few times a second.
            if (tab.CountBlocked(uri, HostOnly(uri), verdict.Category) && tab.Owner is { } owner && tab == owner.Active) owner.QueueShieldUpdate();
        };

        core.NavigationStarting += (_, e) =>
        {
            tab.ResetBlocked();
            if (tab.Owner is { } owner && tab == owner.Active) owner.UpdateShield();
        };

        core.AddScriptToExecuteOnDocumentCreatedAsync(CosmeticScript);
        if (_guardScript.Length > 0 && Environment.GetEnvironmentVariable("VIREO_NOGUARD") != "1") _ = core.AddScriptToExecuteOnDocumentCreatedAsync(_guardScript);
        core.Settings.AreDefaultScriptDialogsEnabled = false; // Vireo draws them (the event is only raised when the engine's own are off)
        core.ScriptDialogOpening += (_, e) => (tab.Owner ?? this).OnScriptDialog(tab, e);
        core.PermissionRequested += (_, e) => (tab.Owner ?? this).OnPermission(tab, core, e);
        core.NavigationStarting += (_, e) => (tab.Owner ?? this).OnPageRedirect(tab, core, e);
        core.WebMessageReceived += (_, e) => (tab.Owner ?? this).OnPageMessage(tab, core, e);
    }

    // ---- Pages that misbehave ----

    /// <summary>
    /// alert(), confirm(), prompt() and "leave this page?" are drawn by Vireo. After a couple of boxes in a short time a page
    /// is not allowed to show more (it is nearly always a trap or a loop): further ones are answered "OK" / "no" without being shown.
    /// </summary>
    private void OnScriptDialog(BrowserTab tab, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        var leaving = e.Kind == CoreWebView2ScriptDialogKind.Beforeunload;
        if (!leaving && _settings.BlockPopups)
        {
            var now = DateTime.UtcNow;
            if (now - tab.DialogWindowStart > TimeSpan.FromSeconds(15)) { tab.DialogWindowStart = now; tab.DialogCount = 0; }
            tab.DialogCount++;
            if (tab.DialogCount > 2)
            {
                tab.DialogsSuppressed++;
                if (tab.DialogCount == 3) ShowToast("Message boxes blocked", HostOnly(tab.Url) + " kept opening them", null);
                if (e.Kind == CoreWebView2ScriptDialogKind.Alert) e.Accept(); // an alert has no answer to give; the others are answered "no"
                return;
            }
        }
        if (SelfTesting) { e.Accept(); return; }

        var deferral = e.GetDeferral();
        var origin = HostOnly(tab.Url);
        var kind = e.Kind;
        var message = e.Message;
        var preset = e.DefaultText;
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var box = new ScriptDialogWindow(this, origin, kind, message, preset);
                box.ShowDialog();
                if (box.Accepted)
                {
                    if (kind == CoreWebView2ScriptDialogKind.Prompt) e.ResultText = box.Text;
                    e.Accept();
                }
            }
            catch { /* the page went away while the box was open */ }
            finally { deferral.Complete(); }
        });
    }

    /// <summary>Notifications are asked for by far too many pages and Vireo has no place to show them: they are refused.</summary>
    private void OnPermission(BrowserTab tab, CoreWebView2 core, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind != CoreWebView2PermissionKind.Notifications) return;
        e.State = CoreWebView2PermissionState.Deny;
        tab.NotificationsRefused++;
    }

    /// <summary>A page sending you somewhere else by itself (not because you clicked), to an address the lists know as an ad, is stopped.</summary>
    private void OnPageRedirect(BrowserTab tab, CoreWebView2 core, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!_settings.BlockAds || !_settings.BlockPopups || e.IsUserInitiated || IsAllowedSite(core.Source)) return;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var to) || to.Scheme is not ("http" or "https")) return;
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var from) || from.Scheme is not ("http" or "https")) return; // the first page of a tab
        if (FilterEngine.SameSite(from.Host, to.Host)) return;
        var verdict = _filters.Check(e.Uri, core.Source, ResourceType.Document);
        if (!verdict.Block) return;
        e.Cancel = true;
        RecordPopupBlocked(tab, e.Uri);
    }

    internal void RecordPopupBlocked(BrowserTab tab, string uri)
    {
        if (tab.CountBlocked(uri, HostOnly(uri), 0) && tab == Active) QueueShieldUpdate();
    }

    private static string HostOnly(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    private bool IsAllowedSite(string url)
    {
        if (_settings.AdAllowSites.Count == 0) return false;
        var host = HostOnly(url);
        return _settings.AdAllowSites.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    private void OnPageMessage(BrowserTab tab, CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs e, CoreWebView2Frame? frame = null)
    {
        using var _crumb = Breadcrumbs.Scope("a message from a page");
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("t").GetString() ?? "";
            if (type == "guard-hello")
            {
                var json = JsonSerializer.Serialize(new { t = "guard-config", on = _settings.BlockPopups });
                try { if (frame is not null) frame.PostWebMessageAsJson(json); else core.PostWebMessageAsJson(json); } catch { /* going away */ }
                return;
            }
            if (type == "popup-blocked")
            {
                if (_settings.BlockPopups) RecordPopupBlocked(tab, root.GetProperty("url").GetString() ?? "");
                return;
            }
            if (type == "http-continue")
            {
                HandleContinueHttp(core, root.GetProperty("u").GetString() ?? "");
                return;
            }
            if (HandleHomeMessage(tab, core, root, type)) return;
            if (type == "search" && Uri.TryCreate(core.Source, UriKind.Absolute, out var from) && from.Host == StartHost)
            {
                NavigateActive(root.GetProperty("q").GetString() ?? "");
                return;
            }
            if (OnMediaMessage(tab, core, frame, root, type)) return;
            if (!_settings.BlockAds || IsAllowedSite(core.Source)) return;
            var host = HostOnly(core.Source);
            string css;
            switch (root.GetProperty("t").GetString())
            {
                case "hello":
                    css = _filters.CosmeticCss(host);
                    break;
                case "names":
                    var classes = root.GetProperty("c").EnumerateArray().Select(x => x.GetString() ?? "").Take(1500);
                    var ids = root.GetProperty("i").EnumerateArray().Select(x => x.GetString() ?? "").Take(1500);
                    css = _filters.CssForNames(classes, ids, host);
                    break;
                default: return;
            }
            if (css.Length > 0)
            {
                var cssJson = JsonSerializer.Serialize(new { t = "css", css });
                if (frame != null)
                {
                    try { frame.PostWebMessageAsJson(cssJson); } catch { }
                }
                else
                {
                    try { core.PostWebMessageAsJson(cssJson); } catch { }
                }
            }
        }
        catch { /* a page can send anything; ignore what we do not understand */ }
    }

    // ---- The shield on the toolbar ----

    private int _shownCount = -1;

    private bool _shieldQueued;

    /// <summary>
    /// The shield's number is redrawn at most four times a second, however many requests are blocked. (A busy page can block
    /// hundreds a second; redrawing for each flooded the window's thread and made the toolbar shake.)
    /// </summary>
    internal void QueueShieldUpdate()
    {
        if (_shieldQueued) return;
        _shieldQueued = true;
        _ = Task.Delay(250).ContinueWith(_ => Dispatcher.BeginInvoke(() => { _shieldQueued = false; UpdateShield(); }, System.Windows.Threading.DispatcherPriority.Background));
    }

    internal void UpdateShield()
    {
        var tab = Active;
        var on = _settings.BlockAds && tab is not null && !IsAllowedSite(tab.Url);
        var count = on ? tab!.Blocked : 0;
        ShieldCount.Text = count > 0 ? (count > 999 ? "999+" : count.ToString()) : "";
        ShieldCount.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShieldIcon.Foreground = on ? (count > 0 ? UiKit.Brush("Accent") : UiKit.Brush("Text2")) : UiKit.Brush("Text3");
        ShieldButton.ToolTip = !_settings.BlockAds ? "Blocking is off" : on ? $"{count} blocked on this page" : "Blocking is off for this site";
        // A small pop when the number first appears on a page; never on every increase (that made it bounce).
        if (_shownCount <= 0 && count > 0) PopIn(ShieldCount);
        _shownCount = count;
    }

    internal int ActiveBlocked => Active?.Blocked ?? 0;
    internal string ShieldText => ShieldCount.Text;

    internal void ShowShield() => OpenDrawer("Privacy", BuildPrivacy());

    private UIElement BuildPrivacy()
    {
        var tab = Active;
        var host = tab is null ? "" : HostOnly(tab.Url);
        var site = FilterEngine.SiteOf(host);
        var hasSite = host.Length > 0 && !tab!.IsStartPage;

        // The headline: how many things this page tried to load and did not get to, in figures, then what they were.
        var big = new Grid { Margin = new Thickness(2, 2, 2, 0) };
        big.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        big.ColumnDefinitions.Add(new ColumnDefinition());
        big.Children.Add(new TextBlock { Text = tab?.Blocked.ToString() ?? "0", FontSize = 34, FontFamily = UiKit.Mono, FontWeight = FontWeights.Medium, Foreground = UiKit.Brush(tab?.Blocked > 0 ? "Text" : "Text3"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = hasSite ? $"blocked on {host}" : "blocked on this page", FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(new TextBlock { Text = tab is { Blocked: > 0 } ? $"{tab.BlockedAds} ads · {tab.BlockedTrackers} trackers" : "Nothing has tried yet", FontSize = 11.5, FontFamily = UiKit.Mono, Foreground = UiKit.Brush("Text3"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(words, 1);
        big.Children.Add(words);

        var children = new List<UIElement> { big };

        if (hasSite)
        {
            var allowed = IsAllowedSite(tab!.Url);
            children.Add(UiKit.Heading("This site"));
            children.Add(UiKit.Card(UiKit.Row($"Block on {site}", "Turn off if a page does not work with blocking on.", UiKit.Switch(!allowed, on =>
            {
                _settings.AdAllowSites.RemoveAll(d => d == site);
                if (!on) _settings.AdAllowSites.Add(site);
                _settings.Commit();
                tab.View?.CoreWebView2?.Reload();
                UpdateShield();
                OpenDrawer("Privacy", BuildPrivacy());
            }))));
        }

        if (tab is { Blocked: > 0 })
        {
            children.Add(UiKit.Heading("Most blocked here"));
            var rows = tab.BlockedHosts.OrderByDescending(kv => kv.Value).Take(6)
                .Select(kv => (UIElement)UiKit.Row(kv.Key, null, UiKit.Number(kv.Value.ToString()))).ToArray();
            children.Add(UiKit.Card(rows));
        }

        children.Add(UiKit.Heading("Everywhere"));
        children.Add(UiKit.Card(
            UiKit.Row("Block ads and trackers", "Stops them before they load and hides leftover boxes.", UiKit.Switch(_settings.BlockAds, on => { _settings.BlockAds = on; _settings.Commit(); UpdateShield(); })),
            UiKit.Row("Block pop-up windows", "Prevent websites from opening unwanted popups and tabs.", UiKit.Switch(_settings.BlockPopups, on => { _settings.BlockPopups = on; _settings.Commit(); })),
            UiKit.Row("Use the community lists", "EasyList, EasyPrivacy, and the ad and tracker host lists of Peter Lowe and AdGuard: far more coverage, at the cost of some memory.", UiKit.Switch(_settings.UseCommunityLists, on => { _settings.UseCommunityLists = on; _settings.Commit(); })),
            UiKit.Block("Filters", FilterStatus, new Button { Style = (Style)FindResource("SecondaryButton"), Content = "Update lists now", HorizontalAlignment = HorizontalAlignment.Left, Tag = "update" })));
        if (((StackPanel)((Border)children[^1]).Child).Children.OfType<StackPanel>().Last().Children.OfType<Button>().FirstOrDefault() is { } update)
            update.Click += async (_, _) =>
            {
                update.Content = "Updating…";
                update.IsEnabled = false;
                await Task.Run(async () => { if (_lists is not null) await _lists.UpdateAsync(); });
                _settings.UseCommunityLists = true;
                _settings.ListsUpdatedAt = DateTime.Now;
                _settings.Commit();
                _filters = await Task.Run(Rebuild);
                FilterStatus = Describe();
                OpenDrawer("Privacy", BuildPrivacy());
            };

        return UiKit.Page([.. children]);
    }
}
