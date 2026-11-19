using Vireo.Models;
using Xunit;

namespace Vireo.Tests;

/// <summary>A tab that has just been playing is not put to sleep in the gap between two songs.</summary>
public class PlayOnTests
{
    [Fact]
    public void A_tab_that_went_quiet_moments_ago_still_counts_as_playing()
    {
        var tab = new BrowserTab { Url = "https://example.com/" };
        Assert.False(tab.Facts.IsPlayingAudio); // never played

        tab.IsPlayingAudio = true;
        Assert.True(tab.Facts.IsPlayingAudio);
        tab.IsPlayingAudio = false; // the gap before the next song
        Assert.True(tab.Facts.IsPlayingAudio);

        tab.AudioStoppedAt = DateTime.UtcNow - BrowserTab.QuietGrace - TimeSpan.FromSeconds(1);
        Assert.False(tab.Facts.IsPlayingAudio); // quiet for a while: it may sleep
    }
}
