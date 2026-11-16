using System.Text.Json;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>What hands expect from a browser: the mouse's back button, pinned start-page sites, Mark of the Web.</summary>
    private async Task RunGestureStepsAsync()
    {
        using var site = new PageSite();
        site.Html("/one.html", "<!doctype html><title>One</title><body style='height:2000px'>one</body>");
        site.Html("/two.html", "<!doctype html><title>Two</title><body style='height:2000px'>two</body>");
        var tab = await w.OpenTabAsync(site.Base + "/one.html");
        await WaitFor(() => !tab.IsLoading && tab.Title == "One");
        w.NavigateActive(site.Base + "/two.html");
        await WaitFor(() => !tab.IsLoading && tab.Title == "Two");

        // The mouse's back button (button 4) pressed over the page, as a person would.
        var core = tab.View!.CoreWebView2;
        foreach (var type in new[] { "mousePressed", "mouseReleased" })
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = 200, y = 200, button = "back", buttons = 8, clickCount = 1 }));
        await WaitFor(() => tab.Title == "One", 4000);
        Check("the mouse's back button goes back", tab.Title == "One", tab.Title);
        await Task.Delay(800);
        Check("and only one page back", tab.Title == "One" && tab.CanGoForward, tab.Title);
        foreach (var type in new[] { "mousePressed", "mouseReleased" })
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = 200, y = 200, button = "forward", buttons = 16, clickCount = 1 }));
        await WaitFor(() => tab.Title == "Two", 4000);
        Check("and the forward button forward", tab.Title == "Two", tab.Title);

        // Pinned start-page sites
        var start = await w.OpenTabAsync(null);
        for (var i = 0; i < 40 && await w.EvalAsync(start, "!!document.getElementById('sites')") != "true"; i++) await Task.Delay(200);
        await Task.Delay(1200);
        await w.EvalAsync(start, "chrome.webview.postMessage({ t: 'site-add', title: 'Example', url: 'example.com' })");
        await WaitFor(() => w.Settings.PinnedSites.Any(p => p.Url == "https://example.com/"), 4000);
        await Task.Delay(1500);
        var tiles = await w.EvalAsync(start, "document.getElementById('sites').textContent");
        Check("a site can be pinned to the start page", w.Settings.PinnedSites.Any(p => p.Title == "Example") && tiles.Contains("Example"), tiles);
        await w.EvalAsync(start, "chrome.webview.postMessage({ t: 'site-add', title: 'Bad', url: 'javascript:alert(1)' })");
        await Task.Delay(800);
        Check("but only a web address", w.Settings.PinnedSites.All(p => p.Url.StartsWith("http")), string.Join(",", w.Settings.PinnedSites.Select(p => p.Url)));
        await Shot("42-start-page-pinned");
        await w.EvalAsync(start, "chrome.webview.postMessage({ t: 'site-remove', url: 'https://example.com/' })");
        await WaitFor(() => w.Settings.PinnedSites.All(p => p.Title != "Example"), 4000);
        Check("and taken off again", w.Settings.PinnedSites.All(p => p.Title != "Example"));
    }
}
