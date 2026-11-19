using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Vireo.Models;
using Vireo.Services;
using Vireo.Services.Import;

namespace Vireo;

/// <summary>
/// The welcome page, shown once: on the first start, and once to people updating to the version that brings it (they see
/// what is new instead). What Vireo does, then three things to set up: bring bookmarks, make it the default browser, and
/// whether to use it as the download manager. Every button does the real thing; skipping it all changes nothing.
/// </summary>
public partial class MainWindow
{
    /// <summary>Raised when a new welcome should be shown again to everyone who saw the last one.</summary>
    internal const int WelcomeVersion = 1;
    internal const string WelcomePath = "/welcome.html";

    /// <summary>"Just the browser" chosen on the page (the setting cannot say "not asked" and "said no" apart).</summary>
    private bool? _welcomeDm;

    private bool WelcomeDue => !_private && !SelfTesting && _settings.WelcomeSeen < WelcomeVersion;

    /// <summary>The welcome as the tab in front, after the restored tabs (or instead of an empty start page).</summary>
    private async Task OpenWelcomeAsync()
    {
        var url = $"https://{StartHost}{WelcomePath}";
        if (_tabs.Count == 1 && _tabs[0].IsStartPage && _tabs[0].View?.CoreWebView2 is { } core) { core.Navigate(url); return; }
        await OpenTabAsync(url);
    }

    internal static bool IsWelcome(CoreWebView2 core) => IsWelcomeAddress(core.Source);

    internal static bool IsWelcomeAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == StartHost && u.AbsolutePath == WelcomePath;

    /// <summary>The welcome page's buttons. Only the welcome page itself (Vireo's own file) is listened to.</summary>
    internal bool HandleWelcomeMessage(BrowserTab tab, CoreWebView2 core, JsonElement root, string type)
    {
        if (!type.StartsWith("welcome-", StringComparison.Ordinal) || !IsWelcome(core)) return false;
        switch (type)
        {
            case "welcome-hello":
                // Seen: it is not shown again, even if the tab is simply closed.
                if (_settings.WelcomeSeen < WelcomeVersion) { _settings.WelcomeSeen = WelcomeVersion; _settings.Commit(); }
                break;
            case "welcome-import":
                ShowImport();
                break;
            case "welcome-default":
                try { DefaultBrowser.Register(Environment.ProcessPath!); } catch { /* the registry is locked down: Windows may still list it */ }
                DefaultBrowser.OpenSettings();
                break;
            case "welcome-dm":
                var on = root.TryGetProperty("on", out var o) && o.ValueKind == JsonValueKind.True;
                _welcomeDm = on;
                UseAsDownloadManager(on);
                break;
            case "welcome-sort":
                _settings.SortByType = root.TryGetProperty("on", out var so) && so.ValueKind == JsonValueKind.True;
                _settings.Commit();
                break;
            case "welcome-done":
                core.Navigate($"https://{StartHost}/newtab.html");
                return true;
            default:
                return true;
        }
        SendWelcome(core);
        return true;
    }

    /// <summary>What the page shows: this computer's browsers, whether Vireo is the default, the download choice.</summary>
    private void SendWelcome(CoreWebView2 core)
    {
        List<string> browsers;
        // Tests (and the README pictures they take) never show which browsers the computer running them has.
        if (SelfTesting) browsers = ["Chrome", "Edge", "Firefox"];
        else try { browsers = OtherBrowsers.Detect().Select(b => b.Kind.ToString()).Distinct().ToList(); }
        catch { browsers = []; } // a profile folder that cannot be read: just not named
        var dm = _settings.DownloadManager ? true : _welcomeDm;
        var json = JsonSerializer.Serialize(new { t = "welcome", isNew = _settings.IsNew, browsers, isDefault = DefaultBrowser.IsDefault(), dm, sort = _settings.SortByType });
        try { core.PostWebMessageAsJson(json); } catch { /* the page went away */ }
    }

    /// <summary>Back from Windows' Settings (choosing the default browser): the page shows the new state.</summary>
    private void RefreshWelcome()
    {
        if (Active?.View?.CoreWebView2 is { } core && IsWelcome(core)) SendWelcome(core);
    }

    /// <summary>For tests: whether the welcome is due for this window's profile.</summary>
    internal bool WelcomeIsDue => _settings.WelcomeSeen < WelcomeVersion;
    internal Task OpenWelcomeForTestAsync() => OpenWelcomeAsync();
}
