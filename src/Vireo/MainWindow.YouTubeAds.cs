using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

/// <summary>
/// YouTube's own ads (see Assets/youtube.js). The script has to be in place before YouTube's code runs, so it cannot wait to
/// ask whether blocking is on: instead each tab has it only while blocking is on for YouTube, and loses it the moment ads
/// are allowed there (in the shield, or Settings). What it looks for comes from the YouTube rules (YouTubeRulesStore): the
/// copy built into Vireo, or a newer one signed with Refora's key; newer rules replace the script in every tab. Ads it
/// removes count on the shield like any other blocked ad.
/// </summary>
public partial class MainWindow
{
    private static string? s_youTubeTemplate;
    private static YouTubeRulesStore? s_youTubeRules;
    private static (int Version, string Script)? s_youTubeScript;
    private static DispatcherTimer? s_rulesTimer;
    private static readonly HttpClient s_rulesHttp = new() { DefaultRequestHeaders = { { "User-Agent", $"Vireo/{Updater.Label(Updater.Current)}" } } };

    /// <summary>The rules in use (the profile's store, made with the first window).</summary>
    internal static YouTubeRulesStore? YouTubeRulesNow => s_youTubeRules;

    private void StartYouTubeRules()
    {
        if (s_youTubeRules is not null || _private) return;
        s_youTubeRules = new YouTubeRulesStore(Path.Combine(_dataFolder, "rules"), YouTubeRulesStore.BuiltInKey());
        s_youTubeRules.Changed += () => Dispatcher.BeginInvoke(() => { foreach (var w in s_windows) w.UpdateYouTubeAdsEverywhere(); });
        // Newer rules from Vireo's website: a while after starting (not while the first pages load), then twice a day.
        // Nothing about the person is sent: a plain request for one public file. Not in tests: they use their own pages.
        if (SelfTesting) return;
        s_rulesTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(45) };
        s_rulesTimer.Tick += async (_, _) =>
        {
            s_rulesTimer!.Interval = YouTubeRulesStore.CheckEvery;
            if (s_youTubeRules is { } store) await store.CheckAsync(s_rulesHttp);
        };
        s_rulesTimer.Start();
    }

    private static string YouTubeScript()
    {
        var rules = s_youTubeRules?.Current ?? new YouTubeRulesStore(Path.GetTempPath(), null).Current;
        if (s_youTubeScript is { } cached && cached.Version == rules.Version) return cached.Script;
        s_youTubeTemplate ??= ReadAsset("youtube.js");
        if (s_youTubeTemplate.Length == 0) return "";
        var script = s_youTubeTemplate.Replace("/*TEST*/false", SelfTesting ? "true" : "false").Replace("/*RULES*/null", rules.ToScriptJson());
        s_youTubeScript = (rules.Version, script);
        return script;
    }

    private bool YouTubeAdsBlocked => _settings.BlockAds && !IsAllowedSite("https://www.youtube.com/");

    /// <summary>Gives the tab the script (with the current rules), or takes it away, to match the settings; from the next page it loads.</summary>
    private async Task UpdateYouTubeAdsAsync(BrowserTab tab, CoreWebView2 core)
    {
        string? want = YouTubeAdsBlocked ? YouTubeScript() : null;
        if (want is { Length: 0 }) want = null;
        if (want == tab.YouTubeAdsText) return;
        tab.YouTubeAdsText = want;
        try
        {
            if (tab.YouTubeAdsScript is { } old) { tab.YouTubeAdsScript = null; core.RemoveScriptToExecuteOnDocumentCreated(old); }
            if (want is null) return;
            var id = await core.AddScriptToExecuteOnDocumentCreatedAsync(want);
            if (tab.YouTubeAdsText == want && tab.YouTubeAdsScript is null) tab.YouTubeAdsScript = id;
            else core.RemoveScriptToExecuteOnDocumentCreated(id); // changed again while it was being added
        }
        catch { tab.YouTubeAdsText = null; tab.YouTubeAdsScript = null; } // the page went away; set again when it is shown
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

    /// <summary>For tests: whether the tab has the YouTube script now, and with which rules.</summary>
    internal static bool HasYouTubeAdsScript(BrowserTab tab) => tab.YouTubeAdsScript is not null;
}
