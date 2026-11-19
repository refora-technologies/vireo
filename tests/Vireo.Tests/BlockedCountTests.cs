using Vireo.Models;
using Xunit;

namespace Vireo.Tests;

/// <summary>What the shield counts, and when a blocked address is answered quietly.</summary>
public class BlockedCountTests
{
    [Fact]
    public void A_script_retrying_a_blocked_address_counts_once()
    {
        var tab = new BrowserTab();
        var counted = 0;
        for (var i = 0; i < 1000; i++)
            if (tab.CountBlocked($"https://cdn.ads.example/config/v3/1018.json?cb={i}", "cdn.ads.example", 0)) counted++;
        Assert.Equal(1, counted);
        Assert.Equal(1, tab.Blocked);
    }

    [Fact]
    public void Different_addresses_each_count()
    {
        var tab = new BrowserTab();
        Assert.True(tab.CountBlocked("https://ads.example/a.js", "ads.example", 0));
        Assert.True(tab.CountBlocked("https://ads.example/b.js", "ads.example", 0));
        Assert.True(tab.CountBlocked("https://track.example/pixel", "track.example", 1));
        Assert.Equal(3, tab.Blocked);
        Assert.Equal(1, tab.BlockedTrackers);
    }

    [Fact]
    public void Repeats_are_counted_per_address_until_the_page_changes()
    {
        var tab = new BrowserTab();
        for (var i = 1; i <= 5; i++) Assert.Equal(i, tab.BlockedAgain($"https://ads.example/config.json?t={i}"));
        Assert.Equal(1, tab.BlockedAgain("https://ads.example/other.json"));
        tab.ResetBlocked();
        Assert.Equal(1, tab.BlockedAgain("https://ads.example/config.json"));
        Assert.True(tab.CountBlocked("https://ads.example/config.json", "ads.example", 0));
    }
}
