using System.Net;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

// Privacy: strip tracking from links, prefer secure connections, tracking prevention, private windows.
public partial class MainWindow
{
    private readonly bool _private;
    private readonly HashSet<string> _httpAllowed = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow() : this(false) { }

    public MainWindow(bool isPrivate)
    {
        _private = isPrivate;
        InitializeComponent();
        Construct();
        JoinWindows();
    }

    internal bool IsPrivate => _private;

    internal void OpenPrivateWindow()
    {
        var win = new MainWindow(true) { Left = Left + 36, Top = Top + 36, Width = Width, Height = Height };
        win.Show();
    }

    private static bool IsLocalAddress(Uri u)
    {
        if (u.IsLoopback || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !u.Host.Contains('.')) return true;
        if (!IPAddress.TryParse(u.Host, out var ip)) return false;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31));
    }

    /// <summary>Called as a page starts to load: cleans the link and upgrades plain http where asked.</summary>
    private void OnPrivacyNavigation(BrowserTab tab, CoreWebView2 core, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return;

        if (_settings.StripTrackingParams)
        {
            var clean = UrlCleaner.Clean(e.Uri);
            if (clean != e.Uri)
            {
                e.Cancel = true;
                Dispatcher.BeginInvoke(() => core.Navigate(clean));
                return;
            }
        }

        if (_settings.HttpsOnly && u.Scheme == "http" && !IsLocalAddress(u) && !_httpAllowed.Contains(u.Host))
        {
            e.Cancel = true;
            tab.UpgradedFrom = e.Uri;
            var secure = new UriBuilder(u) { Scheme = "https", Port = u.IsDefaultPort ? -1 : u.Port }.Uri.ToString();
            Dispatcher.BeginInvoke(() => core.Navigate(secure));
        }
    }

    /// <summary>If a page we upgraded to https could not be reached, say so and let the person choose.</summary>
    private void OnPrivacyNavigated(BrowserTab tab, CoreWebView2 core, CoreWebView2NavigationCompletedEventArgs e)
    {
        var from = tab.UpgradedFrom;
        if (from is null) return;
        if (e.IsSuccess) { tab.UpgradedFrom = null; return; }
        if (e.WebErrorStatus is CoreWebView2WebErrorStatus.OperationCanceled) return;
        tab.UpgradedFrom = null;
        var safe = WebUtility.HtmlEncode(from);
        // The address goes into an HTML attribute, so its quotes must be encoded too.
        var json = WebUtility.HtmlEncode(System.Text.Json.JsonSerializer.Serialize(from));
        core.NavigateToString($$"""
            <!doctype html><meta charset="utf-8"><title>Not secure</title>
            <style>
              html,body{height:100%;margin:0;background:#161D1C;color:#EAF2F0;font:15px/1.55 "Segoe UI Variable Text","Segoe UI",sans-serif}
              main{max-width:540px;margin:0 auto;padding-top:18vh;padding-inline:24px}
              main:before{content:"";display:block;width:36px;height:3px;background:#F2C46D;margin-bottom:22px}
              h1{font-size:22px;font-weight:600;margin:0 0 10px} p{color:#A9BAB6} code{color:#EAF2F0;word-break:break-all;font-family:Consolas,monospace;font-size:13.5px}
              button{margin-top:18px;font:inherit;font-size:13.5px;border:1px solid rgba(255,255,255,.2);border-radius:10px;padding:9px 18px;background:transparent;color:#EAF2F0;cursor:pointer}
              button:hover{background:rgba(255,255,255,.07)}
            </style>
            <main>
              <h1>This site isn't available securely</h1>
              <p>Vireo tried <code>{{safe.Replace("http://", "https://")}}</code> and could not connect. The site may only offer an unencrypted connection, which means others on the network could read what you send and receive.</p>
              <button onclick="chrome.webview.postMessage({t:'http-continue',u:{{json}}})">Continue to the unsecured site</button>
            </main>
            """);
    }

    private void HandleContinueHttp(CoreWebView2 core, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "http") return;
        _httpAllowed.Add(u.Host);
        core.Navigate(url);
    }

    // ---- Settings that reach into the engine ----

    private static CoreWebView2TrackingPreventionLevel Level(TrackingLevel l) => l switch
    {
        TrackingLevel.Off => CoreWebView2TrackingPreventionLevel.None,
        TrackingLevel.Basic => CoreWebView2TrackingPreventionLevel.Basic,
        TrackingLevel.Strict => CoreWebView2TrackingPreventionLevel.Strict,
        _ => CoreWebView2TrackingPreventionLevel.Balanced,
    };

    internal void ApplyEngineSettings()
    {
        var levelSet = false;
        foreach (var tab in _tabs)
        {
            if (tab.View?.CoreWebView2 is not { } core) continue;
            if (!levelSet)
            {
                try { core.Profile.PreferredTrackingPreventionLevel = Level(_settings.Tracking); } catch { /* older runtime */ }
                levelSet = true; // the profile is shared by every tab
            }
            try
            {
                // Passwords and form data are not kept at all in a private window.
                core.Settings.IsPasswordAutosaveEnabled = _settings.SavePasswords && !_private; // the engine's own password manager (Vireo never handles passwords itself)
                core.Settings.IsGeneralAutofillEnabled = _settings.FillForms && !_private;
            }
            catch { /* older runtime */ }
        }
    }

    private string BrowserArguments(bool lowEnd)
    {
        // Fewer helper processes and a smaller disk cache keep memory down. Tabs of one site sharing a process only on small
        // computers, where the saving matters more than a crash in one tab taking its neighbours of the same site along.
        var args = new List<string>
        {
            lowEnd ? "--renderer-process-limit=4" : "--renderer-process-limit=10",
            "--disk-cache-size=157286400",
            "--disable-features=Translate,OptimizationHints,MediaRouter,DialMediaRouteProvider"
        };
        if (lowEnd) { args.Add("--enable-low-end-device-mode"); args.Add("--process-per-site"); }
        if (_settings.BlockWebRtcLeak) args.Add("--force-webrtc-ip-handling-policy=default_public_interface_only");
        return string.Join(' ', args);
    }

    // ---- Clearing data by hand ----

    private System.Windows.UIElement ClearButtons()
    {
        var row = new System.Windows.Controls.WrapPanel { Margin = new System.Windows.Thickness(0, 4, 0, 0) };
        void Add(string label, string what)
        {
            var b = new System.Windows.Controls.Button { Style = (System.Windows.Style)FindResource("SecondaryButton"), Content = label, Height = 28, FontSize = 12, Padding = new System.Windows.Thickness(12, 0, 12, 0), Margin = new System.Windows.Thickness(0, 0, 6, 6) };
            b.Click += async (_, _) =>
            {
                b.IsEnabled = false;
                await ClearBrowsingDataAsync(what);
                b.IsEnabled = true;
                ShowToast("Cleared", label, null);
            };
            row.Children.Add(b);
        }
        Add("History", "history");
        Add("Cookies and site data", "cookies");
        Add("Cache", "cache");
        Add("Passwords", "passwords");
        return row;
    }

    /// <summary>Removes one kind of data: "history", "cookies", "cache" or "passwords".</summary>
    internal async Task ClearBrowsingDataAsync(string what)
    {
        var profile = _tabs.Select(t => t.View?.CoreWebView2?.Profile).FirstOrDefault(p => p is not null);
        try
        {
            switch (what)
            {
                case "history":
                    _library.ClearHistory();
                    if (profile is not null) await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory);
                    break;
                case "cookies":
                    if (profile is not null) await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.AllDomStorage);
                    break;
                case "cache":
                    if (profile is not null) await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
                    break;
                case "passwords":
                    if (profile is not null) await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.PasswordAutosave | CoreWebView2BrowsingDataKinds.GeneralAutofill);
                    break;
            }
        }
        catch { /* an older engine may not know every kind */ }
    }

    // ---- Closing: optionally forget everything ----

    private bool _cleared;

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        // A private window forgets its downloads when it closes, so one still running would be lost: say so first.
        if (!e.Cancel && _private && !SelfTesting && _downloads is { } dl && dl.Items.Count(i => i.State is Vireo.Downloads.DownloadState.Downloading or Vireo.Downloads.DownloadState.Queued) is var running and > 0)
        {
            var answer = MessageBox.Show(this,
                $"{running} download{(running == 1 ? " is" : "s are")} still running in this private window.\n\nClosing it cancels {(running == 1 ? "it" : "them")} and deletes what was saved so far. Close anyway?",
                "Vireo (Private)", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        if (!e.Cancel && !_private)
        {
            if (_settings.ClearDataOnExit) SessionStore.Delete(SessionPath);
            else SaveSession();
        }
        if (e.Cancel || _cleared || _private || !_settings.ClearDataOnExit) return;
        var profile = _tabs.Select(t => t.View?.CoreWebView2?.Profile).FirstOrDefault(p => p is not null);
        if (profile is null) return;
        e.Cancel = true;
        _cleared = true;
        try { await profile.ClearBrowsingDataAsync(); } catch { /* closing anyway */ }
        Close();
    }
}
