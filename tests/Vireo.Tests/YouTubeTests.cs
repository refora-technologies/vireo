using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Vireo.Downloads;
using Vireo.Media;
using Xunit;

namespace Vireo.Tests;

public class YouTubeParseTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw", "jNQXAC9IVRw")]
    [InlineData("https://m.youtube.com/watch?v=jNQXAC9IVRw&t=10s", "jNQXAC9IVRw")]
    [InlineData("https://youtu.be/jNQXAC9IVRw?si=abc", "jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/shorts/jNQXAC9IVRw", "jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/embed/jNQXAC9IVRw", "jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/live/jNQXAC9IVRw", "jNQXAC9IVRw")]
    [InlineData("https://example.com/watch?v=jNQXAC9IVRw", null)]
    [InlineData("https://www.youtube.com/watch?v=short", null)]
    [InlineData("https://www.youtube.com/feed/subscriptions", null)]
    [InlineData("", null)]
    public void Finds_the_video_in_an_address(string url, string? id) => Assert.Equal(id, YouTube.VideoId(url));

    private static string Format(int itag, string mime, int height, long bitrate, long length, string extra = "") =>
        "{\"itag\":" + itag + ",\"url\":\"https://rr1.googlevideo.com/videoplayback?itag=" + itag + "\",\"mimeType\":\"" + mime + "\",\"bitrate\":" + bitrate +
        ",\"contentLength\":\"" + length + "\",\"height\":" + height + ",\"width\":" + (height * 16 / 9) + ",\"fps\":30,\"qualityLabel\":\"" + height + "p\"" + extra + "}";

    private static JsonDocument Response(string status, string formats, string reason = "") => JsonDocument.Parse(
        "{\"playabilityStatus\":{\"status\":\"" + status + "\",\"reason\":\"" + reason + "\"}," +
        "\"videoDetails\":{\"videoId\":\"abc12345678\",\"title\":\"A title\",\"author\":\"A channel\",\"lengthSeconds\":\"212\"," +
        "\"thumbnail\":{\"thumbnails\":[{\"url\":\"https://i/small.jpg\",\"width\":120},{\"url\":\"https://i/big.jpg\",\"width\":480}]}}," +
        "\"streamingData\":{\"adaptiveFormats\":[" + formats + "]}}");

    private const string Avc = "video/mp4; codecs=\\\"avc1.640028\\\"";
    private const string Av1 = "video/mp4; codecs=\\\"av01.0.08M.08\\\"";
    private const string Vp9 = "video/webm; codecs=\\\"vp9\\\"";
    private const string Aac = "audio/mp4; codecs=\\\"mp4a.40.2\\\"";

    [Fact]
    public void Qualities_come_best_first_as_mp4_with_H264_preferred_and_webm_left_out()
    {
        var info = YouTube.Parse(Response("OK", string.Join(",",
            Format(137, Avc, 1080, 4_000_000, 50_000_000),
            Format(399, Av1, 1080, 2_000_000, 30_000_000),
            Format(248, Vp9, 1080, 5_000_000, 40_000_000),
            Format(135, Avc, 480, 1_000_000, 12_000_000),
            Format(401, Av1, 2160, 9_000_000, 200_000_000),
            Format(140, Aac, 0, 128_000, 3_000_000))));
        Assert.Equal(YtStatus.Ok, info.Status);
        Assert.Equal(["2160p", "1080p", "480p"], info.Video.Select(v => v.Label));
        Assert.Equal(137, info.Video[1].Itag); // H.264 chosen over AV1 and VP9 at the same height
        Assert.Equal("A title", info.Title);
        Assert.Equal("A channel", info.Author);
        Assert.Equal(212, info.DurationSec);
        Assert.Equal("https://i/big.jpg", info.Thumbnail);
        Assert.Equal(140, info.Audio!.Itag);
    }

    [Fact]
    public void A_dubbed_video_uses_the_original_language_audio()
    {
        string Audio(int itag, string lang, bool original) =>
            Format(itag, Aac, 0, 128_000, 3_000_000, ",\"audioTrack\":{\"displayName\":\"" + lang + "\",\"audioIsDefault\":" + (original ? "true" : "false") + "}");
        var info = YouTube.Parse(Response("OK", string.Join(",", Format(137, Avc, 1080, 4_000_000, 50_000_000), Audio(140, "German", false), Audio(141, "English", true), Audio(142, "French", false))));
        Assert.Equal(141, info.Audio!.Itag);
    }

    [Theory]
    [InlineData("LOGIN_REQUIRED", "Sign in to confirm you are not a bot", YtStatus.LoginRequired)]
    [InlineData("LOGIN_REQUIRED", "This video is DRM protected.", YtStatus.Protected)]
    [InlineData("UNPLAYABLE", "Video unavailable", YtStatus.Unavailable)]
    [InlineData("LIVE_STREAM_OFFLINE", "", YtStatus.LiveOffline)]
    public void Videos_that_cannot_be_offered_say_why(string status, string reason, YtStatus expected)
    {
        var info = YouTube.Parse(Response(status, "", reason));
        Assert.Equal(expected, info.Status);
        Assert.Empty(info.Video);
        if (reason.Length > 0) Assert.Equal(reason, info.Reason);
    }
}

public class PairEngineTests : IDisposable
{
    public PairEngineTests() => Ffmpeg.Disabled = true;
    public void Dispose() => Ffmpeg.Disabled = false;

    private static string? DashDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests", "server", "media", "dash-real"))) d = d.Parent;
        return d is null ? null : Path.Combine(d.FullName, "tests", "server", "media", "dash-real");
    }

    private static byte[] Joined(string dir, int stream)
    {
        using var ms = new MemoryStream();
        ms.Write(File.ReadAllBytes(Path.Combine(dir, $"init-stream{stream}.m4s")));
        foreach (var f in Directory.GetFiles(dir, $"chunk-stream{stream}-*.m4s").OrderBy(x => x)) ms.Write(File.ReadAllBytes(f));
        return ms.ToArray();
    }

    [Fact]
    public async Task Video_and_audio_are_fetched_together_and_come_out_as_one_playable_mp4()
    {
        var dir = DashDir();
        if (dir is null || !FfmpegTools.Available) return;
        // In the Media Lab test media, stream 0 is a video quality and stream 2 is the audio.
        var video = Joined(dir, 0);
        var audio = Joined(dir, 2);
        using var box = new Sandbox();
        using var vs = new TestServer(video);
        using var asv = new TestServer(audio);
        using var engine = box.Engine(connections: 4);
        var item = engine.Add(new DownloadRequest(vs.BaseUrl + "/v", "film (best).mp4", Pair: new PairSpec(vs.BaseUrl + "/v", video.Length, asv.BaseUrl + "/a", audio.Length)));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(40));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Null(item.Note);
        Assert.Equal("film (best).mp4", item.FileName);
        Assert.Equal(["video", "audio"], FfmpegTools.StreamKinds(item.FinalPath));
        Assert.Equal("", FfmpegTools.DecodeErrors(item.FinalPath)); // every frame and sample decodes
        Assert.Empty(Directory.GetFiles(box.Files, "*.vireopart"));
    }

    [Fact]
    public async Task Two_files_that_cannot_be_joined_are_kept_and_progress_counts_both()
    {
        using var box = new Sandbox();
        using var vs = new TestServer(2_000_000) { ChunkDelayMs = 3 };
        using var asv = new TestServer(600_000) { ChunkDelayMs = 3 };
        using var engine = box.Engine(connections: 3);
        var item = engine.Add(new DownloadRequest(vs.BaseUrl + "/v", "clip.mp4", Pair: new PairSpec(vs.BaseUrl + "/v", 2_000_000, asv.BaseUrl + "/a", 600_000)));
        long peak = 0;
        engine.ItemChanged += i => peak = Math.Max(peak, i.Downloaded);
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(40));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Contains("two files", item.Note);
        Assert.Equal(vs.Data, File.ReadAllBytes(item.FinalPath));
        Assert.Equal(asv.Data, File.ReadAllBytes(Path.Combine(box.Files, "clip (audio).m4a")));
        Assert.True(peak > 1_000_000, $"progress peaked at {peak}");
    }

    [Fact]
    public async Task A_pair_can_be_paused_and_continues_where_both_files_stopped()
    {
        using var box = new Sandbox();
        using var vs = new TestServer(3_000_000) { ChunkDelayMs = 8 };
        using var asv = new TestServer(1_000_000) { ChunkDelayMs = 8 };
        using var engine = box.Engine(connections: 2);
        var item = engine.Add(new DownloadRequest(vs.BaseUrl + "/v", "p.mp4", Pair: new PairSpec(vs.BaseUrl + "/v", 3_000_000, asv.BaseUrl + "/a", 1_000_000)));
        while (item.Downloaded < 800_000) await Task.Delay(20);
        engine.Pause(item);
        while (item.State != DownloadState.Paused) await Task.Delay(20);
        var kept = item.Downloaded;
        vs.ChunkDelayMs = asv.ChunkDelayMs = 0;
        engine.Resume(item);
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(40));
        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(vs.Data, File.ReadAllBytes(item.FinalPath));
        Assert.True(kept > 500_000);
    }
}

/// <summary>Real YouTube, only when asked for: set VIREO_LIVE=1 and run the test.</summary>
public class LiveYouTubeTests
{
    [Fact]
    public async Task A_real_video_is_found_and_saved_as_one_playable_mp4()
    {
        if (Environment.GetEnvironmentVariable("VIREO_LIVE") != "1" || !FfmpegTools.Available) return;
        Ffmpeg.Disabled = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var info = await YouTube.ResolveAsync("dQw4w9WgXcQ", http);
            Assert.Equal(YtStatus.Ok, info.Status);
            Assert.True(info.Video.Count >= 5, $"{info.Video.Count} qualities");
            Assert.NotNull(info.Audio);

            var v = info.Video.Last(); // the smallest: this is a test, not a download
            using var box = new Sandbox();
            using var engine = box.Engine(connections: 6);
            var item = engine.Add(new DownloadRequest(v.Url, "rick.mp4", Pair: new PairSpec(v.Url, v.Length, info.Audio!.Url, info.Audio.Length)));
            await engine.WhenIdleAsync(TimeSpan.FromMinutes(3));
            Assert.Equal(DownloadState.Completed, item.State);
            Assert.Null(item.Note);
            Assert.Equal(["video", "audio"], FfmpegTools.StreamKinds(item.FinalPath));
            Assert.Equal("", FfmpegTools.DecodeErrors(item.FinalPath));
            Assert.InRange(FfmpegTools.Duration(item.FinalPath), 205, 220);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "live-youtube.txt"), $"{info.Title} by {info.Author}: {info.Video.Count} qualities ({string.Join(", ", info.Video.Select(x => x.Label))}); saved {v.Label} + audio as {new FileInfo(item.FinalPath).Length / 1024} KB, {FfmpegTools.Duration(item.FinalPath):0.0}s");
        }
        finally { Ffmpeg.Disabled = false; }
    }
}

internal static class FfmpegTools
{
    public static bool Available { get; } = Runs("ffprobe", "-version") && Runs("ffmpeg", "-version");

    private static bool Runs(string exe, string args)
    {
        try { using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }); p!.WaitForExit(); return p.ExitCode == 0; }
        catch { return false; }
    }

    private static JsonElement Probe(string path)
    {
        using var p = Process.Start(new ProcessStartInfo("ffprobe", $"-v error -show_format -show_streams -of json \"{path}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var json = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public static string[] StreamKinds(string path) => [.. Probe(path).GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()!)];
    public static (int Width, int Height) Size(string path)
    {
        var v = Probe(path).GetProperty("streams").EnumerateArray().First(x => x.GetProperty("codec_type").GetString() == "video");
        return (v.GetProperty("width").GetInt32(), v.GetProperty("height").GetInt32());
    }
    public static double Duration(string path) => double.Parse(Probe(path).GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    public static string DecodeErrors(string path)
    {
        using var p = Process.Start(new ProcessStartInfo("ffmpeg", $"-v error -i \"{path}\" -f null -") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        var errors = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return errors.Trim();
    }
}
