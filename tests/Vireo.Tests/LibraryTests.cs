using System.IO;
using Vireo.Services;
using Xunit;

namespace Vireo.Tests;

public class LibraryTests
{
    private static string Folder() => Path.Combine(AppContext.BaseDirectory, "tmp", Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_page_is_bookmarked_and_unbookmarked_with_the_same_action()
    {
        using var lib = new Library(null);
        Assert.False(lib.IsBookmarked("https://a.test/x"));
        Assert.True(lib.ToggleBookmark("https://a.test/x", "A page"));
        Assert.True(lib.IsBookmarked("https://a.test/x"));
        Assert.True(lib.IsBookmarked("https://a.test/x#section"), "the part after # does not make it a different page");
        Assert.False(lib.ToggleBookmark("https://a.test/x/", "A page"));
        Assert.False(lib.IsBookmarked("https://a.test/x"));
    }

    [Fact]
    public void Visiting_a_page_again_counts_it_and_keeps_one_entry()
    {
        using var lib = new Library(null);
        lib.Visit("https://news.test/a", "News");
        lib.Visit("https://news.test/a#top", "News: today");
        lib.Visit("https://other.test/", "Other");
        var h = lib.History;
        Assert.Equal(2, h.Count);
        var news = h.Single(e => e.Url.Contains("news"));
        Assert.Equal(2, news.Visits);
        Assert.Equal("News: today", news.Title);
    }

    [Fact]
    public void Only_web_pages_are_remembered()
    {
        using var lib = new Library(null);
        lib.Visit("about:blank", "x");
        lib.Visit("data:text/html,hi", "x");
        lib.Visit("file:///C:/a.html", "x");
        Assert.Empty(lib.History);
    }

    [Fact]
    public void Newest_history_comes_first_and_clearing_empties_it()
    {
        using var lib = new Library(null);
        lib.Visit("https://old.test/", "Old", new DateTime(2026, 1, 1));
        lib.Visit("https://new.test/", "New", new DateTime(2026, 6, 1));
        Assert.Equal("New", lib.History[0].Title);
        lib.RemoveHistory("https://new.test/");
        Assert.Single(lib.History);
        lib.ClearHistory();
        Assert.Empty(lib.History);
    }

    [Fact]
    public void Suggestions_match_every_word_in_the_title_or_address_and_favour_bookmarks_and_site_names()
    {
        using var lib = new Library(null);
        lib.Visit("https://github.com/dotnet/runtime", "dotnet/runtime: .NET runtime", DateTime.Now.AddDays(-5));
        lib.Visit("https://example.org/github-tips", "Tips for using git", DateTime.Now);
        lib.Visit("https://cooking.test/", "Recipes", DateTime.Now);
        lib.ToggleBookmark("https://github.com/dotnet/runtime", "dotnet/runtime");

        var hits = lib.Search("github");
        Assert.Equal(2, hits.Count);
        Assert.Equal("https://github.com/dotnet/runtime", hits[0].Url);
        Assert.True(hits[0].IsBookmark);

        Assert.Single(lib.Search("dotnet runtime"));
        Assert.Empty(lib.Search("zebra"));
        Assert.Empty(lib.Search("   "));
    }

    [Fact]
    public void History_is_capped_and_drops_the_oldest()
    {
        using var lib = new Library(null);
        var start = new DateTime(2026, 1, 1);
        lib.Import([], Enumerable.Range(0, Library.HistoryLimit).Select(i => new HistoryEntry($"https://site{i}.test/", $"Site {i}", start.AddMinutes(i), 1)));
        for (var i = 0; i < 100; i++) lib.Visit($"https://new{i}.test/", $"New {i}", start.AddYears(1).AddMinutes(i));
        Assert.Equal(Library.HistoryLimit, lib.History.Count);
        Assert.DoesNotContain(lib.History, h => h.Url == "https://site0.test/");
        Assert.Contains(lib.History, h => h.Url == "https://new99.test/");
    }

    [Fact]
    public void Everything_survives_closing_and_opening_again()
    {
        var folder = Folder();
        using (var lib = new Library(folder))
        {
            lib.ToggleBookmark("https://keep.test/", "Keep me");
            lib.Visit("https://seen.test/", "Seen");
        } // disposing saves
        using var again = new Library(folder);
        Assert.True(again.IsBookmarked("https://keep.test/"));
        Assert.Equal("Seen", Assert.Single(again.History).Title);
        try { Directory.Delete(folder, true); } catch { /* still closing */ }
    }

    [Fact]
    public void A_private_library_writes_nothing()
    {
        var folder = Folder();
        using (var lib = new Library(null))
        {
            lib.ToggleBookmark("https://x.test/", "x");
            lib.Visit("https://x.test/", "x");
        }
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void A_damaged_file_starts_empty_instead_of_crashing()
    {
        var folder = Folder();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "history.json"), "{ this is not json");
        using var lib = new Library(folder);
        Assert.Empty(lib.History);
        lib.Visit("https://ok.test/", "Fine");
        Assert.Single(lib.History);
        try { Directory.Delete(folder, true); } catch { /* still closing */ }
    }
}
