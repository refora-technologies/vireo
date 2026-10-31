using Vireo.Services;
using Xunit;

namespace Vireo.Tests;

public class UrlInputTests
{
    [Theory]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("http://example.com", "http://example.com")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("file:///C:/x.html", "file:///C:/x.html")]
    [InlineData("example.com", "https://example.com")]
    [InlineData("www.example.co.uk/path?q=1", "https://www.example.co.uk/path?q=1")]
    [InlineData("example.com:8080/x", "https://example.com:8080/x")]
    [InlineData("localhost", "http://localhost")]
    [InlineData("localhost:4173/pages/file.html", "http://localhost:4173/pages/file.html")]
    [InlineData("127.0.0.1:4173", "http://127.0.0.1:4173")]
    [InlineData("  example.com  ", "https://example.com")]
    public void Opens_things_that_are_addresses(string typed, string expected) =>
        Assert.Equal(expected, UrlInput.Resolve(typed));

    [Theory]
    [InlineData("hello world", "https://www.google.com/search?q=hello%20world")]
    [InlineData("cats", "https://www.google.com/search?q=cats")]
    [InlineData("what is c#", "https://www.google.com/search?q=what%20is%20c%23")]
    [InlineData("a.b", "https://www.google.com/search?q=a.b")] // one-letter ending is not a domain
    [InlineData("version 1.2.3 notes", "https://www.google.com/search?q=version%201.2.3%20notes")]
    public void Searches_everything_else(string typed, string expected) =>
        Assert.Equal(expected, UrlInput.Resolve(typed));

    [Fact]
    public void Empty_input_opens_nothing()
    {
        Assert.Null(UrlInput.Resolve(""));
        Assert.Null(UrlInput.Resolve("   "));
        Assert.Null(UrlInput.Resolve(null));
    }

    [Fact]
    public void Uses_the_chosen_search_engine() =>
        Assert.Equal("https://example.org/s?q=a%20b", UrlInput.Resolve("a b", "https://example.org/s?q={0}"));

    [Theory]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("https://www.example.com/a/b?c=1", "www.example.com/a/b?c=1")]
    [InlineData("http://example.com/", "http://example.com")]
    [InlineData("http://localhost:4173/pages/x.html", "http://localhost:4173/pages/x.html")]
    [InlineData("about:blank", "")]
    [InlineData("", "")]
    public void Shows_a_short_address(string url, string shown) =>
        Assert.Equal(shown, UrlInput.Display(url));
}

public class SuspensionPolicyTests
{
    private static readonly SuspensionSettings S = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
    private static readonly TabFacts Plain = new(false, false, true);

    [Fact]
    public void Active_tabs_are_never_touched() =>
        Assert.Equal(TabAction.None, SuspensionPolicy.Decide(TabLife.Active, TimeSpan.FromHours(5), Plain, S));

    [Fact]
    public void A_tab_hidden_briefly_is_left_alone() =>
        Assert.Equal(TabAction.None, SuspensionPolicy.Decide(TabLife.Background, TimeSpan.FromSeconds(29), Plain, S));

    [Fact]
    public void A_tab_hidden_a_while_is_frozen() =>
        Assert.Equal(TabAction.Suspend, SuspensionPolicy.Decide(TabLife.Background, TimeSpan.FromSeconds(30), Plain, S));

    [Fact]
    public void A_frozen_tab_is_not_frozen_again() =>
        Assert.Equal(TabAction.None, SuspensionPolicy.Decide(TabLife.Suspended, TimeSpan.FromMinutes(5), Plain, S));

    [Theory]
    [InlineData(TabLife.Background)]
    [InlineData(TabLife.Suspended)]
    public void A_tab_hidden_long_is_freed(TabLife life) =>
        Assert.Equal(TabAction.Discard, SuspensionPolicy.Decide(life, TimeSpan.FromMinutes(10), Plain, S));

    [Fact]
    public void A_freed_tab_stays_freed() =>
        Assert.Equal(TabAction.None, SuspensionPolicy.Decide(TabLife.Discarded, TimeSpan.FromDays(1), Plain, S));

    [Theory]
    [InlineData(true, false, false)] // playing audio
    [InlineData(false, true, false)] // still loading
    [InlineData(false, false, true)] // pinned
    public void Music_loading_and_pinned_tabs_are_protected(bool audio, bool loading, bool pinned)
    {
        var facts = new TabFacts(audio, loading, true, pinned);
        Assert.Equal(TabAction.None, SuspensionPolicy.Decide(TabLife.Background, TimeSpan.FromDays(1), facts, S));
    }

    [Fact]
    public void A_tab_with_no_page_is_frozen_but_never_freed()
    {
        var blank = new TabFacts(false, false, false);
        Assert.Equal(TabAction.Suspend, SuspensionPolicy.Decide(TabLife.Background, TimeSpan.FromHours(1), blank, S));
    }

    [Fact]
    public void Can_be_switched_off() =>
        Assert.Equal(TabAction.None, SuspensionPolicy.Decide(TabLife.Background, TimeSpan.FromDays(1), Plain, S with { Enabled = false }));

    [Fact]
    public void Low_memory_machines_get_the_faster_settings()
    {
        Assert.Same(SuspensionSettings.LowEnd, SuspensionPolicy.ForMemory(4L * 1024 * 1024 * 1024));
        Assert.Same(SuspensionSettings.LowEnd, SuspensionPolicy.ForMemory(2L * 1024 * 1024 * 1024));
        Assert.Same(SuspensionSettings.Normal, SuspensionPolicy.ForMemory(16L * 1024 * 1024 * 1024));
        Assert.Same(SuspensionSettings.Normal, SuspensionPolicy.ForMemory(0));
    }
}
