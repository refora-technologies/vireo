using System.Net.Http;
using System.IO;
using System.Net;
using Vireo.Downloads;
using Vireo.Media;
using Xunit;

namespace Vireo.Tests;

/// <summary>Serves a folder of files over HTTP, the way a streaming site serves a DASH stream.</summary>
internal sealed class FolderServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _dir;
    public string BaseUrl { get; }
    public Func<string, string?>? Override { get; set; }
    public int Hits;

    public FolderServer(string dir)
    {
        _dir = dir;
        var port = Random.Shared.Next(20000, 40000);
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await _listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() =>
                {
                    try
                    {
                        Interlocked.Increment(ref Hits);
                        var name = c.Request.Url!.AbsolutePath.TrimStart('/');
                        var text = Override?.Invoke(name);
                        byte[] data;
                        if (text is not null) data = System.Text.Encoding.UTF8.GetBytes(text);
                        else
                        {
                            var path = Path.Combine(_dir, name);
                            if (!File.Exists(path)) { c.Response.StatusCode = 404; c.Response.Close(); return; }
                            data = File.ReadAllBytes(path);
                        }
                        c.Response.ContentType = name.EndsWith(".mpd") ? "application/dash+xml" : "video/mp4";
                        c.Response.ContentLength64 = data.Length;
                        c.Response.OutputStream.Write(data);
                        c.Response.Close();
                    }
                    catch { /* client went away */ }
                });
            }
        });
    }

    public void Dispose() => _listener.Close();
}

public class DashParserTests
{
    private const string Base = "https://cdn.test/show/manifest.mpd";

    private static string Mpd(string body, string attrs = "type=\"static\" mediaPresentationDuration=\"PT20S\"") =>
        $"<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" {attrs}>{body}</MPD>";

    [Theory]
    [InlineData("PT8.0S", 8.0)]
    [InlineData("PT1H2M3.5S", 3723.5)]
    [InlineData("PT2M", 120.0)]
    [InlineData("P1DT1H", 90000.0)]
    public void Iso_durations_are_read(string text, double seconds) => Assert.Equal(seconds, DashParser.Seconds(text));

    [Fact]
    public void Garbage_is_not_a_duration() => Assert.Null(DashParser.Seconds("soon"));

    [Fact]
    public void A_timeline_template_becomes_a_list_of_numbered_pieces_with_an_init_section()
    {
        var m = DashParser.Parse(Mpd("""
            <Period><AdaptationSet contentType="video" mimeType="video/mp4">
              <Representation id="v1" codecs="avc1.64001f" bandwidth="1800000" width="1280" height="720">
                <SegmentTemplate timescale="1000" initialization="i-$RepresentationID$.mp4" media="s-$RepresentationID$-$Number%05d$.m4s" startNumber="1">
                  <SegmentTimeline><S t="0" d="4000" r="2"/><S d="2000"/></SegmentTimeline>
                </SegmentTemplate>
              </Representation></AdaptationSet></Period>
            """), Base);
        var v = Assert.Single(m.Representations);
        Assert.Equal("720p", v.Label);
        Assert.Equal("https://cdn.test/show/i-v1.mp4", v.Init!.Url);
        Assert.Equal(4, v.Segments.Count);
        Assert.Equal("https://cdn.test/show/s-v1-00001.m4s", v.Segments[0].Url);
        Assert.Equal("https://cdn.test/show/s-v1-00004.m4s", v.Segments[3].Url);
        Assert.Equal([4.0, 4.0, 4.0, 2.0], v.Segments.Select(s => s.Duration));
        Assert.False(m.Live);
    }

    [Fact]
    public void A_time_template_names_pieces_by_their_start_time()
    {
        var m = DashParser.Parse(Mpd("""
            <Period><AdaptationSet mimeType="video/mp4"><Representation id="a" bandwidth="1" height="360">
              <SegmentTemplate timescale="90000" media="$Time$.m4s" initialization="init.mp4"><SegmentTimeline><S t="1000" d="180000" r="1"/></SegmentTimeline></SegmentTemplate>
            </Representation></AdaptationSet></Period>
            """), Base);
        Assert.Equal(["https://cdn.test/show/1000.m4s", "https://cdn.test/show/181000.m4s"], m.Representations[0].Segments.Select(s => s.Url));
    }

    [Fact]
    public void A_template_with_only_a_duration_counts_the_pieces_from_the_length()
    {
        var m = DashParser.Parse(Mpd("""
            <Period><AdaptationSet mimeType="audio/mp4"><Representation id="en" bandwidth="128000">
              <SegmentTemplate timescale="1" duration="6" media="a_$Bandwidth$_$Number$.m4s" initialization="a_init.mp4" startNumber="0"/>
            </Representation></AdaptationSet></Period>
            """, "type=\"static\" mediaPresentationDuration=\"PT20S\""), Base);
        var r = m.Representations[0];
        Assert.True(r.IsAudio);
        Assert.Equal(4, r.Segments.Count);
        Assert.Equal("https://cdn.test/show/a_128000_0.m4s", r.Segments[0].Url);
        Assert.Equal("https://cdn.test/show/a_128000_3.m4s", r.Segments[3].Url);
    }

    [Fact]
    public void Base_addresses_nest_and_a_repeat_of_minus_one_runs_to_the_end_of_the_period()
    {
        var m = DashParser.Parse(Mpd("""
            <BaseURL>https://media.test/root/</BaseURL>
            <Period><BaseURL>p1/</BaseURL><AdaptationSet mimeType="video/mp4"><BaseURL>hd/</BaseURL>
              <Representation id="x" bandwidth="1" height="1080"><BaseURL>r/</BaseURL>
                <SegmentTemplate timescale="1" media="$Number$.m4s" initialization="init.mp4"><SegmentTimeline><S t="0" d="5" r="-1"/></SegmentTimeline></SegmentTemplate>
              </Representation></AdaptationSet></Period>
            """), Base);
        var r = m.Representations[0];
        Assert.Equal("https://media.test/root/p1/hd/r/1.m4s", r.Segments[0].Url);
        Assert.Equal(4, r.Segments.Count); // 20 s in 5 s pieces
    }

    [Fact]
    public void A_segment_list_with_byte_ranges_keeps_the_ranges()
    {
        var m = DashParser.Parse(Mpd("""
            <Period><AdaptationSet mimeType="video/mp4"><Representation id="l" bandwidth="1" height="480"><BaseURL>video.mp4</BaseURL>
              <SegmentList timescale="1" duration="10"><Initialization range="0-599"/>
                <SegmentURL mediaRange="600-1599"/><SegmentURL mediaRange="1600-2599"/></SegmentList>
            </Representation></AdaptationSet></Period>
            """), Base);
        var r = m.Representations[0];
        Assert.Equal((0L, 600L), (r.Init!.Offset, r.Init.Length));
        Assert.Equal("https://cdn.test/show/video.mp4", r.Segments[1].Url);
        Assert.Equal((1600L, 1000L), (r.Segments[1].Offset, r.Segments[1].Length));
    }

    [Fact]
    public void A_quality_that_is_just_a_file_is_offered_as_a_file()
    {
        var m = DashParser.Parse(Mpd("""
            <Period><AdaptationSet mimeType="video/mp4"><Representation id="f" bandwidth="900000" height="720"><BaseURL>clip-720.mp4</BaseURL>
              <SegmentBase indexRange="800-1200"><Initialization range="0-799"/></SegmentBase></Representation></AdaptationSet></Period>
            """), Base);
        var r = m.Representations[0];
        Assert.Equal("https://cdn.test/show/clip-720.mp4", r.FileUrl);
        Assert.Empty(r.Segments);
    }

    [Fact]
    public void Protected_streams_are_recognised()
    {
        var m = DashParser.Parse(Mpd("""
            <Period><AdaptationSet mimeType="video/mp4"><ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>
              <Representation id="d" bandwidth="1" height="720"><SegmentTemplate timescale="1" duration="4" media="$Number$.m4s" initialization="i.mp4"/></Representation></AdaptationSet></Period>
            """), Base);
        Assert.True(m.Protected);
    }

    [Fact]
    public void Subtitles_are_left_out_and_the_longest_period_wins()
    {
        var m = DashParser.Parse(Mpd("""
            <Period id="ad" duration="PT5S"><AdaptationSet mimeType="video/mp4"><Representation id="ad" bandwidth="1" height="240"><BaseURL>ad.mp4</BaseURL><SegmentBase/></Representation></AdaptationSet></Period>
            <Period id="main" duration="PT15S"><AdaptationSet mimeType="video/mp4"><Representation id="main" bandwidth="1" height="720"><BaseURL>main.mp4</BaseURL><SegmentBase/></Representation></AdaptationSet>
              <AdaptationSet mimeType="text/vtt" contentType="text"><Representation id="t" bandwidth="1"><BaseURL>subs.vtt</BaseURL></Representation></AdaptationSet></Period>
            """), Base);
        var r = Assert.Single(m.Representations);
        Assert.Equal("main", r.Id);
    }

    [Fact]
    public void Qualities_are_listed_best_first_and_mp4_is_preferred_over_webm()
    {
        var m = DashParser.Parse(Mpd("""
            <Period><AdaptationSet mimeType="video/mp4">
              <Representation id="s" bandwidth="1" height="360"><BaseURL>a.mp4</BaseURL><SegmentBase/></Representation>
              <Representation id="b" bandwidth="1" height="1080"><BaseURL>b.mp4</BaseURL><SegmentBase/></Representation></AdaptationSet>
              <AdaptationSet mimeType="video/webm"><Representation id="w" bandwidth="1" height="2160"><BaseURL>c.webm</BaseURL><SegmentBase/></Representation></AdaptationSet>
              <AdaptationSet mimeType="audio/mp4" lang="fr"><Role value="alternate"/><Representation id="fr" bandwidth="64000"><BaseURL>fr.m4a</BaseURL><SegmentBase/></Representation></AdaptationSet>
              <AdaptationSet mimeType="audio/mp4" lang="en"><Role value="main"/><Representation id="en-lo" bandwidth="64000"><BaseURL>lo.m4a</BaseURL><SegmentBase/></Representation>
                <Representation id="en-hi" bandwidth="192000"><BaseURL>hi.m4a</BaseURL><SegmentBase/></Representation></AdaptationSet></Period>
            """), Base);
        Assert.Equal(["b", "s"], m.Videos.Select(v => v.Id));
        Assert.Equal("en-hi", m.BestAudio(false)!.Id);
    }

    [Fact]
    public void A_live_stream_with_numbered_pieces_offers_the_most_recent_ones()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var m = DashParser.Parse(Mpd("""
            <Period start="PT0S"><AdaptationSet mimeType="video/mp4"><Representation id="v" bandwidth="1" height="720">
              <SegmentTemplate timescale="1" duration="4" media="seg-$Number$.m4s" initialization="init.mp4" startNumber="1"/></Representation></AdaptationSet></Period>
            """, "type=\"dynamic\" availabilityStartTime=\"2026-01-01T00:00:00Z\""), Base, start.AddSeconds(1000));
        Assert.True(m.Live);
        var s = m.Representations[0].Segments;
        Assert.Equal(23, s.Count); // the last 90 s in 4 s pieces
        Assert.EndsWith("seg-250.m4s", s[^1].Url); // pieces are numbered from 1; the one being made at 1000 s is not offered yet
    }

    [Fact]
    public void A_representation_is_chosen_by_the_tag_on_the_address()
    {
        var url = DashManifest.WithRepresentation("https://x.test/a.mpd?t=1#old", "720 p/1");
        Assert.Equal("720 p/1", DashManifest.RepresentationOf(url));
        Assert.Null(DashManifest.RepresentationOf("https://x.test/a.mpd"));
    }

    [Fact]
    public void The_real_test_manifest_is_understood()
    {
        var dir = DashDir();
        if (dir is null) return;
        var m = DashParser.Parse(File.ReadAllText(Path.Combine(dir, "manifest.mpd")), "http://t.test/manifest.mpd");
        Assert.Equal(["720p", "360p"], m.Videos.Select(v => v.Label));
        Assert.Equal(4, m.Videos[0].Segments.Count);
        Assert.Equal("http://t.test/chunk-stream0-00001.m4s", m.Videos[0].Segments[0].Url);
        Assert.Equal("http://t.test/init-stream2.m4s", m.BestAudio(false)!.Init!.Url);
        Assert.Equal(5, m.BestAudio(false)!.Segments.Count);
        Assert.Equal(8.0, m.Duration);
    }

    internal static string? DashDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests", "server", "media", "dash-real"))) d = d.Parent;
        return d is null ? null : Path.Combine(d.FullName, "tests", "server", "media", "dash-real");
    }
}

public class DashDownloadTests : IDisposable
{
    public DashDownloadTests() => Ffmpeg.Disabled = true;
    public void Dispose() => Ffmpeg.Disabled = false;

    private static async Task<DetectedMedia> Probe(string url)
    {
        var m = new DetectedMedia { Url = url, Kind = MediaKind.Dash };
        using var http = new System.Net.Http.HttpClient();
        await MediaProbe.ProbeAsync(m, http, u => new HttpRequestMessage(HttpMethod.Get, u), CancellationToken.None);
        return m;
    }

    [Fact]
    public async Task A_stream_is_probed_into_qualities_and_audio_only()
    {
        var dir = DashParserTests.DashDir();
        if (dir is null) return;
        using var s = new FolderServer(dir);
        var m = await Probe(s.BaseUrl + "/manifest.mpd");
        Assert.Equal(MediaStatus.Ready, m.Status);
        Assert.Equal(["720p", "360p", "Audio only"], m.Choices.Select(c => c.Label));
        Assert.Equal(8.0, m.DurationSec);
        Assert.All(m.Choices.Take(2), c => Assert.NotNull(c.AudioUrl));
        Assert.True(m.Choices[2].AudioOnly);
    }

    [Fact]
    public async Task A_protected_stream_is_reported_and_never_downloaded()
    {
        var dir = DashParserTests.DashDir();
        if (dir is null) return;
        using var s = new FolderServer(dir) { Override = n => n == "drm.mpd" ? File.ReadAllText(Path.Combine(dir, "manifest.mpd")).Replace("<Representation id=\"0\"", "<ContentProtection schemeIdUri=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"/><Representation id=\"0\"") : null };
        var m = await Probe(s.BaseUrl + "/drm.mpd");
        Assert.Equal(MediaStatus.Protected, m.Status);
        using var box = new Sandbox();
        using var engine = box.Engine(connections: 4);
        var item = engine.Add(new DownloadRequest(s.BaseUrl + "/drm.mpd#" + DashManifest.Tag + "0", "x.mp4", Stream: new StreamSpec(s.BaseUrl + "/drm.mpd#" + DashManifest.Tag + "0")));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(DownloadState.Failed, item.State);
        Assert.Contains("DRM", item.Error);
    }

    [Fact]
    public async Task The_chosen_quality_and_sound_come_out_as_one_playable_mp4()
    {
        var dir = DashParserTests.DashDir();
        if (dir is null || !FfmpegTools.Available) return;
        using var s = new FolderServer(dir);
        var m = await Probe(s.BaseUrl + "/manifest.mpd");
        var choice = m.Choices[1]; // 360p
        using var box = new Sandbox();
        using var engine = box.Engine(connections: 4);
        var item = engine.Add(new DownloadRequest(choice.Url, "show (360p).mp4", Stream: new StreamSpec(choice.Url, choice.AudioUrl, false, 100_000)));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(40));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Null(item.Note);
        Assert.Equal(["video", "audio"], FfmpegTools.StreamKinds(item.FinalPath));
        Assert.Equal("", FfmpegTools.DecodeErrors(item.FinalPath));
        Assert.Empty(Directory.GetFiles(box.Files, "*.vireopart"));
        var video = FfmpegTools.Size(item.FinalPath);
        Assert.Equal(360, video.Height);
    }

    [Fact]
    public async Task Audio_only_is_saved_as_a_playable_audio_file()
    {
        var dir = DashParserTests.DashDir();
        if (dir is null || !FfmpegTools.Available) return;
        using var s = new FolderServer(dir);
        var m = await Probe(s.BaseUrl + "/manifest.mpd");
        var choice = m.Choices[^1];
        using var box = new Sandbox();
        using var engine = box.Engine(connections: 4);
        var item = engine.Add(new DownloadRequest(choice.Url, "show.m4a", Stream: new StreamSpec(choice.Url, null, false, 50_000)));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(40));
        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(["audio"], FfmpegTools.StreamKinds(item.FinalPath));
    }
}
