using System.Text.Json;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>
    /// YouTube's own ads, on a local page that imitates the parts of YouTube that carry them (the script takes that page for
    /// YouTube only in self-test mode): the player data loses its ads (in the page, parsed, fetched), other data is left
    /// alone, ad boxes are hidden, an ad that still plays is skipped or hurried past without sound and the video then plays
    /// normally, the "ad blockers are not allowed" box goes, the shield counts it, and allowing ads on YouTube takes it all away.
    /// </summary>
    private async Task RunYouTubeAdStepsAsync()
    {
        w.Settings.BlockAds = true;
        w.Settings.AdAllowSites.Remove("youtube.com");
        w.Settings.Commit();
        var tab = await w.OpenTabAsync(baseUrl + "/pages/yt-ads.html");
        await WaitFor(() => !tab.IsLoading && tab.Title.Length > 0);
        Check("with ads blocked, the tab has the YouTube script", MainWindow.HasYouTubeAdsScript(tab));
        await Task.Delay(1500);

        async Task<JsonElement> Read(string script)
        {
            var raw = await w.EvalAsync(tab, $"JSON.stringify({script})");
            try { return JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw) ?? "null").RootElement.Clone(); }
            catch { return default; }
        }
        bool Is(JsonElement e, string path, bool value)
        {
            foreach (var part in path.Split('.')) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(part, out e)) return false; }
            return e.ValueKind == (value ? JsonValueKind.True : JsonValueKind.False);
        }

        var r = await Read("window.__r");
        Check("the player data in the page loses its ads and the warning", Is(r, "inline.ads", false) && Is(r, "inline.enforcement", false) && Is(r, "inline.stream", true), r.ToString());
        Check("player data read later loses its ads, and keeps the video", Is(r, "parsed.ads", false) && Is(r, "parsed.video", true), r.ToString());
        Check("player data fetched from the server loses its ads, and keeps the video", Is(r, "fetched.ads", false) && Is(r, "fetched.stream", true), r.ToString());
        Check("other data on the page is left alone", Is(r, "other", true), r.ToString());

        var boxes = await Read("[getComputedStyle(document.getElementById('slot')).display, getComputedStyle(document.getElementById('masthead-ad')).display, getComputedStyle(document.getElementById('content')).display]");
        Check("ad boxes in the page are hidden, the content is not", boxes.ToString() == "[\"none\",\"none\",\"block\"]", boxes.ToString());

        var ad = await Read("{ skipped: window.__skipped || 0, rate: document.getElementById('video').playbackRate, muted: document.getElementById('video').muted }");
        Check("an ad that still plays: Skip is pressed, and it runs fast without sound",
            ad.TryGetProperty("skipped", out var sk) && sk.GetInt32() >= 1 && ad.GetProperty("rate").GetDouble() == 16 && ad.GetProperty("muted").GetBoolean(), ad.ToString());
        await w.EvalAsync(tab, "document.getElementById('player').classList.remove('ad-showing')");
        await Task.Delay(900);
        var after = await Read("{ rate: document.getElementById('video').playbackRate, muted: document.getElementById('video').muted }");
        Check("after the ad the video plays at its own speed, with its sound", after.GetProperty("rate").GetDouble() == 1 && !after.GetProperty("muted").GetBoolean(), after.ToString());
        var gone = await Read("[!!document.getElementById('dialog'), !!document.getElementById('backdrop')]");
        Check("the \"ad blockers are not allowed\" box is gone, with its backdrop", gone.ToString() == "[false,false]", gone.ToString());
        await WaitFor(() => tab.BlockedAds >= 2, 3000);
        Check("the shield counts YouTube's ads", tab.BlockedAds >= 2, $"{tab.BlockedAds} ads");
        await Shot("80-youtube-ads");

        // Allowing ads on YouTube takes the script away (from the next page on).
        w.Settings.AdAllowSites.Add("youtube.com");
        w.Settings.Commit();
        await WaitFor(() => !MainWindow.HasYouTubeAdsScript(tab), 5000);
        await w.ReloadAsync(tab);
        await Task.Delay(1200);
        var off = await Read("{ script: typeof window.__vireoYtStats, ads: window.__r.inline.ads }");
        Check("with ads allowed on YouTube, nothing is touched", !MainWindow.HasYouTubeAdsScript(tab) && off.GetProperty("script").GetString() == "undefined" && off.GetProperty("ads").GetBoolean(), off.ToString());
        w.Settings.AdAllowSites.Remove("youtube.com");
        w.Settings.Commit();
        await WaitFor(() => MainWindow.HasYouTubeAdsScript(tab), 5000);
        Check("and blocking them again brings it back", MainWindow.HasYouTubeAdsScript(tab));
        await w.CloseTabAsync(tab);
    }
}
