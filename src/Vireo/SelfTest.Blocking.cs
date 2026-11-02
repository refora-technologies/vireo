using System.Net;
using System.Text.Json;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>A small shop page that tries to load an ad, a tracker and its own script.</summary>
    private sealed class AdSite : IDisposable
    {
        private readonly HttpListener _listener = new();
        public Dictionary<string, int> Hits { get; } = [];
        public string Base { get; } = "";

        public AdSite()
        {
            for (var port = 50000 + Random.Shared.Next(10000); ; port++)
            {
                try { _listener.Prefixes.Add($"http://127.0.0.1:{port}/"); _listener.Start(); Base = $"http://127.0.0.1:{port}"; break; }
                catch (HttpListenerException) { _listener.Prefixes.Clear(); }
            }
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _listener.GetContextAsync(); } catch { return; }
                    var path = c.Request.Url!.AbsolutePath;
                    lock (Hits) Hits[path] = Hits.GetValueOrDefault(path) + 1;
                    var (type, body) = path switch
                    {
                        "/ads.html" => ("text/html", """
                            <!doctype html><html><head><title>Shop</title>
                            <style>body{background:#fff;color:#000;font:16px sans-serif}</style>
                            <script src="/ads/banner.js"></script>
                            <script src="/app.js"></script></head><body>
                            <img src="/tracker/pixel.gif" width="1" height="1">
                            <div class="ad-banner" id="b1">Buy now! Advert</div>
                            <div class="content" id="c1">Real content</div>
                            </body></html>
                            """),
                        "/app.js" => ("application/javascript", "window.appLoaded = true;"),
                        "/ads/banner.js" => ("application/javascript", "window.adLoaded = true;"),
                        "/tracker/pixel.gif" => ("image/gif", "GIF89a"),
                        _ => ("text/plain", ""),
                    };
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    c.Response.ContentType = type;
                    c.Response.ContentLength64 = bytes.Length;
                    try { await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close(); } catch { /* gone */ }
                }
            });
        }

        public int Count(string path) { lock (Hits) return Hits.GetValueOrDefault(path); }
        public void Reset() { lock (Hits) Hits.Clear(); }
        public void Dispose() => _listener.Close();
    }

    private async Task<(bool App, bool Ad, string Banner, string Content)> ProbePageAsync(Models.BrowserTab tab)
    {
        var raw = await w.EvalAsync(tab, "JSON.stringify({app: window.appLoaded === true, ad: window.adLoaded === true, banner: getComputedStyle(document.getElementById('b1')).display, content: getComputedStyle(document.getElementById('c1')).display})");
        using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw)!);
        var j = doc.RootElement;
        return (j.GetProperty("app").GetBoolean(), j.GetProperty("ad").GetBoolean(), j.GetProperty("banner").GetString()!, j.GetProperty("content").GetString()!);
    }

    private async Task RunBlockingStepsAsync()
    {
        using var site = new AdSite();
        w.Settings.BlockAds = true;
        w.Settings.AdAllowSites.Clear();
        w.Settings.Commit();
        w.AddFilterRules("/ads/banner.js\n/tracker/pixel.gif");

        var tab = await w.OpenTabAsync(site.Base + "/ads.html");
        await WaitFor(() => !tab.IsLoading && tab.Title == "Shop");
        await Task.Delay(1500);
        var page = await ProbePageAsync(tab);
        Check("blocked requests never reach the server", site.Count("/ads/banner.js") == 0 && site.Count("/tracker/pixel.gif") == 0, $"ad {site.Count("/ads/banner.js")}, tracker {site.Count("/tracker/pixel.gif")}");
        Check("the page's own script still runs", site.Count("/app.js") == 1 && page.App);
        Check("the blocked ad script did not run", !page.Ad);
        Check("the leftover ad box is hidden", page.Banner == "none", page.Banner);
        Check("real content is untouched", page.Content == "block", page.Content);
        Check("the shield counts what was stopped", tab.Blocked == 2 && w.ShieldText == "2", $"{tab.Blocked} blocked, shield '{w.ShieldText}'");

        w.ShowShield();
        await Task.Delay(700);
        await Shot("15-privacy-panel");
        w.CloseDrawer();
        await Task.Delay(400);

        // Turning blocking off for this site lets everything load.
        w.Settings.AdAllowSites.Add("127.0.0.1");
        w.Settings.Commit();
        site.Reset();
        await w.ReloadAsync(tab);
        await Task.Delay(1500);
        page = await ProbePageAsync(tab);
        Check("allowing a site lets its ads load", site.Count("/ads/banner.js") == 1 && page.Ad && tab.Blocked == 0, $"ad hits {site.Count("/ads/banner.js")}, blocked {tab.Blocked}");
        Check("and its ad box shows again", page.Banner != "none", page.Banner);

        w.Settings.AdAllowSites.Clear();
        w.Settings.BlockAds = false;
        w.Settings.Commit();
        site.Reset();
        await w.ReloadAsync(tab);
        await Task.Delay(1200);
        Check("the global switch turns blocking off everywhere", site.Count("/ads/banner.js") == 1 && tab.Blocked == 0);

        w.Settings.BlockAds = true;
        w.Settings.Commit();
        site.Reset();
        await w.ReloadAsync(tab);
        await Task.Delay(1500);
        Check("and back on", site.Count("/ads/banner.js") == 0 && tab.Blocked == 2, $"blocked {tab.Blocked}");
    }
}
