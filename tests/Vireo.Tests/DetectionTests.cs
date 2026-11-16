using System.Text;
using Vireo.Media;
using Xunit;

namespace Vireo.Tests;

/// <summary>Streams that hide: behind a picture's or a text file's name, behind no type at all, or only named in a site's JSON.</summary>
public class DetectionTests
{
    [Theory]
    [InlineData("https://cdn.x/poster.png", "application/vnd.apple.mpegurl", MediaKind.Hls)]
    [InlineData("https://cdn.x/list.txt", "application/x-mpegURL", MediaKind.Hls)]
    [InlineData("https://cdn.x/api/stream.json", "application/vnd.apple.mpegurl", MediaKind.Hls)]
    [InlineData("https://cdn.x/manifest.xml", "application/dash+xml", MediaKind.Dash)]
    public void A_playlist_behind_a_misleading_name_is_still_classified_by_its_type(string url, string type, MediaKind kind)
    {
        Assert.False(MediaClassifier.ClearlyNotMedia(url));
        Assert.Equal(kind, MediaClassifier.Classify(url, 200, type)?.Kind);
    }

    [Theory]
    [InlineData("https://x/app.js")]
    [InlineData("https://x/site.css")]
    [InlineData("https://x/font.woff2")]
    [InlineData("https://x/logo.svg")]
    public void Scripts_styles_and_fonts_are_still_skipped_without_reading_headers(string url) => Assert.True(MediaClassifier.ClearlyNotMedia(url));

    [Fact]
    public void A_playlist_with_no_telling_address_or_type_is_found_by_its_first_bytes()
    {
        Assert.Equal(MediaKind.Hls, MediaClassifier.SniffPlaylist(Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nhd.m3u8\n")));
        Assert.Equal(MediaKind.Hls, MediaClassifier.SniffPlaylist([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("\r\n#EXTM3U\n")]));
        Assert.Equal(MediaKind.Dash, MediaClassifier.SniffPlaylist(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\">")));
        Assert.Null(MediaClassifier.SniffPlaylist(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><rss></rss>")));
        Assert.Null(MediaClassifier.SniffPlaylist(Encoding.UTF8.GetBytes("{\"ok\":true}")));
        Assert.Null(MediaClassifier.SniffPlaylist([]));
    }

    [Theory]
    [InlineData("https://x/api/source?id=7", "text/plain", null, true)]
    [InlineData("https://x/api/source?id=7", "", 900L, true)]
    [InlineData("https://x/get?id=7", "application/octet-stream", 1200L, true)]
    [InlineData("https://x/get?id=7", "application/octet-stream", null, false)]          // could be a whole film: never read
    [InlineData("https://x/get?id=7", "application/octet-stream", 50_000_000L, false)]
    [InlineData("https://x/v/seg-12.ts", "application/octet-stream", 900L, false)]      // a piece of a stream
    [InlineData("https://x/movie.mp4", "application/octet-stream", 900L, false)]        // a file, classified by name
    [InlineData("https://x/page", "text/html", 900L, false)]
    [InlineData("https://x/app.js", "text/plain", 900L, false)]
    public void Only_small_plain_answers_are_looked_inside(string url, string type, long? length, bool expected) =>
        Assert.Equal(expected, MediaClassifier.ShouldSniff(url, 200, type, length));

    [Fact]
    public void Failed_answers_are_never_looked_inside() => Assert.False(MediaClassifier.ShouldSniff("https://x/api/source", 404, "text/plain", 100));

    [Fact]
    public void Stream_addresses_written_in_json_are_found_with_their_escapes_undone()
    {
        var json = """{"sources":[{"file":"https:\/\/cdn.example.com\/hls\/ep1\/master.m3u8?token=a&exp=9","type":"hls"}],"tracks":[{"file":"https://cdn.example.com/subs/en.vtt"}],"dash":"https://cdn.example.com/d/manifest.mpd"}""";
        var links = MediaClassifier.FindStreamLinks(json);
        Assert.Equal(["https://cdn.example.com/hls/ep1/master.m3u8?token=a&exp=9", "https://cdn.example.com/d/manifest.mpd"], links);
    }

    [Fact]
    public void Quality_set_addresses_keep_the_commas_in_their_path()
    {
        // nginx-vod / Akamai "urlset" playlists list their qualities with commas; a comma after the address still ends it.
        var json = """{"hls":{"src":"https://dash.cdn.example/hls/v1/1892-,240p-av1,1080p-av1,.mp4.urlset/master.m3u8?hash=Ab-c&expires=9&ip=2402:d000::1","type":"application/x-mpegURL"}, "list":[https://a.example/x.m3u8,https://b.example/y.mpd]}""";
        Assert.Equal(
            ["https://dash.cdn.example/hls/v1/1892-,240p-av1,1080p-av1,.mp4.urlset/master.m3u8?hash=Ab-c&expires=9&ip=2402:d000::1", "https://a.example/x.m3u8", "https://b.example/y.mpd"],
            MediaClassifier.FindStreamLinks(json));
    }

    [Fact]
    public void One_video_in_two_codec_playlists_becomes_one_entry_preferring_h264()
    {
        static MediaChoice C(string label, int h, string codec) => new("v", label, $"{h * 16 / 9}×{h} · {codec}", h * 1000L, $"https://x/{codec}/{h}.m3u8", null);
        var av1 = new DetectedMedia { Url = "https://x/av1/master.m3u8", Kind = MediaKind.Hls, Status = MediaStatus.Ready, Title = "Clip", DurationSec = 1782, Height = 2160,
            Choices = [C("4K", 2160, "AV1"), C("1440p", 1440, "AV1"), C("1080p", 1080, "AV1"), C("720p", 720, "AV1"), new("audio", "Audio only", "Original sound", 1, "https://x/a.m3u8", null, true)] };
        var avc = new DetectedMedia { Url = "https://x/avc/master.m3u8", Kind = MediaKind.Hls, Status = MediaStatus.Ready, Title = "Clip", DurationSec = 1782.4, Height = 1440,
            Choices = [C("1440p", 1440, "H.264"), C("1080p", 1080, "H.264"), C("720p", 720, "H.264")] };
        Assert.True(MediaProbe.SameVideoOtherCodec(avc, av1));
        MediaProbe.MergeChoices(avc, av1);
        Assert.Equal(["4K · AV1", "1440p · H.264", "1080p · H.264", "720p · H.264", "Audio only · Original sound"], avc.Choices.Select(c => c.Label + " · " + c.Detail.Split(" · ")[^1]));
        Assert.Equal(2160, avc.Height);
        Assert.Equal(avc.Choices.Count, avc.Choices.Select(c => c.Id).Distinct().Count());

        var other = new DetectedMedia { Url = "https://ad/x.m3u8", Kind = MediaKind.Hls, Status = MediaStatus.Ready, Title = "Clip", DurationSec = 30, Choices = [C("720p", 720, "H.264"), C("1080p", 1080, "H.264")] };
        Assert.False(MediaProbe.SameVideoOtherCodec(av1, other));
        Assert.Equal("H.264", MediaProbe.VideoCodecName("avc1.640028,mp4a.40.2"));
        Assert.Equal("AV1", MediaProbe.VideoCodecName("mp4a.40.2,av01.0.08M.08"));
        Assert.Null(MediaProbe.VideoCodecName(null));
    }

    [Theory]
    [InlineData("https://static-cdn.example.com/thumbs/static4/1/12/124/12415030/12415030-preview.webm", true)]
    [InlineData("https://cdn.example.com/videos/123/preview.mp4", true)]
    [InlineData("https://cdn.example.com/v/123_teaser.mp4", true)]
    [InlineData("https://cdn.example.com/videopreview/abc.mp4", true)]
    [InlineData("https://vid-s1.example.com/v2/abc/18929565-720p.mp4", false)]
    [InlineData("https://previewer.example.com/videos/full.mp4", false)]   // the site's name does not count
    [InlineData("https://cdn.example.com/v/full.mp4?preview=1", false)]   // nor a query word
    public void Hover_preview_clips_are_told_apart_by_their_path(string url, bool preview) =>
        Assert.Equal(preview, MediaClassifier.LooksLikePreviewClip(url));

    [Fact]
    public void Json_without_streams_gives_nothing_and_only_json_is_read_for_links()
    {
        Assert.Empty(MediaClassifier.FindStreamLinks("""{"poster":"https://cdn.example.com/p.jpg","next":"/watch/2"}"""));
        Assert.True(MediaClassifier.ShouldScanForLinks("https://x/api/episode/1", 200, "application/json; charset=utf-8", 4000));
        Assert.False(MediaClassifier.ShouldScanForLinks("https://x/api/episode/1", 200, "text/html", 4000));
        Assert.False(MediaClassifier.ShouldScanForLinks("https://x/api/huge", 200, "application/json", 50_000_000));
    }

    [Fact]
    public void An_untyped_download_that_names_itself_a_video_is_one()
    {
        var c = MediaClassifier.Classify("https://x/download?id=9", 200, "application/octet-stream", 30_000_000, null, "attachment; filename=\"Episode 01.mkv\"");
        Assert.Equal(MediaKind.File, c?.Kind);
        Assert.Equal("mkv", c?.Container);
        Assert.Null(MediaClassifier.Classify("https://x/download?id=9", 200, "application/octet-stream", 30_000_000, null, "attachment; filename=report.pdf"));
        Assert.Equal("mp4", MediaClassifier.ContainerFromDisposition("attachment; filename*=UTF-8''Le%20film.mp4"));
    }
}
