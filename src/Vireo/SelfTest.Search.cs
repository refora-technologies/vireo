using Vireo.Services;

namespace Vireo;

internal sealed partial class SelfTest
{
    private async Task RunSearchStepsAsync()
    {
        Check("Google is the default search engine", new Settings().SearchTemplate.Contains("google.com/search"), new Settings().SearchTemplate);
        w.Settings.SearchTemplate = UrlInput.DefaultSearch;
        w.Settings.Commit();
        Check("typed words go to Google", w.TypedTarget("blue whales")?.StartsWith("https://www.google.com/search?q=blue%20whales") == true, w.TypedTarget("blue whales"));
        Check("an address is still opened as an address", w.TypedTarget("example.com/a") == "https://example.com/a");

        // Switching in Settings changes where the next search goes, with no restart.
        w.ShowSettings();
        await Task.Delay(500);
        Check("the settings list the engines", w.PickInPanel("Bing"));
        Check("picking Bing sends searches to Bing", w.TypedTarget("blue whales")?.StartsWith("https://www.bing.com/search?q=blue%20whales") == true, w.TypedTarget("blue whales"));
        Check("and it is remembered", w.Settings.SearchTemplate.Contains("bing.com"), w.Settings.SearchTemplate);
        w.PickInPanel("DuckDuckGo");
        Check("DuckDuckGo works too", w.TypedTarget("blue whales")?.StartsWith("https://duckduckgo.com/?q=blue%20whales") == true, w.TypedTarget("blue whales"));
        w.PickInPanel("Brave");
        Check("and Brave", w.TypedTarget("blue whales")?.StartsWith("https://search.brave.com/search?q=blue%20whales") == true, w.TypedTarget("blue whales"));
        w.PickInPanel("Google");
        Check("and back to Google", w.TypedTarget("blue whales")?.StartsWith("https://www.google.com/search?q=") == true);

        // Ideas while typing: a word with no history still gets the engine's suggestions (this used to cancel its own request)
        w.SuggestSource = (q, _) => Task.FromResult(new List<string> { q + " begins", q + " movies", q + " arkham" });
        w.TypeInAddress("batman");
        await WaitFor(() => w.SuggestionCount >= 4, 4000);
        var titles = string.Join(" | ", w.Suggestions.Select(s => s.Title));
        Check("typing a new word offers what was typed, then the engine's ideas", w.SuggestionCount == 4 && w.Suggestions[0] is { IsSearch: true, Title: "batman" } && w.Suggestions.Skip(1).All(s => s.IsSearch), titles);
        w.ClearAddressFocus();
        w.SuggestSource = null;
        using (var probe = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            var online = false;
            try { online = (await probe.GetAsync("https://suggestqueries.google.com/complete/search?client=chrome&q=a")).IsSuccessStatusCode; } catch { /* offline */ }
            if (online)
            {
                w.Settings.SearchTemplate = UrlInput.DefaultSearch;
                w.TypeInAddress("batman");
                await WaitFor(() => w.SuggestionCount >= 4, 8000);
                Check("and the real search engine answers too", w.SuggestionCount >= 4, string.Join(" | ", w.Suggestions.Select(s => s.Title)));
                await Shot("33-suggestions-search");
                w.ClearAddressFocus();
            }
            else Console.WriteLine("SKIP  real suggestion check (offline)");
        }

        // The page that opens in a new tab asks the browser, so it follows the choice too.
        w.PickInPanel("Bing");
        w.CloseDrawer();
        var tab = await w.OpenTabAsync(null);
        for (var i = 0; i < 40 && await w.EvalAsync(tab, "!!document.getElementById('search')") != "true"; i++) await Task.Delay(200); // wait for the page itself
        await w.EvalAsync(tab, "(() => { const f = document.getElementById('search'); f.q.value = 'tidal pools'; f.requestSubmit(); })()");
        await WaitFor(() => tab.Url.Contains("bing.com"), 8000);
        Check("the start page's search box follows the choice", tab.Url.Contains("bing.com") && tab.Url.Contains("tidal"), tab.Url);
        w.Settings.SearchTemplate = UrlInput.DefaultSearch;
        w.Settings.Commit();
    }
}
