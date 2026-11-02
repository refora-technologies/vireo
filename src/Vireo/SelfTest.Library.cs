namespace Vireo;

internal sealed partial class SelfTest
{
    private async Task RunLibraryStepsAsync()
    {
        using var site = new AdSite();
        w.Settings.BlockAds = false;
        w.Settings.Commit();
        var tab = await w.OpenTabAsync(site.Base + "/ads.html");
        await WaitFor(() => !tab.IsLoading && tab.Title == "Shop");
        await Task.Delay(600);

        // History remembers the page, with its title
        Check("a visited page is remembered with its title", w.Library.History.Any(h => h.Url == site.Base + "/ads.html" && h.Title == "Shop"), $"{w.Library.History.Count} entries");

        // Bookmark star
        Check("the star starts empty", !w.BookmarkFilled);
        w.BookmarkActive();
        await Task.Delay(400);
        Check("pressing the star bookmarks the page", w.Library.IsBookmarked(tab.Url) && w.BookmarkFilled);
        await Shot("18-bookmarked");

        // Suggestions while typing
        w.TypeInAddress("sho");
        await Task.Delay(500);
        Check("typing offers matching pages", w.SuggestionCount >= 1 && w.FirstSuggestionUrl.StartsWith(site.Base), $"{w.SuggestionCount} suggestions, first {w.FirstSuggestionUrl}");
        Check("a bookmarked page is marked as one", w.FirstSuggestionIsBookmark);
        await Shot("19-suggestions");
        w.ClearAddressFocus();
        await Task.Delay(400);
        Check("suggestions go away when you leave the address bar", w.SuggestionCount == 0 || !w.SuggestionsOpen);

        // The panels
        w.ShowBookmarks();
        await Task.Delay(700);
        await Shot("20-bookmarks");
        w.ShowHistory();
        await Task.Delay(500);
        await Shot("21-history");
        w.CloseDrawer();
        await Task.Delay(400);

        // Find in the page
        w.ShowFind();
        await Task.Delay(300);
        w.TypeInFind("content");
        await Task.Delay(900);
        Check("find counts the matches in the page", w.FindCountText == "1 of 1", w.FindCountText);
        await Shot("22-find");
        w.CloseFind();
        Check("and closes", !w.FindOpen);

        // Pressing the star again removes it
        w.BookmarkActive();
        await Task.Delay(300);
        Check("pressing the star again removes the bookmark", !w.Library.IsBookmarked(tab.Url) && !w.BookmarkFilled);
    }
}
