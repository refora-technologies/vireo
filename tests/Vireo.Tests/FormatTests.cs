using Vireo.Services;
using Xunit;

namespace Vireo.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    [InlineData(150L * 1024 * 1024, "150 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.0 GB")]
    [InlineData(-1, "")]
    public void Sizes(long bytes, string expected) => Assert.Equal(expected, Format.Bytes(bytes));

    [Theory]
    [InlineData(8, "0:08")]
    [InlineData(252, "4:12")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-5, "0:00")]
    public void Durations(double seconds, string expected) => Assert.Equal(expected, Format.Duration(seconds));

    [Fact]
    public void Speeds() => Assert.Equal("2.0 MB/s", Format.Speed(2 * 1024 * 1024));

    [Theory]
    [InlineData(4, "4 s left")]
    [InlineData(90, "2 min left")]
    [InlineData(7200, "2.0 h left")]
    public void Time_left(double seconds, string expected) => Assert.Equal(expected, Format.Eta(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Dates_are_friendly()
    {
        var now = new DateTime(2026, 10, 31, 22, 0, 0);
        Assert.Equal("Today 09:05", Format.When(new DateTime(2026, 10, 31, 9, 5, 0), now));
        Assert.Equal("Yesterday 23:59", Format.When(new DateTime(2026, 10, 30, 23, 59, 0), now));
        Assert.Equal("3 Oct", Format.When(new DateTime(2026, 10, 3, 1, 0, 0), now));
        Assert.Equal("3 Oct 2025", Format.When(new DateTime(2025, 10, 3, 1, 0, 0), now));
    }
}

public class UrlCleanerTests
{
    [Theory]
    [InlineData("https://shop.test/p?id=7&utm_source=mail&utm_medium=x", "https://shop.test/p?id=7")]
    [InlineData("https://news.test/a?fbclid=abc123", "https://news.test/a")]
    [InlineData("https://news.test/a?fbclid=abc#section", "https://news.test/a#section")]
    [InlineData("https://x.test/?q=cats&gclid=1&page=2", "https://x.test/?q=cats&page=2")]
    [InlineData("https://x.test/?UTM_Source=A", "https://x.test/")]
    public void Tracking_parameters_are_removed(string dirty, string clean) => Assert.Equal(clean, UrlCleaner.Clean(dirty));

    [Theory]
    [InlineData("https://x.test/")]
    [InlineData("https://x.test/?q=utm_source")]
    [InlineData("https://x.test/p?id=7&sort=asc#top")]
    [InlineData("https://x.test/?a=1&a=2")]
    public void Everything_else_is_left_exactly_alone(string url) => Assert.Equal(url, UrlCleaner.Clean(url));
}

public class NiceTitleTests
{
    [Theory]
    [InlineData("Episode 1", "https://cdn.test/v/index.m3u8", "Episode 1")]
    [InlineData("127.0.0.1", "https://cdn.test/shows/the-show/index.m3u8", "the-show")]
    [InlineData("http://127.0.0.1:4173/pages/file.html", "http://127.0.0.1:4173/clip.mp4", "clip")]
    [InlineData("", "https://cdn.test/media/Holiday%20Trip.mp4", "Holiday Trip")]
    [InlineData("cdn.test", "https://cdn.test/talks/manifest.mpd#vireo-rep=1", "talks")]
    public void A_title_that_is_really_an_address_gives_way_to_the_files_name(string title, string url, string expected) =>
        Xunit.Assert.Equal(expected, Vireo.MainWindow.NiceTitle(title, url));
}
