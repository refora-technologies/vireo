using System.Diagnostics;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

internal sealed partial class SelfTest
{
    private async Task WaitTitle(BrowserTab tab, string title, int ms = 15000)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            if (tab.View?.CoreWebView2 is not null && await w.EvalAsync(tab, "document.title") == $"\"{title}\"") return;
            await Task.Delay(250);
        }
    }

    private async Task RunRecoveryStepsAsync()
    {
        using var site = new PageSite().Html("/plain.html", "<!doctype html><title>Plain</title><body><p>hello</p></body>");

        // ---- Clearing data by hand ----
        var tab = await w.OpenTabAsync(site.Base + "/plain.html");
        await WaitTitle(tab, "Plain");
        await w.EvalAsync(tab, "document.cookie = 'vireo=1; path=/'");
        Check("a cookie is set", (await w.EvalAsync(tab, "document.cookie")).Contains("vireo=1")); // cookies ignore ports, so other test sites may have left theirs
        await w.ClearBrowsingDataAsync("cookies");
        await w.ReloadAsync(tab);
        await Task.Delay(300);
        Check("clearing cookies removes it", await w.EvalAsync(tab, "document.cookie") == "\"\"", await w.EvalAsync(tab, "document.cookie"));
        Check("there is history to clear", w.Library.History.Count > 0);
        await w.ClearBrowsingDataAsync("history");
        Check("clearing history empties it", w.Library.History.Count == 0, $"{w.Library.History.Count} left");

        // ---- Starting where you left off ----
        var snapshot = w.CurrentSession();
        Check("the open tabs are summed up for the next start", snapshot.Tabs.Any(t => t.Url.EndsWith("/plain.html")) && snapshot.Tabs.All(t => SessionStore.Restorable(t.Url)), string.Join(" | ", snapshot.Tabs.Select(t => t.Url)));
        var before = w.AllTabs.Count;
        var page = site.Base + "/plain.html";
        await w.RestoreSessionAsync(new SessionState([new SessionTab(page + "?a", "Remembered A", true), new SessionTab(page + "?b", "Remembered B", false)], 1));
        var lazy = w.AllTabs.FirstOrDefault(t => t.Url.EndsWith("?a"));
        var live = w.AllTabs.FirstOrDefault(t => t.Url.EndsWith("?b"));
        Check("a session brings its tabs back", w.AllTabs.Count == before + 2 && lazy is not null && live is not null, $"{before} -> {w.AllTabs.Count}");
        Check("the one you were on loads at once", live is not null && w.Active == live && live.View is not null);
        Check("the others wait, using no memory, under their old title", lazy is { View: null, Life: TabLife.Discarded } && lazy.Title == "Remembered A" && lazy.IsPinned, $"{lazy?.Title} {lazy?.Life}");
        await w.ActivateAsync(lazy!);
        await WaitTitle(lazy!, "Plain");
        Check("and load when clicked", lazy!.View is not null && await w.EvalAsync(lazy, "document.title") == "\"Plain\"");

        // ---- A page crashes ----
        var core = lazy.View!.CoreWebView2;
        var recoveries = w.RendererRecoveries;
        await core.CallDevToolsProtocolMethodAsync("Page.crash", "{}").ContinueWith(_ => { }); // the call never answers: the page is gone
        await WaitFor(() => w.RendererRecoveries > recoveries, 15000);
        Check("a crashed page is noticed and reloaded", w.RendererRecoveries > recoveries);
        await WaitTitle(lazy, "Plain");
        Check("and works again", await w.EvalAsync(lazy, "document.title") == "\"Plain\"");
        await Shot("31-after-page-crash");

        // ---- The whole engine stops ----
        var pid = (int)lazy.View!.CoreWebView2.BrowserProcessId;
        try { Process.GetProcessById(pid).Kill(true); } catch { /* already gone */ }
        await WaitFor(() => w.BrowserRecoveries > 0, 40000);
        Check("when the browser engine is killed Vireo starts a new one", w.BrowserRecoveries > 0);
        await WaitTitle(lazy, "Plain", 30000);
        Check("the tab you were on comes back", w.Active == lazy && lazy.View?.CoreWebView2 is not null && await w.EvalAsync(lazy, "document.title") == "\"Plain\"");
        Check("with a different engine process", lazy.View!.CoreWebView2.BrowserProcessId != pid);
        Check("and the other tabs are asleep until clicked", w.AllTabs.Where(t => t != lazy).All(t => t.View is null));
        await Shot("32-after-engine-restart");
    }
}
