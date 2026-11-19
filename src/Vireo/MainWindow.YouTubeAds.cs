using Microsoft.Web.WebView2.Core;
using Vireo.Models;

namespace Vireo;

/// <summary>
/// YouTube's own ads (see Assets/youtube.js). The script has to be in place before YouTube's code runs, so it cannot wait to
/// ask whether blocking is on: instead each tab has it only while blocking is on for YouTube, and loses it the moment ads
/// are allowed there (in the shield, or Settings). Ads it removes count on the shield like any other blocked ad.
/// </summary>
public partial class MainWindow
{
    private static string? s_youTubeScript;

    private static string YouTubeScript => s_youTubeScript ??= ReadAsset("youtube.js").Replace("/*TEST*/false", SelfTesting ? "true" : "false");

    private bool YouTubeAdsBlocked => _settings.BlockAds && !IsAllowedSite("https://www.youtube.com/");

    /// <summary>Gives the tab the script, or takes it away, to match the settings (it applies from the next page it loads).</summary>
    private async Task UpdateYouTubeAdsAsync(BrowserTab tab, CoreWebView2 core)
    {
        var want = YouTubeAdsBlocked && YouTubeScript.Length > 0;
        if (want == tab.YouTubeAdsWanted) return;
        tab.YouTubeAdsWanted = want;
        try
        {
            if (want)
            {
                var id = await core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeScript);
                if (tab.YouTubeAdsWanted == true) tab.YouTubeAdsScript = id;
                else core.RemoveScriptToExecuteOnDocumentCreated(id); // switched off while it was being added
            }
            else if (tab.YouTubeAdsScript is { } id)
            {
                tab.YouTubeAdsScript = null;
                core.RemoveScriptToExecuteOnDocumentCreated(id);
            }
        }
        catch { tab.YouTubeAdsWanted = null; } // the page went away; tried again when it is shown
    }

    private void UpdateYouTubeAdsEverywhere()
    {
        foreach (var tab in _tabs)
            if (tab.View?.CoreWebView2 is { } core) _ = UpdateYouTubeAdsAsync(tab, core);
    }

    /// <summary>An ad YouTube's page would have shown: counted once on the shield. Only YouTube's own pages are listened to.</summary>
    private bool OnYouTubeAdMessage(BrowserTab tab, CoreWebView2 core, string type, System.Text.Json.JsonElement root)
    {
        if (type != "yt-ad") return false;
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var page)) return true;
        var youTube = page.Host == "youtube.com" || page.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
            || (SelfTesting && page.AbsolutePath.StartsWith("/pages/yt-", StringComparison.Ordinal));
        if (!youTube) return true;
        var n = root.TryGetProperty("n", out var nn) && nn.TryGetInt32(out var v) ? v : 0;
        var video = System.Web.HttpUtility.ParseQueryString(page.Query)["v"] ?? page.AbsolutePath;
        if (tab.CountBlocked($"https://www.youtube.com/vireo-ad/{Uri.EscapeDataString(video)}/{n}", "youtube.com (its own ads)", 0) && tab == Active) QueueShieldUpdate();
        return true;
    }

    /// <summary>For tests: whether the tab has the YouTube script now.</summary>
    internal static bool HasYouTubeAdsScript(BrowserTab tab) => tab.YouTubeAdsScript is not null;
}
