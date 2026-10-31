using Vireo.Media;
using Xunit;

namespace Vireo.Tests;

public class MediaClassifierTests
{
    [Theory]
    [InlineData("https://x.test/live/master.m3u8", 200, "application/vnd.apple.mpegurl", MediaKind.Hls)]
    [InlineData("https://x.test/v/index.m3u8", 200, "application/octet-stream", MediaKind.Hls)]
    [InlineData("https://x.test/v/manifest.mpd", 200, "application/dash+xml", MediaKind.Dash)]
    [InlineData("https://x.test/clip.mp4", 200, "video/mp4", MediaKind.File)]
    [InlineData("https://x.test/song.mp3", 200, "audio/mpeg", MediaKind.File)]
    [InlineData("https://x.test/video", 200, "video/webm", MediaKind.File)]
    public void Recognises_media(string url, int status, string type, MediaKind kind) =>
        Assert.Equal(kind, MediaClassifier.Classify(url, status, type, 5_000_000)?.Kind);

    [Theory]
    [InlineData("https://x.test/seg0.ts", "video/mp2t")]
    [InlineData("https://x.test/chunk.m4s", "video/iso.segment")]
    [InlineData("https://x.test/a.js", "application/javascript")]
    [InlineData("https://x.test/p.png", "image/png")]
    [InlineData("https://x.test/v?range=0-999", "video/mp4")]
    [InlineData("https://rr1.googlevideo.com/videoplayback?itag=18", "video/mp4")]
    public void Ignores_pieces_pages_and_other_files(string url, string type) =>
        Assert.Null(MediaClassifier.Classify(url, 200, type, 5_000_000));

    [Theory]
    [InlineData("https://x.test/chunk-list/index.m3u8", "application/x-mpegurl", MediaKind.Hls)]   // a folder named like a piece
    [InlineData("https://x.test/seg-hd/master.m3u8", "", MediaKind.Hls)]
    [InlineData("https://x.test/p/stream.m3u8?token=1", "image/jpeg", MediaKind.Hls)]            // served as a picture to put off downloaders
    [InlineData("https://x.test/p/playlist.php?f=a.m3u8", "text/html", MediaKind.Hls)]
    [InlineData("https://x.test/p/manifest.mpd", "image/png", MediaKind.Dash)]
    [InlineData("https://x.test/p/video.mpd", "video/vnd.mpeg.dash.mpd", MediaKind.Dash)]
    [InlineData("https://x.test/p/index.m3u8", "application/m3u8", MediaKind.Hls)]
    public void Playlists_are_found_even_when_the_site_disguises_them(string url, string type, MediaKind kind) =>
        Assert.Equal(kind, MediaClassifier.Classify(url, 200, type, 3000)?.Kind);

    [Theory]
    [InlineData("https://x.test/movie.mp4", "text/html")]          // an error page answering for a video address
    [InlineData("https://x.test/movie.mp4", "image/jpeg")]
    [InlineData("https://stats.jwpltx.com/ping.gif", "video/mp4")]
    [InlineData("https://www.google-analytics.com/j/collect", "video/mp4")]
    [InlineData("https://x.test/video/frag-12.mp4", "video/mp4")]  // a numbered piece
    [InlineData("https://x.test/a/seg-3-v1-a1.ts", "")]
    public void Things_that_only_look_like_videos_are_not_offered(string url, string type) =>
        Assert.Null(MediaClassifier.Classify(url, 200, type, 5_000_000));

    [Fact]
    public void Short_video_clips_count_but_short_sounds_do_not()
    {
        Assert.NotNull(MediaClassifier.Classify("https://x.test/clip.mp4", 200, "video/mp4", 80_000));
        Assert.Null(MediaClassifier.Classify("https://x.test/click.mp3", 200, "audio/mpeg", 80_000));
        Assert.NotNull(MediaClassifier.Classify("https://x.test/song.mp3", 200, "audio/mpeg", 2_000_000));
    }

    [Fact]
    public void Addresses_that_look_like_media_are_told_apart_cheaply()
    {
        Assert.True(MediaClassifier.LooksLikeMediaUrl("https://x.test/a/b/master.m3u8?x=1"));
        Assert.True(MediaClassifier.LooksLikeMediaUrl("https://x.test/a.mp4"));
        Assert.False(MediaClassifier.LooksLikeMediaUrl("https://x.test/app.js"));
        Assert.False(MediaClassifier.LooksLikeMediaUrl("https://x.test/seg1.ts")); // pieces by the thousand are not remembered
    }

    [Fact]
    public void Small_files_are_beacons_and_failed_requests_are_ignored()
    {
        Assert.Null(MediaClassifier.Classify("https://x.test/tiny.mp4", 200, "video/mp4", 4000));
        Assert.Null(MediaClassifier.Classify("https://x.test/a.mp4", 404, "video/mp4", 5_000_000));
        Assert.Null(MediaClassifier.Classify("ftp://x.test/a.mp4", 200, "video/mp4", 5_000_000));
    }

    [Fact]
    public void A_ranged_answer_reports_the_whole_files_size()
    {
        var c = MediaClassifier.Classify("https://x.test/big.mp4", 206, "video/mp4", 1_000_000, "bytes 0-999999/52428800");
        Assert.Equal(52428800, c!.Size);
        Assert.Null(MediaClassifier.Classify("https://x.test/big.mp4", 206, "video/mp4", 1_000_000)!.Size); // a partial answer says nothing about the full size
    }

    [Fact]
    public void The_same_file_asked_for_with_different_tags_has_one_key() =>
        Assert.Equal(MediaClassifier.Key("https://x.test/a.mp4?id=1&cb=123"), MediaClassifier.Key("https://x.test/a.mp4?id=1&cb=999&range=0-5"));

    [Theory]
    [InlineData("audio/mpeg", "mp3", true)]
    [InlineData("video/mp4", "mp4", false)]
    [InlineData(null, "m4a", true)]
    public void Audio_is_told_from_video(string? mime, string container, bool audio) => Assert.Equal(audio, MediaClassifier.IsAudio(mime, container));
}

public class HlsParserTests
{
    private const string Master = """
        #EXTM3U
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="English",LANGUAGE="en",DEFAULT=YES,URI="audio/en.m3u8"
        #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="English",LANGUAGE="en",URI="subs/en.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2",AUDIO="aud"
        360/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=5000000,AVERAGE-BANDWIDTH=4500000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",FRAME-RATE=60.000,AUDIO="aud"
        1080/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS="mp4a.40.2"
        https://other.test/audio-only.m3u8
        """;

    [Fact]
    public void A_master_lists_qualities_best_first_with_whole_addresses()
    {
        var m = (HlsMaster)HlsParser.Parse(Master, "https://s.test/v/master.m3u8");
        Assert.Equal(["1080p", "360p", "Audio 64 kbps"], m.Variants.Select(v => v.Label));
        Assert.Equal("https://s.test/v/1080/index.m3u8", m.Variants[0].Url);
        Assert.Equal(60, m.Variants[0].FrameRate);
        Assert.Equal(4_500_000, m.Variants[0].Bandwidth);
        Assert.True(m.Variants[2].AudioOnly);
        Assert.Equal("https://other.test/audio-only.m3u8", m.Variants[2].Url);
        Assert.Equal("https://s.test/v/audio/en.m3u8", Assert.Single(m.Audio).Url);
        Assert.Equal(1, m.Subtitles);
        Assert.False(m.Drm);
    }

    private const string Vod = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:6
        #EXT-X-MEDIA-SEQUENCE:10
        #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x1234
        #EXTINF:6.0,
        s0.ts
        #EXTINF:4.5,
        s1.ts
        #EXT-X-ENDLIST
        """;

    [Fact]
    public void A_media_playlist_gives_the_pieces_in_order_with_their_keys()
    {
        var m = (HlsMedia)HlsParser.Parse(Vod, "https://s.test/v/index.m3u8");
        Assert.False(m.Live);
        Assert.True(m.Aes128);
        Assert.Equal(10.5, m.Duration);
        Assert.Equal([10L, 11L], m.Segments.Select(s => s.Sequence));
        Assert.Equal("https://s.test/v/s0.ts", m.Segments[0].Url);
        Assert.Equal("https://s.test/v/key.bin", m.Segments[0].Key!.Uri);
        Assert.Equal(16, m.Segments[0].Key!.Iv!.Length);
        Assert.Equal(0x34, m.Segments[0].Key!.Iv![15]);
    }

    [Fact]
    public void A_playlist_without_an_end_marker_is_live() =>
        Assert.True(((HlsMedia)HlsParser.Parse("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\na.ts\n", "https://s.test/l.m3u8")).Live);

    [Fact]
    public void Byte_ranges_and_init_sections_are_read()
    {
        var m = (HlsMedia)HlsParser.Parse("""
            #EXTM3U
            #EXT-X-MAP:URI="init.mp4",BYTERANGE="720@0"
            #EXTINF:2,
            #EXT-X-BYTERANGE:1000@720
            f.m4s
            #EXTINF:2,
            #EXT-X-BYTERANGE:900
            f.m4s
            #EXT-X-ENDLIST
            """, "https://s.test/v/i.m3u8");
        Assert.True(m.IsFragmentedMp4);
        Assert.Equal(720, m.Segments[0].Map!.Length);
        Assert.Equal((720L, 1000L), (m.Segments[0].Offset!.Value, m.Segments[0].Length!.Value));
        Assert.Equal(1720, m.Segments[1].Offset); // continues where the last range ended
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\",KEYFORMAT=\"com.apple.streamingkeydelivery\"")]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES-CTR,URI=\"data:text/plain;base64,AAAA\",KEYFORMAT=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"")]
    public void Protected_streams_are_flagged_so_they_are_never_downloaded(string keyLine)
    {
        var m = (HlsMedia)HlsParser.Parse($"#EXTM3U\n{keyLine}\n#EXTINF:2,\na.ts\n#EXT-X-ENDLIST\n", "https://s.test/p.m3u8");
        Assert.True(m.Drm);
        Assert.Equal("OTHER", m.Segments[0].Key!.Method);
    }

    [Fact]
    public void Without_an_iv_the_pieces_number_is_used()
    {
        var iv = HlsParser.SequenceIv(0x0102);
        Assert.Equal(16, iv.Length);
        Assert.Equal([1, 2], iv[14..]);
        Assert.All(iv[..14], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Attributes_keep_quoted_commas() =>
        Assert.Equal("avc1.4d,mp4a.40.2", HlsParser.Attributes("BANDWIDTH=1,CODECS=\"avc1.4d,mp4a.40.2\",RESOLUTION=2x2")["CODECS"]);

    [Fact]
    public void Playlists_are_recognised_and_other_text_is_not()
    {
        Assert.True(HlsParser.IsPlaylist("\n#EXTM3U\n"));
        Assert.False(HlsParser.IsPlaylist("<html>"));
    }
}

public class QualityListTests
{
    private const string AppleLike = """
        #EXTM3U
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud1",NAME="English",DEFAULT=YES,URI="a1/p.m3u8"
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud2",NAME="English",DEFAULT=YES,CHANNELS="6",URI="a2/p.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=2500000,CODECS="avc1.640020,ac-3",RESOLUTION=960x540,FRAME-RATE=30.000,AUDIO="aud2"
        v5/p.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2400000,CODECS="avc1.640020,mp4a.40.2",RESOLUTION=960x540,FRAME-RATE=30.000,AUDIO="aud1"
        v5/p.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=900000,CODECS="avc1.640015,mp4a.40.2",RESOLUTION=480x270,FRAME-RATE=30.000,AUDIO="aud1"
        v2/p.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=900000,CODECS="avc1.640015,ac-3",RESOLUTION=480x270,FRAME-RATE=30.000,AUDIO="aud2"
        v2/p.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=4000000,CODECS="avc1.640020,mp4a.40.2",RESOLUTION=960x540,FRAME-RATE=60.000,AUDIO="aud1"
        v6/p.m3u8
        """;

    [Fact]
    public void The_same_picture_listed_for_each_kind_of_sound_is_offered_once_with_the_stereo_one()
    {
        var master = (HlsMaster)HlsParser.Parse(AppleLike, "https://x.test/m.m3u8");
        var list = MediaProbe.OneOfEach(master.Variants.Where(v => !v.AudioOnly));
        Assert.Equal(["540p 60", "540p 30", "270p 30"], list.Select(v => $"{v.Label} {Math.Round(v.FrameRate ?? 0)}"));
        Assert.All(list, v => Assert.Contains("mp4a", v.Codecs));
    }
}
