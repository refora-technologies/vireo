using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Vireo.Media;

namespace Vireo.Downloads;

/// <summary>The stream is protected by DRM. Vireo detects this and refuses; it never decrypts protected content.</summary>
public sealed class DrmProtectedException() : Exception("This stream is protected by DRM. Vireo does not download protected content.");

public sealed class StreamTrackResult
{
    public bool Fragmented { get; init; }
    public string Extension { get; init; } = "ts";
    public int SegmentsDone { get; init; }
    public long Bytes { get; init; }
}

/// <summary>Where a track stands, so it can be saved and picked up again.</summary>
public sealed class StreamProgress
{
    public long Bytes;
    public int Done;
    public int Count;
    public double RecordedSeconds;
    /// <summary>Pieces the server no longer had; they are left out of the file.</summary>
    public int Skipped;
    public event Action? Changed;
    internal void Raise() => Changed?.Invoke();
}

/// <summary>
/// Downloads one HLS (or DASH) stream into one file: pieces are fetched in parallel, decrypted (plain AES-128) and written
/// strictly in order. A VOD stream can be resumed from the piece it stopped at; a live stream is recorded until
/// stopped or until the stream ends.
/// </summary>
public sealed class StreamDownloader(HttpClient http, Func<string, HttpRequestMessage> newRequest, RateLimiter limiter, int connections)
{
    private const int MaxFailures = 5;
    private const int PlaylistFailuresBeforeEnd = 4;
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _keys = new();

    /// <summary>A connection that sends nothing for this long is dropped and tried again. Without it, one stuck piece stalls the whole download.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>A live stream whose playlist has shown nothing new for this long (and for at least four piece lengths) is taken as over.</summary>
    public TimeSpan LiveIdleLimit { get; set; } = TimeSpan.FromSeconds(45);
    /// <summary>How long a live recording waits between looks at the playlist, at most.</summary>
    public TimeSpan LiveLookInterval { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly byte[] Missing = [];
    private int _missing;

    private sealed class MapState
    {
        public string? Url { get; set; }
        public long? Offset { get; set; }
    }

    // When a server refuses ("403") with the page's address as the referer, a few other honest claims are tried:
    // the stream's own site, then no referer at all. The one that works is remembered so later pieces go straight to it.
    private string? _referer;
    private bool _refererChosen;

    private HttpRequestMessage Build(string url, long? offset, long? length)
    {
        var req = newRequest(url);
        if (_refererChosen) RequestHeaders.SetReferer(req, url, _referer, script: true);
        if (offset is { } o && length is { } l) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(o, o + l - 1);
        return req;
    }

    /// <summary>Sends a request; a server that does not answer at all within the stall time counts as a dropped connection.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(StallTimeout);
        try { return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("The server did not answer."); }
    }

    /// <summary>Reads the body; it may take as long as it likes as long as data keeps arriving.</summary>
    private async Task<byte[]> ReadBodyAsync(HttpResponseMessage res, CancellationToken ct)
    {
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var ms = new MemoryStream((int)Math.Clamp(res.Content.Headers.ContentLength ?? 0, 0, 64 << 20));
        var buffer = new byte[1 << 16];
        while (true)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(StallTimeout);
            int n;
            try { n = await stream.ReadAsync(buffer, limit.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("The server stopped sending."); }
            if (n == 0) return ms.ToArray();
            ms.Write(buffer, 0, n);
        }
    }

    private static string FinalUrl(HttpResponseMessage res, string url) => res.RequestMessage?.RequestUri?.ToString() ?? url;

    private async Task<(byte[] Data, string FinalUrl)?> TryOtherRefererAsync(string url, long? offset, long? length, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        foreach (var candidate in new[] { uri.GetLeftPart(UriPartial.Authority) + "/", "" })
        {
            using var req = Build(url, offset, length);
            RequestHeaders.SetReferer(req, url, candidate, script: true);
            using var res = await SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) continue;
            var data = await ReadBodyAsync(res, ct);
            _referer = candidate;
            _refererChosen = true;
            return (data, FinalUrl(res, url));
        }
        return null;
    }

    /// <summary>The bytes at an address, and the address they really came from (after any redirects: relative links resolve against that).</summary>
    private async Task<(byte[] Data, string FinalUrl)> FetchAsync(string url, long? offset, long? length, CancellationToken ct)
    {
        var failures = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = Build(url, offset, length);
                using var res = await SendAsync(req, ct);
                (byte[] Data, string FinalUrl) got;
                if (res.StatusCode == HttpStatusCode.Forbidden && !_refererChosen)
                {
                    got = await TryOtherRefererAsync(url, offset, length, ct) ?? throw new DownloadHttpException(res.StatusCode);
                }
                else
                {
                    if (!res.IsSuccessStatusCode) throw new DownloadHttpException(res.StatusCode);
                    got = (await ReadBodyAsync(res, ct), FinalUrl(res, url));
                }
                await limiter.WaitAsync(got.Data.Length, ct);
                return got;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (DownloadHttpException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or DownloadHttpException or TimeoutException)
            {
                if (++failures > MaxFailures) throw;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(6000, 400 * Math.Pow(2, failures - 1))), ct);
            }
        }
    }

    private async Task<byte[]> GetBytesAsync(string url, long? offset, long? length, CancellationToken ct) => (await FetchAsync(url, offset, length, ct)).Data;

    private async Task<byte[]> SegmentAsync(HlsSegment s, CancellationToken ct)
    {
        var data = await GetBytesAsync(s.Url, s.Offset, s.Length, ct);
        if (s.Key is { Method: "AES-128", Uri: { } uri })
        {
            // Lazy makes sure only one connection fetches the key while the others wait for it.
            try
            {
                var key = await _keys.GetOrAdd(uri, u => new Lazy<Task<byte[]>>(() => GetBytesAsync(u, null, null, ct))).Value;
                if (key.Length != 16) throw new InvalidDataException("The stream key is not a valid AES-128 key.");
                using var aes = Aes.Create();
                aes.Key = key;
                var iv = s.Key.Iv ?? HlsParser.SequenceIv(s.Sequence);
                try { data = aes.DecryptCbc(data, iv, PaddingMode.PKCS7); }
                catch (CryptographicException) when (data.Length % 16 == 0) { data = aes.DecryptCbc(data, iv, PaddingMode.None); }
            }
            catch
            {
                _keys.TryRemove(uri, out _);
                throw;
            }
        }
        return SegmentSniffer.Unwrap(data); // pieces dressed up as pictures
    }

    /// <summary>Fetches the playlist; a master playlist is narrowed to its best video quality.</summary>
    public async Task<(HlsMedia Media, string Url)> LoadAsync(string playlistUrl, CancellationToken ct)
    {
        var url = playlistUrl;
        for (var hop = 0; hop < 3; hop++)
        {
            var (bytes, finalUrl) = await FetchAsync(url, null, null, ct);
            var text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            if (DashParser.IsManifest(text))
            {
                // A DASH manifest: the address says which quality or sound to take (see DashManifest.Tag).
                var dash = DashParser.Parse(text, finalUrl);
                if (dash.Protected) throw new DrmProtectedException();
                var wanted = dash.Find(DashManifest.RepresentationOf(url)) ?? dash.Videos.FirstOrDefault() ?? dash.BestAudio(false) ?? throw new InvalidDataException("The stream lists nothing to download.");
                if (wanted.Segments.Count == 0) throw new InvalidDataException("This quality is a single file, not a list of pieces.");
                return (wanted.ToMedia(dash.Live, dash.TargetDuration), url);
            }
            if (!HlsParser.IsPlaylist(text)) throw new InvalidDataException("The stream playlist could not be read.");
            var parsed = HlsParser.Parse(text, finalUrl);
            if (parsed is HlsMedia media)
            {
                if (media.Drm || media.Segments.Any(s => s.Key is { Method: "OTHER" })) throw new DrmProtectedException();
                if (media.Segments.Count == 0) throw new InvalidDataException("The stream playlist is empty.");
                return (media, url);
            }
            var master = (HlsMaster)parsed;
            if (master.Drm) throw new DrmProtectedException();
            url = (master.Variants.FirstOrDefault(v => !v.AudioOnly) ?? master.Variants.FirstOrDefault())?.Url ?? throw new InvalidDataException("The stream lists no qualities.");
        }
        throw new InvalidDataException("The stream playlist could not be read.");
    }

    private static string KeyOf(HlsSegment s) => s.Offset is { } o ? $"{s.Url}#{o}" : s.Url;

    /// <summary>
    /// Downloads the stream to <paramref name="outPath"/>. Resuming: pass how many pieces and bytes are already in the file.
    /// For a live stream, <paramref name="stop"/> ends the recording and keeps what was recorded; it also ends by itself when
    /// the stream says it is over, stops growing, or its playlist goes away.
    /// </summary>
    public async Task<StreamTrackResult> DownloadAsync(string playlistUrl, string outPath, StreamProgress progress, CancellationToken ct, Func<bool>? stop = null, int resumePieces = 0, long resumeBytes = 0)
    {
        var (media, url) = await LoadAsync(playlistUrl, ct);
        progress.Count = media.Segments.Count;
        var first = media.Segments[0];
        var extension = media.Container ?? (first.Map is not null ? "mp4" : Path.GetExtension(new Uri(first.Url).AbsolutePath).TrimStart('.').ToLowerInvariant() is "aac" or "mp3" or "m4a" ? Path.GetExtension(new Uri(first.Url).AbsolutePath).TrimStart('.').ToLowerInvariant() : "ts");

        await using var file = new FileStream(outPath, resumePieces > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
        if (resumePieces > 0) { file.SetLength(resumeBytes); file.Seek(resumeBytes, SeekOrigin.Begin); }
        progress.Bytes = resumeBytes;
        progress.Done = resumePieces;
        _missing = 0;

        var mapState = new MapState();
        if (first.Map is { } map)
        {
            mapState.Url = map.Url;
            mapState.Offset = map.Offset;
            if (resumePieces == 0)
            {
                var init = await GetBytesAsync(map.Url, map.Offset, map.Length, ct);
                await file.WriteAsync(init, ct);
                progress.Bytes += init.Length;
            }
        }

        // Pieces are told apart by address, not by number: some servers restart the numbering or reissue the playlist.
        var seen = new HashSet<string>(media.Segments.Select(KeyOf), StringComparer.Ordinal);
        var todo = media.Segments.Skip(resumePieces).ToList();
        var budget = media.Live ? int.MaxValue : Math.Max(2, (int)Math.Ceiling(media.Segments.Count * 0.02)); // pieces that may be missing before it is a failure
        var lastNew = DateTime.UtcNow;
        var playlistFailures = 0;
        while (true)
        {
            await WritePiecesAsync(todo, file, progress, mapState, budget, media.Live, ct);
            if (!media.Live || stop?.Invoke() == true) break;

            // Live: look at the playlist again and take what is new, until it says it is over, stops growing, or goes away.
            var idleLimit = TimeSpan.FromSeconds(Math.Max(LiveIdleLimit.TotalSeconds, media.TargetDuration * 4));
            var wait = TimeSpan.FromSeconds(Math.Clamp(media.TargetDuration / 2, 1, LiveLookInterval.TotalSeconds));
            todo = [];
            var over = false;
            while (todo.Count == 0 && !over)
            {
                for (var waited = TimeSpan.Zero; waited < wait && stop?.Invoke() != true; waited += TimeSpan.FromMilliseconds(250)) await Task.Delay(250, ct);
                if (stop?.Invoke() == true) { over = true; break; }
                try
                {
                    var (fresh, _) = await LoadAsync(url, ct);
                    playlistFailures = 0;
                    todo = [.. fresh.Segments.Where(s => seen.Add(KeyOf(s)))];
                    media = fresh;
                    if (todo.Count > 0) lastNew = DateTime.UtcNow;
                    else if (!fresh.Live) over = true;                                      // the stream announced its end
                    else if (DateTime.UtcNow - lastNew > idleLimit) over = true;           // nothing new for a long time: it ended without saying so
                }
                catch (Exception e) when (e is DownloadHttpException or HttpRequestException or InvalidDataException or TimeoutException)
                {
                    // The playlist is gone (the stream ended and the server cleaned up): keep what was recorded.
                    if (++playlistFailures >= PlaylistFailuresBeforeEnd) over = true;
                }
            }
            progress.Count += todo.Count;
            if (over && todo.Count == 0) break;
        }
        await file.FlushAsync(ct);
        return new StreamTrackResult { Fragmented = first.Map is not null && media.Container is null, Extension = extension, SegmentsDone = progress.Done, Bytes = progress.Bytes };
    }

    /// <summary>Fetches pieces in parallel and writes them in order. Progress is counted only once a piece is safely written.</summary>
    private async Task WritePiecesAsync(IReadOnlyList<HlsSegment> pieces, FileStream file, StreamProgress progress, MapState mapState, int missingBudget, bool live, CancellationToken ct)
    {
        if (pieces.Count == 0) return;
        var window = Math.Max(4, connections * 2);
        var ready = new ConcurrentDictionary<int, byte[]>();
        var cursor = -1;
        var written = -1;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? failure = null;

        var workers = Enumerable.Range(0, Math.Max(1, Math.Min(connections, pieces.Count))).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var i = Interlocked.Increment(ref cursor);
                    if (i >= pieces.Count) return;
                    while (i - Volatile.Read(ref written) > window && !cts.IsCancellationRequested) await Task.Delay(5, cts.Token);
                    try { ready[i] = await SegmentAsync(pieces[i], cts.Token); }
                    catch (DownloadHttpException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone && Interlocked.Increment(ref _missing) <= missingBudget)
                    {
                        ready[i] = Missing; // gone from the server: leave it out rather than lose everything else
                    }
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException && cts.IsCancellationRequested))
            {
                Interlocked.CompareExchange(ref failure, e, null);
                cts.Cancel();
            }
            catch (OperationCanceledException) { /* stopped */ }
        }, CancellationToken.None)).ToArray();

        try
        {
            for (var i = 0; i < pieces.Count; i++)
            {
                byte[]? data;
                while (!ready.TryRemove(i, out data))
                {
                    if (failure is not null || cts.IsCancellationRequested) goto done;
                    await Task.Delay(3, CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                }
                if (ReferenceEquals(data, Missing))
                {
                    Volatile.Write(ref written, i);
                    if (!live) progress.Skipped++; // at the live edge a piece that is not there yet or is already gone is normal, not a loss to report
                    progress.Done++;
                    progress.RecordedSeconds += pieces[i].Duration;
                    progress.Raise();
                    continue;
                }
                var seg = pieces[i];
                if (seg.Map is { } segMap && (segMap.Url != mapState.Url || segMap.Offset != mapState.Offset))
                {
                    var initBytes = await GetBytesAsync(segMap.Url, segMap.Offset, segMap.Length, ct);
                    await file.WriteAsync(initBytes, ct);
                    progress.Bytes += initBytes.Length;
                    mapState.Url = segMap.Url;
                    mapState.Offset = segMap.Offset;
                }
                await file.WriteAsync(data, ct);
                Volatile.Write(ref written, i);
                progress.Bytes += data.Length;
                progress.Done++;
                progress.RecordedSeconds += pieces[i].Duration;
                progress.Raise();
            }
        }
        finally
        {
            cts.Cancel();
            await Task.WhenAll(workers);
        }
    done:
        ct.ThrowIfCancellationRequested();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
