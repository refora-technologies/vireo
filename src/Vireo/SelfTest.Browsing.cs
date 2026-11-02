using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Vireo.Downloads;
using Vireo.Models;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>A tiny web server for pages the test writes itself.</summary>
    private sealed class PageSite : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, (string Type, byte[] Body)> _pages = [];
        public string Base { get; } = "";
        /// <summary>The same site under another name: to the browser it is a different site.</summary>
        public string Local { get; } = "";
        /// <summary>Gets first look at each request; return true when it answered itself.</summary>
        public Func<HttpListenerContext, bool>? Intercept { get; set; }

        public PageSite()
        {
            for (var port = 40000 + Random.Shared.Next(9000); ; port++)
            {
                try { _listener.Prefixes.Add($"http://127.0.0.1:{port}/"); _listener.Prefixes.Add($"http://localhost:{port}/"); _listener.Start(); Base = $"http://127.0.0.1:{port}"; Local = $"http://localhost:{port}"; break; }
                catch (HttpListenerException) { _listener.Prefixes.Clear(); }
            }
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _listener.GetContextAsync(); } catch { return; }
                    c.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    if (Intercept?.Invoke(c) == true) { try { c.Response.Close(); } catch { /* gone */ } continue; }
                    (string Type, byte[] Body) page;
                    lock (_pages) page = _pages.TryGetValue(c.Request.Url!.AbsolutePath, out var p) ? p : ("text/plain", []);
                    c.Response.ContentType = page.Type;
                    c.Response.ContentLength64 = page.Body.Length;
                    try { await c.Response.OutputStream.WriteAsync(page.Body); c.Response.Close(); } catch { /* gone */ }
                }
            });
        }

        public PageSite Html(string path, string html) => Add(path, "text/html", Encoding.UTF8.GetBytes(html));
        public PageSite Add(string path, string type, byte[] body) { lock (_pages) _pages[path] = (type, body); return this; }
        public void Dispose() => _listener.Close();
    }

    /// <summary>A real mouse click inside the page, which counts as the person clicking (pop-ups and downloads need that).</summary>
    private async Task<bool> RealClickAsync(BrowserTab tab, string selector)
    {
        var json = await w.EvalAsync(tab, $"(() => {{ const e = document.querySelector('{selector}'); if (!e) return null; const r = e.getBoundingClientRect(); return JSON.stringify({{ x: r.left + r.width / 2, y: r.top + r.height / 2 }}); }})()");
        if (json == "null" || tab.View?.CoreWebView2 is not { } core) return false;
        using var doc = JsonDocument.Parse(JsonDocument.Parse(json).RootElement.GetString()!);
        var x = doc.RootElement.GetProperty("x").GetDouble();
        var y = doc.RootElement.GetProperty("y").GetDouble();
        foreach (var type in new[] { "mousePressed", "mouseReleased" })
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = "left", clickCount = 1 }));
        return true;
    }

    private async Task RunBrowsingStepsAsync()
    {
        var folder = Path.Combine(outDir, "browsing-downloads");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        w.Settings.DownloadFolder = folder;
        w.Settings.BlockAds = false;
        w.Settings.BlockPopups = true;
        w.Settings.Commit();
        foreach (var old in w.Downloads.Items.ToList()) w.Downloads.Remove(old);

        using var site = new PageSite();
        site.Html("/grab.html", """
            <!doctype html><html><head><title>Files</title></head><body>
            <img src="/pics/a.png" alt="first"><img src="/pics/b.jpg" alt="second">
            <a href="/docs/report.pdf">Report</a> <a href="/files/archive.zip">Archive</a> <a href="/next.html">Next page</a> <a href="/app.js">script</a>
            <audio src="/sound/song.mp3" controls></audio>
            </body></html>
            """)
            .Add("/pics/a.png", "image/png", new byte[2000]).Add("/pics/b.jpg", "image/jpeg", new byte[3000])
            .Add("/docs/report.pdf", "application/pdf", new byte[4000]).Add("/files/archive.zip", "application/zip", new byte[5000])
            .Add("/sound/song.mp3", "audio/mpeg", new byte[6000])
            .Html("/popups.html", """
                <!doctype html><html><head><title>Popups</title></head><body>
                <button id="open" style="width:200px;height:60px" onclick="window.open('/target.html','signin','width=420,height=520')">Sign in</button>
                <a id="blank" href="/target.html?link" target="_blank" style="display:block;margin-top:20px">Open in a new tab</a>
                <script>setTimeout(() => window.open('/target.html?uninvited', '_blank'), 400);</script>
                </body></html>
                """)
            .Html("/target.html", "<!doctype html><title>pending</title><script>document.title = window.opener ? 'has-opener' : 'no-opener';</script>")
            .Html("/plain.html", "<!doctype html><title>Plain</title><body><p>hello</p></body>");

        // ---- Page Grabber ----
        var grab = await w.OpenTabAsync(site.Base + "/grab.html");
        await WaitFor(() => !grab.IsLoading && grab.Title == "Files");
        await w.ShowBatchGrabberAsync();
        await Task.Delay(500);
        var items = w.GrabberPanel?.Items ?? [];
        string Names() => string.Join(",", items.Select(i => $"{i.FileName}:{i.Category}"));
        Check("the Page Grabber lists the pictures, documents, archives and sounds", new[] { "/pics/a.png", "/pics/b.jpg", "/docs/report.pdf", "/files/archive.zip", "/sound/song.mp3" }.All(n => items.Any(i => i.Url.EndsWith(n))), Names());
        Check("sorted into their kinds", items.Any(i => i.FileName == "report.pdf" && i.Category == GrabCategory.Documents) && items.Any(i => i.FileName == "archive.zip" && i.Category == GrabCategory.Archives) && items.Any(i => i.FileName == "a.png" && i.Category == GrabCategory.Images), Names());
        Check("and leaves out ordinary pages and scripts", !items.Any(i => i.FileName is "next.html" or "app.js"), Names());
        await Shot("29-page-grabber");
        await HoverEveryButtonAsync("Page Grabber");
        if (w.GrabberPanel is { } panel) await panel.DownloadForTestAsync();
        await WaitFor(() => w.Downloads.Items.Count(i => i.State == DownloadState.Completed) >= 5, 20000);
        var saved = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
        Check("Download Selected saves them all", new[] { "a.png", "archive.zip", "b.jpg", "report.pdf" }.All(n => saved.Contains(n)) && saved.Any(n => n!.EndsWith(".mp3")), string.Join(",", saved));
        w.CloseDrawer();

        // ---- Zoom is remembered per site ----
        var zoomTab = await w.OpenTabAsync(site.Base + "/plain.html");
        await WaitFor(() => !zoomTab.IsLoading && zoomTab.Title == "Plain");
        w.ZoomActive(1.15);
        await Task.Delay(300);
        var zoomed = zoomTab.View!.ZoomFactor;
        Check("zooming in changes the page", zoomed > 1.1, $"{zoomed:0.00}");
        Check("and is kept for the site", w.Settings.SiteZoom.TryGetValue("127.0.0.1", out var kept) && Math.Abs(kept - zoomed) < 0.01, JsonSerializer.Serialize(w.Settings.SiteZoom));
        var again = await w.OpenTabAsync(site.Base + "/plain.html");
        await WaitFor(() => !again.IsLoading && again.Title == "Plain");
        await Task.Delay(300);
        Check("another tab on the same site opens at that zoom", Math.Abs(again.View!.ZoomFactor - zoomed) < 0.01, $"{again.View.ZoomFactor:0.00}");
        w.ResetZoomActive();
        await Task.Delay(300);
        Check("resetting forgets it", Math.Abs(again.View.ZoomFactor - 1) < 0.01 && w.Settings.SiteZoom.Count == 0, JsonSerializer.Serialize(w.Settings.SiteZoom));

        // ---- Pinning keeps tabs together at the front ----
        var first = w.AllTabs[0];
        var pinMe = await w.OpenTabAsync(site.Base + "/plain.html", activate: false);
        var other = await w.OpenTabAsync(site.Base + "/grab.html", activate: false);
        w.TogglePin(pinMe);
        Check("a pinned tab moves to the front", w.AllTabs[0] == pinMe && pinMe.IsPinned);
        w.TogglePin(other);
        Check("the next pinned tab joins it", w.AllTabs[1] == other, string.Join(",", w.AllTabs.Take(3).Select(t => t.Title)));
        var count = w.AllTabs.Count;
        await w.CloseOthersAsync(first);
        Check("closing the others keeps the pinned ones", w.AllTabs.Contains(pinMe) && w.AllTabs.Contains(other) && w.AllTabs.Contains(first) && w.AllTabs.Count == 3, $"{count} -> {w.AllTabs.Count}");
        w.TogglePin(pinMe);
        w.TogglePin(other);
        Check("unpinning puts them back behind the pinned ones", !pinMe.IsPinned && !other.IsPinned);
        await w.ActivateAsync(first);

        // ---- Pop-ups: only the ones you ask for ----
        var host = await w.OpenTabAsync(site.Base + "/popups.html");
        await WaitFor(() => !host.IsLoading && host.Title == "Popups");
        var before = w.AllTabs.Count;
        var blockedBefore = host.Blocked;
        await Task.Delay(1500); // the page tries to open a window by itself after 400 ms
        Check("a window the page opens by itself is blocked", w.AllTabs.Count == before && w.AllTabs.All(t => !t.Url.Contains("uninvited")), $"{before} -> {w.AllTabs.Count}");
        Check("and counted on the shield", host.Blocked > blockedBefore, $"{blockedBefore} -> {host.Blocked}");

        Check("pressing the Sign in button (a real click)", await RealClickAsync(host, "#open"));
        await WaitFor(() => w.AllTabs.Any(t => t != host && t.Url.Contains("/target.html")), 8000);
        var popup = w.AllTabs.FirstOrDefault(t => t != host && t.Url.Contains("/target.html"));
        Check("opens the window the person asked for", popup is not null && w.AllTabs.Count == before + 1, $"{before} -> {w.AllTabs.Count}");
        if (popup is not null)
        {
            await WaitFor(() => popup.Title is "has-opener" or "no-opener", 6000);
            Check("which stays connected to the page, so sign-in answers can come back", popup.Title == "has-opener", popup.Title);
            Check("and comes to the front", w.Active == popup);
            await Shot("30-popup-tab");
            await w.CloseTabAsync(popup);
        }
        await w.ActivateAsync(host);
        await Task.Delay(1900); // let the chain window pass
        var withLink = w.AllTabs.Count;
        await RealClickAsync(host, "#blank");
        await WaitFor(() => w.AllTabs.Any(t => t.Url.Contains("?link")), 8000);
        Check("a link that opens a new tab works", w.AllTabs.Count == withLink + 1 && w.AllTabs.Any(t => t.Url.Contains("?link")), $"{withLink} -> {w.AllTabs.Count}");
        foreach (var t in w.AllTabs.Where(t => t.Url.Contains("/target.html")).ToList()) await w.CloseTabAsync(t);
    }
}
