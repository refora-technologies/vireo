using Vireo.Models;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>Pages that behave like the worst ad-funded sites: windows nobody asked for, hijacked clicks, invisible layers, dialog floods, redirects.</summary>
    private async Task RunHostileStepsAsync()
    {
        w.Settings.BlockAds = true;
        w.Settings.BlockPopups = true;
        w.Settings.Commit();
        using var site = new PageSite();   // the page the person is on
        using var adnet = new PageSite();  // where the ads come from
        w.AddFilterRules("/redirect.html$document");
        var ad = adnet.Local;

        site.Html("/hostile.html", $$"""
            <!doctype html><html><head><title>Hostile</title></head><body style="margin:0;background:#111;color:#eee;font:16px sans-serif">
            <div id="empty" style="width:500px;height:200px;background:#333">Empty space (a click here is hijacked)</div>
            <button id="legit" style="width:200px;height:50px" onclick="window.open('{{ad}}/signin.html','signin','width=420,height=520')">Sign in</button>
            <a id="honest" href="{{ad}}/link.html" target="_blank" style="display:block;margin-top:20px">A normal link to another site</a>
            <script>
              document.addEventListener('click', () => window.open('{{ad}}/hijack.html'));
              setTimeout(() => window.open('{{ad}}/timed.html'), 200);
              for (let i = 0; i < 8; i++) setTimeout(() => alert('You won! ' + i), 100 + i * 30);
              setTimeout(() => { const a = document.createElement('a'); a.href = '{{ad}}/scripted.html'; a.target = '_blank'; document.body.appendChild(a); a.click(); }, 400);
              setTimeout(() => { document.body.dispatchEvent(new MouseEvent('click')); }, 450);
              Notification.requestPermission();
              setTimeout(() => { location.href = '{{ad}}/redirect.html'; }, 900);
            </script></body></html>
            """);
        site.Html("/layer.html", $$"""
            <!doctype html><html><head><title>Layer</title></head><body style="margin:0;background:#111;color:#eee;font:16px sans-serif">
            <h1>Watch the video</h1><p>A button that is really a trap:</p>
            <a id="trap" href="{{ad}}/layer-ad.html" target="_blank" style="position:fixed;inset:0;opacity:0;z-index:99999;display:block"></a>
            </body></html>
            """);
        foreach (var path in new[] { "/signin.html", "/link.html", "/hijack.html", "/timed.html", "/scripted.html", "/redirect.html", "/layer-ad.html" })
            adnet.Html(path, "<!doctype html><title>" + path[1..^5] + "</title><body>an ad</body>");

        var before = w.AllTabs.Count;
        var tab = await w.OpenTabAsync(site.Base + "/hostile.html");
        await WaitFor(() => !tab.IsLoading && tab.Title == "Hostile");
        await Task.Delay(2600); // everything the page tries on its own happens in this time
        var blocked0 = tab.Blocked;
        Check("windows opened by the page itself or by scripted links are not allowed through", w.AllTabs.Count == before + 1, $"{before} -> {w.AllTabs.Count}: {string.Join(" ", w.AllTabs.Skip(before).Select(t => t.Url))}");
        Check("a page that keeps showing message boxes is cut off", tab.DialogsSuppressed >= 5, $"{tab.DialogsSuppressed} suppressed");
        Check("a redirect to an ad address is refused", tab.Url.Contains("/hostile.html"), tab.Url);
        Check("the page is not allowed to ask for notifications", await w.EvalAsync(tab, "Notification.permission") == "\"denied\"", await w.EvalAsync(tab, "Notification.permission"));
        Check("and the shield counted what it stopped", blocked0 >= 1, $"{blocked0} stopped");
        await Shot("34-hostile-page");

        // A click on empty space makes the page open a window
        await w.ActivateAsync(tab);
        var count = w.AllTabs.Count;
        await RealClickAsync(tab, "#empty");
        await Task.Delay(1200);
        Check("a click on empty space does not open the window the page hides behind it", w.AllTabs.Count == count && tab.Blocked > blocked0, $"{count} -> {w.AllTabs.Count}, stopped {blocked0} -> {tab.Blocked}");

        // Things people really click are allowed
        await Task.Delay(2000);
        await RealClickAsync(tab, "#honest");
        await WaitFor(() => w.AllTabs.Any(t => t.Url.Contains("/link.html")), 8000);
        Check("an ordinary link to another site still opens", w.AllTabs.Any(t => t.Url.Contains("/link.html")));
        await Task.Delay(800);
        Check("but not the window the page tried to open on the back of that click", !w.AllTabs.Any(t => t.Url.Contains("/hijack.html")), string.Join(" ", w.AllTabs.Select(t => t.Url)));
        foreach (var t in w.AllTabs.Where(t => t.Url.Contains("/link.html")).ToList()) await w.CloseTabAsync(t);
        await w.ActivateAsync(tab);
        await Task.Delay(2000);
        await RealClickAsync(tab, "#legit");
        await WaitFor(() => w.AllTabs.Any(t => t.Url.Contains("/signin.html")), 8000);
        Check("and so does a sign-in button's window", w.AllTabs.Any(t => t.Url.Contains("/signin.html")));
        foreach (var t in w.AllTabs.Where(t => t.Url.Contains("/signin.html")).ToList()) await w.CloseTabAsync(t);

        // An invisible sheet over the whole page
        var layer = await w.OpenTabAsync(site.Base + "/layer.html");
        await WaitFor(() => !layer.IsLoading && layer.Title == "Layer");
        await Task.Delay(500);
        count = w.AllTabs.Count;
        var stopped = layer.Blocked;
        await RealClickAsync(layer, "#trap");
        await Task.Delay(1500);
        Check("a click on an invisible layer laid over the page opens nothing", w.AllTabs.Count == count && layer.Blocked > stopped, $"{count} -> {w.AllTabs.Count}, stopped {stopped} -> {layer.Blocked}");
    }
}
