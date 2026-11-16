using System.Net.Http;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using Vireo.Downloads;
using Vireo.Media;
using Xunit;

namespace Vireo.Tests;

/// <summary>A web server that behaves the way awkward streaming sites do, serving real video made by ffmpeg.</summary>
internal sealed class LabServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public string Base { get; } = "";
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentDictionary<string, string> Types { get; } = new();
    /// <summary>Gets first look at every request; return true when it answered.</summary>
    public Func<HttpListenerRequest, HttpListenerResponse, Task<bool>>? Intercept { get; set; }
    public ConcurrentQueue<string> Log { get; } = new();
    public ConcurrentQueue<string?> Referers { get; } = new();

    public LabServer()
    {
        for (var port = 30000 + Random.Shared.Next(10000); ; port++)
        {
            try { _listener.Prefixes.Add($"http://127.0.0.1:{port}/"); _listener.Start(); Base = $"http://127.0.0.1:{port}"; break; }
            catch (HttpListenerException) { _listener.Prefixes.Clear(); }
        }
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await _listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(c));
            }
        });
    }

    private async Task Handle(HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath;
        Log.Enqueue(path);
        Referers.Enqueue(c.Request.Headers["Referer"]);
        try
        {
            if (Intercept is not null && await Intercept(c.Request, c.Response)) return;
            if (!Files.TryGetValue(path, out var body)) { c.Response.StatusCode = 404; return; }
            c.Response.ContentType = Types.TryGetValue(path, out var t) ? t : path.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t";
            c.Response.ContentLength64 = body.Length;
            await c.Response.OutputStream.WriteAsync(body);
        }
        catch { /* client went away */ }
        finally { try { c.Response.Close(); } catch { /* closed */ } }
    }

    public int Count(string path) => Log.Count(p => p == path);
    public void Dispose() => _listener.Close();
}

/// <summary>A short real video cut into transport-stream pieces by ffmpeg (made once per test run).</summary>
internal static class LabVideo
{
    public sealed record Pieces(List<byte[]> Segments, double SegmentSeconds);

    private static readonly Lazy<Pieces?> Made = new(() =>
    {
        var clip = FindClip();
        if (clip is null || !FfmpegTools.Available) return null;
        var dir = Path.Combine(Path.GetTempPath(), "vireo-lab-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", $"-v error -y -i \"{clip}\" -c:v libx264 -g 30 -c:a aac -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_filename \"{dir}/s%02d.ts\" \"{dir}/index.m3u8\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            var segs = Directory.GetFiles(dir, "s*.ts").OrderBy(x => x).Select(File.ReadAllBytes).ToList();
            return segs.Count >= 3 ? new Pieces(segs, 2) : null;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* in use */ } }
    });

    public static Pieces? Get() => Made.Value;

    private static string? FindClip()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "tests", "server", "media", "clip.mp4"))) d = d.Parent;
        return d is null ? null : Path.Combine(d.FullName, "tests", "server", "media", "clip.mp4");
    }

    public static string Playlist(int count, string prefix = "", bool end = true, int sequence = 0, double target = 2)
    {
        var sb = new StringBuilder($"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:{(int)Math.Ceiling(target)}\n#EXT-X-MEDIA-SEQUENCE:{sequence}\n");
        for (var i = 0; i < count; i++) sb.Append($"#EXTINF:2.0,\n{prefix}s{i:00}.ts\n");
        if (end) sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>A few bytes of a PNG picture in front of the data, as some sites do.</summary>
    public static byte[] InPicture(byte[] data, int pictureBytes = 120)
    {
        var png = new byte[pictureBytes];
        new Random(5).NextBytes(png);
        png[0] = 0x89; png[1] = 0x50; png[2] = 0x4E; png[3] = 0x47; png[4] = 0x0D; png[5] = 0x0A; png[6] = 0x1A; png[7] = 0x0A;
        // make sure no run of three transport-stream sync bytes appears in the picture part by accident
        for (var i = 8; i < png.Length; i++) if (png[i] == 0x47) png[i] = 0x46;
        return [.. png, .. data];
    }
}

public class SegmentSnifferTests
{
    private static byte[] Ts(int packets = 5) { var d = new byte[188 * packets]; for (var i = 0; i < packets; i++) d[i * 188] = 0x47; return d; }

    [Fact]
    public void A_picture_in_front_of_transport_stream_data_is_cut_off()
    {
        var real = Ts();
        var wrapped = LabVideo.InPicture(real, 200);
        Assert.Equal(real, SegmentSniffer.Unwrap(wrapped));
    }

    [Fact]
    public void A_picture_in_front_of_an_mp4_piece_is_cut_off()
    {
        var mp4 = new byte[] { 0, 0, 0, 16, (byte)'m', (byte)'o', (byte)'o', (byte)'f', 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 9 };
        var wrapped = LabVideo.InPicture(mp4, 90);
        Assert.Equal(mp4, SegmentSniffer.Unwrap(wrapped));
    }

    [Fact]
    public void Real_pieces_and_unknown_data_are_left_alone()
    {
        var real = Ts();
        Assert.Same(real, SegmentSniffer.Unwrap(real));
        var random = new Random(3).NextBytes(5000);
        Assert.Same(random, SegmentSniffer.Unwrap(random));
        var text = Encoding.ASCII.GetBytes("WEBVTT\n\n00:00.000 --> 00:01.000\nhello\n");
        Assert.Same(text, SegmentSniffer.Unwrap(text));
    }

    [Fact]
    public void A_picture_with_nothing_behind_it_is_left_as_it_is()
    {
        var picture = LabVideo.InPicture([], 300);
        Assert.Same(picture, SegmentSniffer.Unwrap(picture));
    }
}

public class StreamLabTests
{
    private static StreamDownloader Downloader(TimeSpan? stall = null, TimeSpan? idle = null)
    {
        var d = new StreamDownloader(new HttpClient(), url => new HttpRequestMessage(HttpMethod.Get, url), new RateLimiter(), 4);
        if (stall is { } s) d.StallTimeout = s;
        if (idle is { } i) d.LiveIdleLimit = i;
        d.LiveLookInterval = TimeSpan.FromSeconds(1);
        return d;
    }

    private static async Task<(StreamTrackResult Result, string Path, StreamProgress Progress)> Get(StreamDownloader d, string url, Func<bool>? stop = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "vireo-lab-out-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        var progress = new StreamProgress();
        var r = await d.DownloadAsync(url, path, progress, CancellationToken.None, stop);
        return (r, path, progress);
    }

    private static LabServer Serve(int pieces, bool end = true)
    {
        var video = LabVideo.Get()!;
        var server = new LabServer();
        server.Files["/v/index.m3u8"] = Encoding.UTF8.GetBytes(LabVideo.Playlist(pieces, end: end));
        for (var i = 0; i < pieces; i++) server.Files[$"/v/s{i:00}.ts"] = video.Segments[i % video.Segments.Count];
        return server;
    }

    [Fact]
    public async Task A_redirected_playlist_finds_its_pieces_next_to_where_it_really_is()
    {
        if (LabVideo.Get() is null) return;
        using var server = Serve(3);
        server.Intercept = (req, res) =>
        {
            if (req.Url!.AbsolutePath != "/go/short") return Task.FromResult(false);
            res.StatusCode = 302;
            res.RedirectLocation = "/v/index.m3u8"; // the pieces are listed as "s00.ts", which means /v/s00.ts and not /go/s00.ts
            return Task.FromResult(true);
        };
        var (result, path, _) = await Get(Downloader(), server.Base + "/go/short");
        try
        {
            Assert.Equal(3, result.SegmentsDone);
            Assert.True(new FileInfo(path).Length > 10_000);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_piece_that_never_answers_is_dropped_and_asked_for_again()
    {
        if (LabVideo.Get() is null) return;
        using var server = Serve(4);
        var hung = 0;
        server.Intercept = async (req, res) =>
        {
            if (req.Url!.AbsolutePath != "/v/s02.ts" || Interlocked.Increment(ref hung) > 1) return false;
            await Task.Delay(TimeSpan.FromSeconds(60)); // the first try just sits there
            return true;
        };
        var watch = Stopwatch.StartNew();
        var (result, path, _) = await Get(Downloader(stall: TimeSpan.FromSeconds(1)), server.Base + "/v/index.m3u8");
        try
        {
            Assert.Equal(4, result.SegmentsDone);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
            Assert.Equal(2, server.Count("/v/s02.ts"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_piece_that_stops_halfway_is_also_asked_for_again()
    {
        if (LabVideo.Get() is null) return;
        using var server = Serve(3);
        var tries = 0;
        server.Intercept = async (req, res) =>
        {
            if (req.Url!.AbsolutePath != "/v/s01.ts" || Interlocked.Increment(ref tries) > 1) return false;
            var data = server.Files["/v/s01.ts"];
            res.ContentLength64 = data.Length;
            await res.OutputStream.WriteAsync(data.AsMemory(0, data.Length / 2));
            await res.OutputStream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(60)); // then nothing more
            return true;
        };
        var (result, path, _) = await Get(Downloader(stall: TimeSpan.FromSeconds(1)), server.Base + "/v/index.m3u8");
        try
        {
            Assert.Equal(3, result.SegmentsDone);
            Assert.Equal(LabVideo.Get()!.Segments.Take(3).Sum(s => (long)s.Length), new FileInfo(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Pieces_dressed_up_as_pictures_are_saved_as_the_video_they_are()
    {
        if (LabVideo.Get() is null) return;
        using var server = Serve(3);
        for (var i = 0; i < 3; i++) server.Files[$"/v/s{i:00}.ts"] = LabVideo.InPicture(server.Files[$"/v/s{i:00}.ts"], 140 + i * 30);
        foreach (var k in server.Files.Keys.Where(k => k.EndsWith(".ts"))) server.Types[k] = "image/png";
        var (result, path, _) = await Get(Downloader(), server.Base + "/v/index.m3u8");
        try
        {
            Assert.Equal(3, result.SegmentsDone);
            Assert.Equal(LabVideo.Get()!.Segments.Take(3).Sum(s => (long)s.Length), new FileInfo(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_few_missing_pieces_are_left_out_and_counted_but_too_many_is_a_failure()
    {
        if (LabVideo.Get() is null) return;
        using var few = Serve(4);
        few.Files.TryRemove("/v/s01.ts", out _);
        var (result, path, progress) = await Get(Downloader(), few.Base + "/v/index.m3u8");
        try
        {
            Assert.Equal(1, progress.Skipped);
            Assert.Equal(4, result.SegmentsDone);
        }
        finally { File.Delete(path); }

        using var many = Serve(4);
        foreach (var k in new[] { "/v/s00.ts", "/v/s01.ts", "/v/s02.ts" }) many.Files.TryRemove(k, out _);
        var e = await Assert.ThrowsAsync<DownloadHttpException>(() => Get(Downloader(), many.Base + "/v/index.m3u8"));
        Assert.Equal(HttpStatusCode.NotFound, e.Status);
    }

    [Fact]
    public async Task A_live_stream_that_just_stops_growing_is_finished_by_itself()
    {
        if (LabVideo.Get() is null) return;
        using var server = Serve(3, end: false); // three pieces and no end marker, ever
        server.Files["/v/index.m3u8"] = Encoding.UTF8.GetBytes(LabVideo.Playlist(3, end: false, target: 1));
        var watch = Stopwatch.StartNew();
        var (result, path, _) = await Get(Downloader(idle: TimeSpan.FromSeconds(2)), server.Base + "/v/index.m3u8");
        try
        {
            Assert.Equal(3, result.SegmentsDone);
            Assert.InRange(watch.Elapsed.TotalSeconds, 3, 25);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_live_stream_whose_playlist_disappears_keeps_what_it_recorded()
    {
        if (LabVideo.Get() is null) return;
        using var server = Serve(3, end: false);
        server.Files["/v/index.m3u8"] = Encoding.UTF8.GetBytes(LabVideo.Playlist(3, end: false, target: 1));
        var looks = 0;
        server.Intercept = (req, res) =>
        {
            if (req.Url!.AbsolutePath != "/v/index.m3u8" || Interlocked.Increment(ref looks) <= 1) return Task.FromResult(false);
            res.StatusCode = 404; // the stream is over and the server tidied up
            return Task.FromResult(true);
        };
        var (result, path, _) = await Get(Downloader(idle: TimeSpan.FromSeconds(60)), server.Base + "/v/index.m3u8");
        try { Assert.Equal(3, result.SegmentsDone); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_live_stream_that_restarts_its_numbering_does_not_lose_pieces()
    {
        if (LabVideo.Get() is null) return;
        using var server = Serve(5, end: false);
        // first the pieces 0-2 numbered 0-2, then 3-4 numbered from 0 again (some servers do this when they re-issue the playlist)
        var looks = 0;
        server.Intercept = (req, res) =>
        {
            if (req.Url!.AbsolutePath != "/v/index.m3u8") return Task.FromResult(false);
            var n = Interlocked.Increment(ref looks);
            string text;
            if (n == 1) text = LabVideo.Playlist(3, end: false, target: 1);
            else
            {
                var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:2.0,\ns03.ts\n#EXTINF:2.0,\ns04.ts\n#EXT-X-ENDLIST\n");
                text = sb.ToString();
            }
            var bytes = Encoding.UTF8.GetBytes(text);
            res.ContentType = "application/vnd.apple.mpegurl";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes);
            return Task.FromResult(true);
        };
        var (result, path, _) = await Get(Downloader(), server.Base + "/v/index.m3u8");
        try { Assert.Equal(5, result.SegmentsDone); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Stopping_a_live_recording_is_noticed_within_a_moment()
    {
        if (LabVideo.Get() is null) return;
        using var server = Serve(3, end: false);
        server.Files["/v/index.m3u8"] = Encoding.UTF8.GetBytes(LabVideo.Playlist(3, end: false, target: 10));
        var stop = false;
        var watch = Stopwatch.StartNew();
        var task = Get(Downloader(idle: TimeSpan.FromSeconds(300)), server.Base + "/v/index.m3u8", () => stop);
        await Task.Delay(1500);
        stop = true;
        var (_, path, _) = await task;
        try { Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"took {watch.Elapsed}"); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_whole_download_of_picture_wrapped_pieces_behind_a_referer_lock_ends_as_a_playable_mp4()
    {
        if (LabVideo.Get() is null) return;
        using var server = Serve(4);
        for (var i = 0; i < 4; i++) server.Files[$"/v/s{i:00}.ts"] = LabVideo.InPicture(server.Files[$"/v/s{i:00}.ts"], 90);
        server.Types["/v/index.m3u8"] = "image/jpeg"; // the playlist itself is also disguised
        server.Intercept = (req, res) =>
        {
            // pieces and playlist are only handed to requests that say the player page sent them
            if (req.Headers["Referer"] is { } r && r.StartsWith("https://player.example")) return Task.FromResult(false);
            res.StatusCode = 403;
            return Task.FromResult(true);
        };
        using var box = new Sandbox();
        using var engine = box.Engine(connections: 4);
        var item = engine.Add(new DownloadRequest(server.Base + "/v/index.m3u8", "show.mp4", "https://player.example/embed/1", null, null, Stream: new StreamSpec(server.Base + "/v/index.m3u8", null, false, 500_000)));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.EndsWith(".mp4", item.FileName);
        Assert.Null(item.Note);
        Assert.Equal(["video", "audio"], FfmpegTools.StreamKinds(item.FinalPath));
        Assert.Equal("", FfmpegTools.DecodeErrors(item.FinalPath));
    }
}
