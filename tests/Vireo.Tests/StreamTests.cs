using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Vireo.Downloads;
using Xunit;

namespace Vireo.Tests;

/// <summary>An HLS site made of random bytes: the pieces do not need to be real video to prove they arrive intact and in order.</summary>
public sealed class HlsServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, int> _hits = [];
    public string Base { get; } = "";
    public int Pieces { get; }
    public int PieceSize { get; }
    public bool Encrypted { get; set; }
    public bool ExplicitIv { get; set; } = true;
    public bool InitSegment { get; set; }
    public bool Protected { get; set; }
    public bool MasterPlaylist { get; set; }
    /// <summary>Piece i takes this long (ms), so slow pieces finish after fast ones.</summary>
    public Func<int, int> Delay { get; set; } = _ => 0;
    public int FailFirst { get; set; }
    public int MissingPiece { get; set; } = -1;
    /// <summary>Every piece from this number up to <see cref="MissingPiece"/> is gone as well.</summary>
    public int MissingFrom { get; set; } = -1;
    public bool Live { get; set; }
    public int LiveStart { get; set; } = 3;
    private DateTime _liveStarted = DateTime.UtcNow;
    public readonly byte[] Key = RandomNumberGenerator.GetBytes(16);
    public readonly byte[] Iv = RandomNumberGenerator.GetBytes(16);
    public byte[] Init { get; } = RandomNumberGenerator.GetBytes(700);

    public HlsServer(int pieces = 12, int pieceSize = 40_000)
    {
        Pieces = pieces;
        PieceSize = pieceSize;
        for (var port = 25000 + Random.Shared.Next(20000); ; port++)
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

    public byte[] Plain(int i) => new Random(1000 + i).NextBytes(PieceSize);

    public byte[] Expected(int from = 0, int? to = null) => [.. (InitSegment ? Init : []), .. Enumerable.Range(from, (to ?? Pieces) - from).SelectMany(Plain)];

    public int Hits(string path) { lock (_hits) return _hits.GetValueOrDefault(path); }

    private byte[] Wire(int i)
    {
        var plain = Plain(i);
        if (!Encrypted) return plain;
        using var aes = Aes.Create();
        aes.Key = Key;
        return aes.EncryptCbc(plain, ExplicitIv ? Iv : SequenceIv(i), PaddingMode.PKCS7);
    }

    private static byte[] SequenceIv(int seq)
    {
        var iv = new byte[16];
        iv[15] = (byte)seq;
        iv[14] = (byte)(seq >> 8);
        return iv;
    }

    private string Playlist()
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n");
        if (Protected) sb.Append("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n");
        else if (Encrypted) sb.Append($"#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"{(ExplicitIv ? ",IV=0x" + Convert.ToHexString(Iv) : "")}\n");
        if (InitSegment) sb.Append("#EXT-X-MAP:URI=\"init.mp4\"\n");
        var count = Live ? Math.Min(Pieces, LiveStart + (int)((DateTime.UtcNow - _liveStarted).TotalSeconds / 1.2)) : Pieces;
        for (var i = 0; i < count; i++) sb.Append($"#EXTINF:2.0,\np{i}.ts\n");
        if (!Live || count >= Pieces) sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    private async Task Handle(HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath;
        lock (_hits) _hits[path] = _hits.GetValueOrDefault(path) + 1;
        try
        {
            byte[] body;
            switch (path)
            {
                case "/master.m3u8":
                    body = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=640x360\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=1280x720\nindex.m3u8\n");
                    break;
                case "/index.m3u8": body = Encoding.UTF8.GetBytes(Playlist()); break;
                case "/low.m3u8": c.Response.StatusCode = 500; return;
                case "/key.bin": body = Key; break;
                case "/init.mp4": body = Init; break;
                default:
                    if (!path.StartsWith("/p") || !int.TryParse(path[2..^3], out var i) || i >= Pieces) { c.Response.StatusCode = 404; return; }
                    if (i == MissingPiece || (MissingFrom >= 0 && i >= MissingFrom && i <= MissingPiece)) { c.Response.StatusCode = 404; return; }
                    if (FailFirst > 0 && Hits(path) <= FailFirst) { c.Response.StatusCode = 503; return; }
                    var wait = Delay(i);
                    if (wait > 0) await Task.Delay(wait);
                    body = Wire(i);
                    break;
            }
            c.Response.ContentLength64 = body.Length;
            await c.Response.OutputStream.WriteAsync(body);
        }
        catch { /* client went away */ }
        finally { try { c.Response.Close(); } catch { /* closed */ } }
    }

    public void Dispose() => _listener.Close();
}

internal static class RandomExt
{
    public static byte[] NextBytes(this Random r, int n)
    {
        var b = new byte[n];
        r.NextBytes(b);
        return b;
    }
}

public class StreamTests
{
    private static string Out() => Path.Combine(AppContext.BaseDirectory, "tmp", Guid.NewGuid().ToString("N") + ".bin");

    private static StreamDownloader Downloader(int connections = 6, HttpClient? http = null) =>
        new(http ?? new HttpClient(), url => new HttpRequestMessage(HttpMethod.Get, url), new RateLimiter(), connections);

    private static async Task<(StreamTrackResult Result, byte[] Bytes, StreamProgress Progress)> Run(HlsServer s, string playlist = "/index.m3u8", StreamDownloader? d = null)
    {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "tmp"));
        var path = Out();
        var progress = new StreamProgress();
        var r = await (d ?? Downloader()).DownloadAsync(s.Base + playlist, path, progress, CancellationToken.None);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return (r, bytes, progress);
    }

    [Fact]
    public async Task Pieces_that_finish_out_of_order_are_still_written_in_order()
    {
        using var s = new HlsServer(14) { Delay = i => i % 3 == 0 ? 120 : i % 5 == 0 ? 60 : 0 };
        var (r, bytes, p) = await Run(s);
        Assert.Equal(s.Expected(), bytes);
        Assert.Equal(14, p.Done);
        Assert.Equal("ts", r.Extension);
        Assert.False(r.Fragmented);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Encrypted_pieces_are_decrypted_with_the_given_iv_or_the_pieces_number(bool explicitIv)
    {
        using var s = new HlsServer(8) { Encrypted = true, ExplicitIv = explicitIv };
        var (_, bytes, _) = await Run(s);
        Assert.Equal(s.Expected(), bytes);
        Assert.Equal(1, s.Hits("/key.bin")); // the key is fetched once
    }

    [Fact]
    public async Task A_fragmented_stream_starts_with_its_init_section()
    {
        using var s = new HlsServer(6) { InitSegment = true };
        var (r, bytes, _) = await Run(s);
        Assert.Equal(s.Expected(), bytes);
        Assert.True(r.Fragmented);
        Assert.Equal("mp4", r.Extension);
    }

    [Fact]
    public async Task A_master_playlist_is_narrowed_to_its_best_quality()
    {
        using var s = new HlsServer(5);
        var (_, bytes, _) = await Run(s, "/master.m3u8");
        Assert.Equal(s.Expected(), bytes);
    }

    [Fact]
    public async Task A_piece_that_fails_once_is_tried_again()
    {
        using var s = new HlsServer(6) { FailFirst = 1 };
        var (_, bytes, _) = await Run(s);
        Assert.Equal(s.Expected(), bytes);
    }

    [Fact]
    public async Task A_piece_that_is_gone_is_left_out_and_counted()
    {
        using var s = new HlsServer(8) { MissingPiece = 4 };
        var (result, bytes, progress) = await Run(s);
        Assert.Equal(1, progress.Skipped);
        Assert.Equal(8, result.SegmentsDone);
        Assert.Equal([.. s.Expected(0, 4), .. s.Expected(5, 8)], bytes); // everything else, in order
    }

    [Fact]
    public async Task Too_many_gone_pieces_stop_the_download_with_a_clear_error()
    {
        using var s = new HlsServer(8) { MissingPiece = 4, MissingFrom = 2 };
        var e = await Assert.ThrowsAsync<DownloadHttpException>(() => Run(s));
        Assert.Equal(HttpStatusCode.NotFound, e.Status);
    }

    [Fact]
    public async Task Protected_streams_are_refused_before_anything_is_fetched()
    {
        using var s = new HlsServer(4) { Protected = true };
        await Assert.ThrowsAsync<DrmProtectedException>(() => Run(s));
        Assert.Equal(0, s.Hits("/p0.ts"));
    }

    [Fact]
    public async Task A_stopped_download_resumes_from_the_piece_it_reached()
    {
        using var s = new HlsServer(16) { Delay = _ => 40 };
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "tmp"));
        var path = Out();
        var progress = new StreamProgress();
        using var cts = new CancellationTokenSource();
        progress.Changed += () => { if (progress.Done >= 5) cts.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader(connections: 2).DownloadAsync(s.Base + "/index.m3u8", path, progress, cts.Token));
        var done = progress.Done;
        var written = progress.Bytes;
        Assert.InRange(done, 5, 12);

        var second = new StreamProgress();
        var r = await Downloader().DownloadAsync(s.Base + "/index.m3u8", path, second, CancellationToken.None, resumePieces: done, resumeBytes: written);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        Assert.Equal(s.Expected(), bytes);
        Assert.Equal(16, r.SegmentsDone);
        Assert.Equal(1, s.Hits("/p0.ts")); // piece 0 was not fetched a second time
    }

    [Fact]
    public async Task A_live_stream_is_recorded_until_it_ends()
    {
        using var s = new HlsServer(8) { Live = true, LiveStart = 2 };
        var (_, bytes, p) = await Run(s);
        Assert.Equal(s.Expected(), bytes);
        Assert.Equal(8, p.Done);
    }

    [Fact]
    public async Task A_live_recording_can_be_stopped_and_keeps_what_it_has()
    {
        using var s = new HlsServer(40) { Live = true, LiveStart = 3 };
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "tmp"));
        var path = Out();
        var progress = new StreamProgress();
        var stop = false;
        progress.Changed += () => { if (progress.Done >= 5) stop = true; };
        var r = await Downloader().DownloadAsync(s.Base + "/index.m3u8", path, progress, CancellationToken.None, stop: () => stop);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        Assert.InRange(r.SegmentsDone, 5, 20);
        Assert.Equal(s.Expected(0, r.SegmentsDone), bytes); // a clean cut: whole pieces, in order, from the start
    }
}

public class StreamEngineTests : IDisposable
{
    public StreamEngineTests() => Ffmpeg.Disabled = true; // prove the built-in paths without it
    public void Dispose() => Ffmpeg.Disabled = false;

    private static DownloadRequest Request(HlsServer s, string name, string? audio = null, bool live = false) =>
        new(s.Base + "/index.m3u8", name, Stream: new StreamSpec(s.Base + "/index.m3u8", audio, live));

    [Fact]
    public async Task A_stream_is_a_normal_download_that_ends_as_one_file()
    {
        using var box = new Sandbox();
        using var s = new HlsServer(12);
        using var engine = box.Engine(connections: 4);
        var item = engine.Add(Request(s, "episode.mp4"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal("episode.ts", item.FileName); // plain MPEG-TS pieces; no ffmpeg to turn them into mp4
        Assert.Equal(s.Expected(), File.ReadAllBytes(Path.Combine(box.Files, "episode.ts")));
        Assert.Empty(Directory.GetFiles(box.Files, "*.vireopart"));
    }

    [Fact]
    public async Task A_stream_can_be_paused_and_picks_up_at_the_next_piece()
    {
        using var box = new Sandbox();
        using var s = new HlsServer(20) { Delay = _ => 50 };
        using var engine = box.Engine(connections: 2);
        var item = engine.Add(Request(s, "pausable.mp4"));
        while (item.Downloaded < 5 * s.PieceSize) await Task.Delay(20);
        engine.Pause(item);
        while (item.State != DownloadState.Paused) await Task.Delay(20);
        Assert.InRange(item.Done0, 5, 19);

        s.Delay = _ => 0;
        engine.Resume(item);
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(s.Expected(), File.ReadAllBytes(item.FinalPath));
        Assert.Equal(1, s.Hits("/p0.ts"));
    }

    [Fact]
    public async Task A_stream_survives_closing_the_browser()
    {
        using var box = new Sandbox();
        using var s = new HlsServer(20) { Delay = _ => 50 };
        string id;
        using (var first = box.Engine(connections: 2))
        {
            var item = first.Add(Request(s, "survivor.mp4"));
            id = item.Id;
            while (item.Downloaded < 5 * s.PieceSize) await Task.Delay(20);
        }
        s.Delay = _ => 0;
        using var second = box.Engine(connections: 4);
        var back = Assert.Single(second.Items);
        Assert.Equal(id, back.Id);
        Assert.Equal(DownloadState.Paused, back.State);
        second.Resume(back);
        await second.WhenIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(s.Expected(), File.ReadAllBytes(back.FinalPath));
    }

    [Fact]
    public async Task A_live_recording_stops_on_request_and_saves_what_it_has()
    {
        using var box = new Sandbox();
        using var s = new HlsServer(60) { Live = true, LiveStart = 3 };
        using var engine = box.Engine(connections: 3);
        var item = engine.Add(Request(s, "live.mp4", live: true));
        while (item.Done0 < 5) await Task.Delay(30);
        Assert.Contains("Recording", item.Note);
        engine.StopLive(item);
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadState.Completed, item.State);
        var bytes = File.ReadAllBytes(item.FinalPath);
        Assert.True(bytes.Length >= 5 * s.PieceSize);
        Assert.Equal(s.Expected(0, bytes.Length / s.PieceSize), bytes);
    }

    [Fact]
    public async Task A_protected_stream_is_refused_with_a_clear_message()
    {
        using var box = new Sandbox();
        using var s = new HlsServer(4) { Protected = true };
        using var engine = box.Engine();
        var item = engine.Add(Request(s, "drm.mp4"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(DownloadState.Failed, item.State);
        Assert.Contains("DRM", item.Error);
        Assert.Empty(Directory.GetFiles(box.Files).Where(f => !f.EndsWith(".json")));
    }

    [Fact]
    public async Task Video_and_audio_that_cannot_be_joined_are_kept_as_two_named_files()
    {
        using var box = new Sandbox();
        using var s = new HlsServer(6);
        using var engine = box.Engine();
        var item = engine.Add(Request(s, "film.mp4", audio: s.Base + "/index.m3u8"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Contains("two files", item.Note);
        Assert.Equal(s.Expected(), File.ReadAllBytes(Path.Combine(box.Files, "film.ts")));
        Assert.True(File.Exists(Path.Combine(box.Files, "film (audio).ts")));
    }

    [Fact]
    public async Task Cancelling_a_stream_removes_its_parts()
    {
        using var box = new Sandbox();
        using var s = new HlsServer(30) { Delay = _ => 60 };
        using var engine = box.Engine(connections: 2);
        var item = engine.Add(Request(s, "gone.mp4"));
        while (item.Downloaded < 3 * s.PieceSize) await Task.Delay(20);
        engine.Cancel(item);
        while (item.State != DownloadState.Cancelled) await Task.Delay(20);
        for (var i = 0; i < 50 && Directory.GetFiles(box.Files).Length > 0; i++) await Task.Delay(100); // removal may wait for the writer to let go
        Assert.Empty(Directory.GetFiles(box.Files));
    }
}
