using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Vireo.Downloads;

public sealed class DownloadOptions
{
    public int Connections { get; set; } = 8;
    public int MaxActive { get; set; } = 3;
    public int SpeedLimitKBps { get; set; }
    public string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}

public sealed class DownloadHttpException(HttpStatusCode status) : Exception($"Server answered {(int)status}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Downloads files the way a download manager does: the file is split across several connections,
/// fast connections take over the tail of slow ones, everything is written straight to its place in a
/// pre-sized file, and progress is saved so a download survives a pause, a dropped connection or a crash.
/// </summary>
public sealed partial class DownloadEngine : IDisposable
{
    internal const long MinSplit = 256 * 1024;
    private const int ChunkSize = 64 * 1024;
    private const int MaxFailures = 6;
    private const string DefaultAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    /// <summary>Where each download's record is kept so it survives a restart; none for a private window (memory only).</summary>
    private readonly string? _stateFolder;
    private readonly object _stateGate = new();

    /// <summary>A private window's downloads: nothing about them is written anywhere but the saved file itself.</summary>
    public bool IsPrivate => _stateFolder is null;
    private readonly List<DownloadItem> _items = [];
    private readonly object _gate = new();
    private readonly RateLimiter _limiter = new();
    private readonly Timer _ticker;
    private bool _disposed;

    public DownloadOptions Options { get; }
    public IReadOnlyList<DownloadItem> Items { get { lock (_gate) return [.. _items]; } }

    public event Action<DownloadItem>? ItemAdded;
    public event Action<DownloadItem>? ItemRemoved;
    public event Action<DownloadItem>? ItemChanged;

    /// <param name="stateFolder">Where records are kept; null for a private window, whose list lives only in memory.</param>
    public DownloadEngine(string? stateFolder, DownloadOptions? options = null)
    {
        Options = options ?? new DownloadOptions();
        _stateFolder = stateFolder;
        if (stateFolder is not null) Directory.CreateDirectory(stateFolder);
        // Playlists a page made in script are answered from memory (see InlinePlaylists); everything else goes out as usual.
        _http = new HttpClient(new Vireo.Media.InlinePlaylists.Handler(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None, // we count and store the bytes exactly as sent
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 64,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })) { Timeout = Timeout.InfiniteTimeSpan };
        Load();
        _ticker = new Timer(_ => Sample(), null, 500, 500);
    }

    // ---- Adding and controlling ----

    /// <summary>Picks the folder for a download that names none (a private window asks where its downloads go); null = the usual one.</summary>
    public Func<string?>? ChooseFolder { get; set; }

    public DownloadItem Add(DownloadRequest request)
    {
        var folder = string.IsNullOrWhiteSpace(request.Folder) ? ChooseFolder?.Invoke() ?? Options.Folder : request.Folder;
        Directory.CreateDirectory(folder);
        var item = new DownloadItem
        {
            Url = request.Url, Referer = request.Referer, UserAgent = request.UserAgent, Cookie = request.Cookie,
            Folder = folder, Connections = Options.Connections,
        };
        item.Stream = request.Stream;
        item.Pair = request.Pair;
        if (!string.IsNullOrWhiteSpace(request.FileName)) item.FileName = Reserve(folder, FileNaming.Sanitize(request.FileName));
        lock (_gate) _items.Insert(0, item);
        Save(item);
        ItemAdded?.Invoke(item);
        Pump();
        return item;
    }

    public void Pause(DownloadItem item)
    {
        lock (item.Lock)
        {
            item.RetryAttempts = 0;
            ClearReconnectNote(item);
            if (item.State == DownloadState.Downloading) { item.Stop = StopReason.Pause; item.Cts?.Cancel(); return; }
            if (item.State == DownloadState.Queued) item.State = DownloadState.Paused;
        }
        Changed(item, save: true);
    }

    public void Resume(DownloadItem item)
    {
        lock (item.Lock)
        {
            if (item.State is not (DownloadState.Paused or DownloadState.Failed)) return;
            item.State = DownloadState.Queued;
            item.Error = null;
            item.RetryAttempts = 0;
            ClearReconnectNote(item);
        }
        Changed(item, save: true);
        Pump();
    }

    public void Cancel(DownloadItem item)
    {
        bool running;
        lock (item.Lock)
        {
            running = item.State == DownloadState.Downloading && item.Cts is not null;
            if (running) { item.Stop = StopReason.Cancel; item.Cts!.Cancel(); }
        }
        if (running) return; // the running task cleans up
        DiscardPartial(item);
        item.State = DownloadState.Cancelled;
        Changed(item, save: true);
        Pump();
    }

    /// <summary>Takes a download off the list. With <paramref name="deleteFile"/> the finished file goes too.</summary>
    public void Remove(DownloadItem item, bool deleteFile = false)
    {
        if (item.State == DownloadState.Downloading) Cancel(item);
        lock (_gate) _items.Remove(item);
        if (_stateFolder is not null) try { File.Delete(StatePath(item)); } catch { /* already gone */ }
        if (deleteFile && item.State == DownloadState.Completed) try { File.Delete(item.FinalPath); } catch { /* in use */ }
        DiscardPartial(item);
        ItemRemoved?.Invoke(item);
        Pump();
    }

    /// <summary>For tests and shutdown: waits until nothing is running or queued.</summary>
    public async Task WhenIdleAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (Items.All(i => !i.IsActive)) return;
            await Task.Delay(30);
        }
        throw new TimeoutException("Downloads did not finish in time: " + string.Join(", ", Items.Where(i => i.IsActive).Select(i => $"{i.FileName} {i.State} {i.Downloaded}/{i.Total}")));
    }

    // ---- Scheduling ----

    private void Pump()
    {
        if (_disposed) return;
        _limiter.BytesPerSecond = Options.SpeedLimitKBps * 1024L;
        List<DownloadItem> toStart = [];
        lock (_gate)
        {
            var running = _items.Count(i => i.State == DownloadState.Downloading);
            // Oldest first.
            foreach (var item in Enumerable.Reverse(_items))
            {
                if (running >= Math.Max(1, Options.MaxActive)) break;
                if (item.State != DownloadState.Queued) continue;
                item.State = DownloadState.Downloading;
                item.Stop = StopReason.None;
                item.Cts = new CancellationTokenSource();
                running++;
                toStart.Add(item);
            }
        }
        foreach (var item in toStart)
        {
            item.Connections = Options.Connections;
            var ct = item.Cts!.Token;
            item.Run = Task.Run(() => RunAsync(item, ct));
            Changed(item, save: false);
        }
    }

    private void Changed(DownloadItem item, bool save)
    {
        if (item.Parent is { } parent) { item = parent; save = false; } // a part of a pair is reported as its whole
        if (save) Save(item);
        item.RaiseChanged();
        ItemChanged?.Invoke(item);
    }

    /// <summary>How many times a dropped connection is retried before the download is given up as failed.</summary>
    private const int MaxReconnects = 3;
    private const string ReconnectNote = "Reconnecting";

    private async Task RunAsync(DownloadItem item, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                try
                {
                    await RunOnceAsync(item, ct);
                    item.RetryAttempts = 0;
                    ClearReconnectNote(item);
                    return;
                }
                catch (OperationCanceledException) when (item.Stop != StopReason.None)
                {
                    Stopped(item);
                    return;
                }
                catch (Exception e) when (item.Stop == StopReason.None && IsTransient(e) && item.RetryAttempts < MaxReconnects)
                {
                    lock (item.Lock) item.ActiveConnections = 0;
                    item.RetryAttempts++;
                    item.Speed = 0;
                    item.Note = $"{ReconnectNote} (attempt {item.RetryAttempts}/{MaxReconnects})…";
                    Changed(item, save: false);
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, Math.Pow(2, item.RetryAttempts))), ct); }
                    catch (OperationCanceledException) when (item.Stop != StopReason.None)
                    {
                        // Paused or cancelled while waiting to try again.
                        Stopped(item);
                        return;
                    }
                    ClearReconnectNote(item);
                    Changed(item, save: false);
                }
                catch (Exception e)
                {
                    lock (item.Lock) item.ActiveConnections = 0;
                    ClearReconnectNote(item);
                    item.State = DownloadState.Failed;
                    item.Error = Friendly(e);
                    item.Speed = 0;
                    Changed(item, save: true);
                    return;
                }
            }
        }
        finally
        {
            Pump();
        }
    }

    /// <summary>One go at the whole job. A failure leaves the progress made so far, so the next go carries on from there.</summary>
    private async Task RunOnceAsync(DownloadItem item, CancellationToken ct)
    {
        if (item.Pair is not null) { await RunPairAsync(item, ct); return; }
        if (item.Stream is not null) { await RunStreamAsync(item, ct); return; }
        await ProbeAsync(item, ct);
        using (var file = File.OpenHandle(item.PartPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous))
        {
            if (item.CanResume && item.Total > 0 && RandomAccess.GetLength(file) < item.Total) RandomAccess.SetLength(file, item.Total);
            await TransferAsync(item, file, ct);
        }
        Finish(item);
    }

    /// <summary>The person paused or cancelled it.</summary>
    private void Stopped(DownloadItem item)
    {
        lock (item.Lock) item.ActiveConnections = 0;
        if (item.Stop == StopReason.Cancel) { DiscardPartial(item); item.State = DownloadState.Cancelled; }
        else item.State = DownloadState.Paused;
        item.Speed = 0;
        item.RetryAttempts = 0;
        ClearReconnectNote(item);
        Changed(item, save: true);
    }

    /// <summary>Only the "reconnecting" message goes away; other notes (such as "saved as two files") must stay.</summary>
    private static void ClearReconnectNote(DownloadItem item)
    {
        if (item.Note?.StartsWith(ReconnectNote, StringComparison.Ordinal) == true) item.Note = null;
    }

    // ---- Step 1: ask the server what this is ----

    private HttpRequestMessage NewRequest(DownloadItem item)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, item.Url);
        RequestHeaders.Apply(req, item.Url, item.UserAgent ?? DefaultAgent, item.Referer, item.Cookie, script: false);
        return req;
    }

    private async Task ProbeAsync(DownloadItem item, CancellationToken ct)
    {
        using var req = NewRequest(item);
        req.Headers.Range = new RangeHeaderValue(0, 0);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        long total;
        bool canResume;
        if (res.StatusCode == HttpStatusCode.PartialContent)
        {
            total = res.Content.Headers.ContentRange?.Length ?? -1;
            canResume = total > 0;
        }
        else if (res.IsSuccessStatusCode)
        {
            total = res.Content.Headers.ContentLength ?? -1;
            canResume = false; // the server ignored the range: one connection only
        }
        else throw new DownloadHttpException(res.StatusCode);

        var etag = res.Headers.ETag?.Tag;
        if (string.IsNullOrEmpty(item.FileName))
        {
            var proposed = FileNaming.Sanitize(FileNaming.FromDisposition(res.Content.Headers.ContentDisposition) ?? FileNaming.FromUrl(res.RequestMessage?.RequestUri?.ToString() ?? item.Url), res.Content.Headers.ContentType?.MediaType);
            item.FileName = Reserve(item.Folder, proposed);
        }

        // A half-finished file is only good if it is still the same file on the server.
        bool changed;
        lock (item.Lock)
        {
            changed = item.Segments.Count > 0 && (!canResume || item.Total != total || (item.ETag is not null && etag is not null && item.ETag != etag));
            if (changed) item.Segments.Clear();
        }
        if (changed) DiscardPartial(item);

        item.Total = total;
        item.CanResume = canResume;
        item.ETag = etag;
        item.LastModified = res.Content.Headers.LastModified?.ToString("R");
        item.ContentType = res.Content.Headers.ContentType?.MediaType;
        Changed(item, save: true);
    }

    // ---- Step 2: move the bytes ----

    private async Task TransferAsync(DownloadItem item, SafeFileHandle file, CancellationToken ct)
    {
        if (!(item.CanResume && item.Total > 0))
        {
            await StreamAsync(item, file, ct);
            return;
        }

        lock (item.Lock)
        {
            if (item.Segments.Count == 0) Plan(item);
            foreach (var s in item.Segments) s.Active = false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? fatal = null;
        var tasks = Enumerable.Range(0, Math.Max(1, item.Connections)).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var seg = item.TakeSegment(MinSplit);
                    if (seg is null) return;
                    await FetchSegmentAsync(item, seg, file, linked.Token);
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException && linked.IsCancellationRequested))
            {
                Interlocked.CompareExchange(ref fatal, e, null);
                linked.Cancel();
            }
        }, CancellationToken.None)).ToArray();
        await Task.WhenAll(tasks);

        ct.ThrowIfCancellationRequested();
        if (fatal is not null) ExceptionDispatchInfo.Capture(fatal).Throw();
        lock (item.Lock)
            if (item.Segments.Any(s => s.Remaining > 0)) throw new IOException("The download ended before the whole file arrived.");
    }

    private static void Plan(DownloadItem item)
    {
        var count = (int)Math.Clamp(item.Total / MinSplit, 1, Math.Max(1, item.Connections));
        var size = item.Total / count;
        for (var i = 0; i < count; i++)
        {
            var start = i * size;
            var end = i == count - 1 ? item.Total - 1 : start + size - 1;
            item.Segments.Add(new Segment { Start = start, End = end, Done = start });
        }
    }

    private async Task FetchSegmentAsync(DownloadItem item, Segment seg, SafeFileHandle file, CancellationToken ct)
    {
        var failures = 0;
        var buffer = new byte[ChunkSize];
        while (true)
        {
            long start, end;
            lock (item.Lock)
            {
                if (seg.Remaining <= 0) return;
                (start, end) = (seg.Done, seg.End);
            }
            ct.ThrowIfCancellationRequested();
            var progressed = false;
            lock (item.Lock) item.ActiveConnections++;
            try
            {
                using var req = NewRequest(item);
                req.Headers.Range = new RangeHeaderValue(start, end);
                if (item.ETag is not null && !item.ETag.StartsWith("W/")) req.Headers.TryAddWithoutValidation("If-Range", item.ETag);
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (res.StatusCode != HttpStatusCode.PartialContent)
                {
                    if (res.IsSuccessStatusCode) throw new IOException("The server stopped allowing partial downloads.");
                    throw new DownloadHttpException(res.StatusCode);
                }
                if (res.Content.Headers.ContentRange?.From is { } from && from != start) throw new IOException("The server sent a different part than asked for.");

                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                while (true)
                {
                    int want;
                    lock (item.Lock)
                    {
                        if (seg.Remaining <= 0) break; // another connection took the rest
                        want = (int)Math.Min(buffer.Length, seg.Remaining);
                    }
                    var read = await stream.ReadAsync(buffer.AsMemory(0, want), ct);
                    if (read == 0) break;
                    await _limiter.WaitAsync(read, ct);

                    int take;
                    long at;
                    lock (item.Lock)
                    {
                        take = (int)Math.Min(read, seg.Remaining); // the end may have moved while we were reading
                        at = seg.Done;
                    }
                    if (take <= 0) break;
                    await RandomAccess.WriteAsync(file, buffer.AsMemory(0, take), at, ct);
                    lock (item.Lock) seg.Done += take;
                    Interlocked.Add(ref item.WindowBytes, take);
                    if (item.Parent is { } owner) { Interlocked.Add(ref owner.WindowBytes, take); Interlocked.Add(ref owner.StreamBytes, take); }
                    progressed = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (DownloadHttpException e) when (!Retryable(e.Status)) { throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or DownloadHttpException or TimeoutException)
            {
                if (progressed) failures = 0;
                if (++failures > MaxFailures) throw;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(8000, 400 * Math.Pow(2, failures - 1))), ct);
            }
            finally
            {
                lock (item.Lock) item.ActiveConnections--;
            }
        }
    }

    /// <summary>One connection from start to finish, for servers that cannot send parts of a file.</summary>
    private async Task StreamAsync(DownloadItem item, SafeFileHandle file, CancellationToken ct)
    {
        var buffer = new byte[ChunkSize];
        for (var attempt = 1; ; attempt++)
        {
            Interlocked.Exchange(ref item.StreamBytes, 0);
            lock (item.Lock) { item.Segments.Clear(); item.ActiveConnections = 1; }
            try
            {
                using var req = NewRequest(item);
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!res.IsSuccessStatusCode) throw new DownloadHttpException(res.StatusCode);
                if (item.Total < 0) item.Total = res.Content.Headers.ContentLength ?? -1;
                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                long at = 0;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    await _limiter.WaitAsync(read, ct);
                    await RandomAccess.WriteAsync(file, buffer.AsMemory(0, read), at, ct);
                    at += read;
                    Interlocked.Add(ref item.StreamBytes, read);
                    Interlocked.Add(ref item.WindowBytes, read);
                    if (item.Parent is { } owner) { Interlocked.Add(ref owner.WindowBytes, read); Interlocked.Add(ref owner.StreamBytes, read); }
                }
                if (item.Total > 0 && at != item.Total) throw new IOException("The connection closed before the whole file arrived.");
                if (item.Total < 0) item.Total = at;
                RandomAccess.SetLength(file, at);
                return;
            }
            catch (Exception e) when (e is HttpRequestException or IOException && attempt < 3 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
            finally
            {
                lock (item.Lock) item.ActiveConnections = 0;
            }
        }
    }

    private static bool Retryable(HttpStatusCode s) => s is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)s >= 500;

    private static bool IsTransient(Exception e)
    {
        switch (e)
        {
            case DownloadHttpException dhe: return Retryable(dhe.Status);
            case HttpRequestException or HttpIOException or TimeoutException or System.Net.Sockets.SocketException: return true;
            case IOException io when io.InnerException is null: return false; // the disk, not the network
        }
        return e.InnerException is not null && IsTransient(e.InnerException);
    }

    // ---- Step 3: finish ----

    private void Finish(DownloadItem item)
    {
        var target = item.FinalPath;
        if (File.Exists(target))
        {
            item.FileName = FileNaming.Unique(item.Folder, item.FileName, _ => false);
            target = item.FinalPath;
        }
        File.Move(item.PartPath, target);
        Mark(target, item.Url, item.Referer);
        item.State = DownloadState.Completed;
        item.CompletedAt = DateTime.Now;
        item.Speed = 0;
        item.Error = null;
        lock (item.Lock) item.ActiveConnections = 0;
        Changed(item, save: true);
    }

    private void DiscardPartial(DownloadItem item)
    {
        lock (item.Lock) item.Segments.Clear();
        Interlocked.Exchange(ref item.StreamBytes, 0);
        if (item.FileName.Length == 0) return;
        DiscardStreamParts(item);
        foreach (var child in item.Children.ToList()) DiscardPartial(child);
        item.Children.Clear();
        item.StreamStage = 0; item.Done0 = item.Done1 = 0; item.Bytes0 = item.Bytes1 = 0;
        DeleteSoon(item.PartPath);
    }

    /// <summary>
    /// Removes a half-finished file. Something may still hold it for a moment (the writer closing, a virus scanner looking at it),
    /// so a failed attempt is retried a few times instead of leaving the file behind.
    /// </summary>
    internal static void DeleteSoon(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* held: try again in the background, never making the window wait */ }
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(100);
                try { if (File.Exists(path)) File.Delete(path); return; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* still held */ }
            }
        });
    }

    private string Reserve(string folder, string name) =>
        FileNaming.Unique(folder, name, n => { lock (_gate) return _items.Any(i => i.Folder == folder && string.Equals(i.FileName, n, StringComparison.OrdinalIgnoreCase) && i.State is not (DownloadState.Cancelled or DownloadState.Failed)); });

    private static string Friendly(Exception e) => e switch
    {
        DownloadHttpException { Status: HttpStatusCode.NotFound or HttpStatusCode.Gone } => "The server no longer has this file.",
        DownloadHttpException { Status: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } => "The server refused the download. Open the page again and retry.",
        DownloadHttpException x => $"The server answered {(int)x.Status}.",
        DrmProtectedException d => d.Message,
        HttpRequestException => "Could not reach the server. Check the connection and retry.",
        IOException io when io.Message.Contains("disk", StringComparison.OrdinalIgnoreCase) || io.HResult == unchecked((int)0x80070070) => "The disk is full.",
        UnauthorizedAccessException => "No permission to write to the download folder.",
        _ => e.Message,
    };

    // ---- Speed ----

    private void Sample()
    {
        if (_disposed) return;
        foreach (var item in Items.Where(i => i.State == DownloadState.Downloading))
        {
            var now = DateTime.UtcNow;
            var bytes = Interlocked.Exchange(ref item.WindowBytes, 0);
            var seconds = Math.Max(0.05, (now - item.SampleAt).TotalSeconds);
            item.SampleAt = now;
            if (item.Children.Count > 0) lock (item.Lock) item.ActiveConnections = item.Children.Sum(c => c.ActiveConnections);
            var instant = bytes / seconds;
            item.Speed = item.Speed <= 0 ? instant : item.Speed * 0.6 + instant * 0.4;
            Changed(item, save: (now - item.LastSaved).TotalSeconds >= 2);
            if ((now - item.LastSaved).TotalSeconds >= 2) item.LastSaved = now;
        }
    }

    // ---- Saving, so downloads survive a restart ----

    private sealed record SegmentRecord(long Start, long End, long Done);

    private sealed record Record(string Id, string Url, string? Referer, string? UserAgent, string? Cookie, string Folder, string FileName,
        string? ETag, string? LastModified, string? ContentType, long Total, bool CanResume, int Connections, DownloadState State, string? Error,
        DateTime CreatedAt, DateTime? CompletedAt, List<SegmentRecord> Segments,
        PairSpec? Pair = null, StreamSpec? Stream = null, int StreamStage = 0, int Done0 = 0, long Bytes0 = 0, int Done1 = 0, long Bytes1 = 0, bool Fragmented0 = false, bool Fragmented1 = false, string? Ext0 = null, string? Ext1 = null,
        List<SegmentRecord>? Segments0 = null, List<SegmentRecord>? Segments1 = null);

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private string StatePath(DownloadItem item) => Path.Combine(_stateFolder!, item.Id + ".json");

    /// <summary>The "from the internet" mark on a saved file; a private window's file is marked without saying which site it came from.</summary>
    private void Mark(string path, string? url, string? referer) =>
        MarkOfTheWeb.Apply(path, IsPrivate ? null : url, IsPrivate ? null : referer);

    private void Save(DownloadItem item)
    {
        if (item.Parent is not null || _stateFolder is null) return;
        try
        {
            Record rec;
            lock (item.Lock)
                rec = new Record(item.Id, item.Url, item.Referer, item.UserAgent, item.Cookie, item.Folder, item.FileName, item.ETag, item.LastModified, item.ContentType,
                    item.Total, item.CanResume, item.Connections, item.State, item.Error, item.CreatedAt, item.CompletedAt,
                    item.Segments.Select(s => new SegmentRecord(s.Start, s.End, s.Done)).ToList(),
                    item.Pair, item.Stream, item.StreamStage, item.Done0, item.Bytes0, item.Done1, item.Bytes1, item.Fragmented0, item.Fragmented1, item.Ext0, item.Ext1,
                    item.Children.Count > 0 ? item.Children[0].Segments.Select(s => new SegmentRecord(s.Start, s.End, s.Done)).ToList() : null,
                    item.Children.Count > 1 ? item.Children[1].Segments.Select(s => new SegmentRecord(s.Start, s.End, s.Done)).ToList() : null);
            var path = StatePath(item);
            var tmp = path + ".tmp";
            lock (_stateGate) // one writer at a time per file keeps the rename safe
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(rec, Json));
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch { /* saving is best effort: a failed save must never stop a download */ }
    }

    private void Load()
    {
        if (_stateFolder is null) return;
        foreach (var path in Directory.EnumerateFiles(_stateFolder, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<Record>(File.ReadAllText(path), Json);
                if (r is null) continue;
                var item = new DownloadItem { Id = r.Id, Url = r.Url, Referer = r.Referer, UserAgent = r.UserAgent, Cookie = r.Cookie, CreatedAt = r.CreatedAt }
                    ;
                item.Folder = r.Folder; item.FileName = r.FileName; item.ETag = r.ETag; item.LastModified = r.LastModified; item.ContentType = r.ContentType;
                item.Total = r.Total; item.CanResume = r.CanResume; item.Connections = r.Connections; item.Error = r.Error; item.CompletedAt = r.CompletedAt;
                item.Pair = r.Pair; item.Stream = r.Stream; item.StreamStage = r.StreamStage; item.Done0 = r.Done0; item.Bytes0 = r.Bytes0; item.Done1 = r.Done1; item.Bytes1 = r.Bytes1;
                item.Fragmented0 = r.Fragmented0; item.Fragmented1 = r.Fragmented1; item.Ext0 = r.Ext0; item.Ext1 = r.Ext1;
                item.StreamBytes = r.Bytes0 + r.Bytes1;
                foreach (var s in r.Segments) item.Segments.Add(new Segment { Start = s.Start, End = s.End, Done = s.Done });
                if (r.Pair is { } pair)
                {
                    var stem = Path.GetFileNameWithoutExtension(r.FileName);
                    var c0 = new DownloadItem { Url = pair.VideoUrl, Folder = r.Folder, FileName = stem + ".video.mp4", Parent = item, Connections = r.Connections, Total = pair.VideoLength, UserAgent = r.UserAgent, Referer = r.Referer, Cookie = r.Cookie, CanResume = true };
                    var c1 = new DownloadItem { Url = pair.AudioUrl, Folder = r.Folder, FileName = stem + ".audio.m4a", Parent = item, Connections = r.Connections, Total = pair.AudioLength, UserAgent = r.UserAgent, Referer = r.Referer, Cookie = r.Cookie, CanResume = true };
                    if (r.Segments0 is not null) foreach (var s in r.Segments0) c0.Segments.Add(new Segment { Start = s.Start, End = s.End, Done = s.Done });
                    if (r.Segments1 is not null) foreach (var s in r.Segments1) c1.Segments.Add(new Segment { Start = s.Start, End = s.End, Done = s.Done });
                    item.Children.Add(c0);
                    item.Children.Add(c1);
                }
                // Whatever was running when the browser closed is simply paused.
                item.State = r.State is DownloadState.Downloading or DownloadState.Queued ? DownloadState.Paused : r.State;
                if (item.State == DownloadState.Completed && !File.Exists(item.FinalPath)) { item.State = DownloadState.Failed; item.Error = "The file is no longer where it was saved."; }
                _items.Add(item);
            }
            catch { /* a damaged record is skipped */ }
        }
        _items.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ticker.Dispose();
        var running = Items.Where(i => i.State == DownloadState.Downloading).ToList();
        foreach (var item in running)
        {
            item.Stop = StopReason.Shutdown;
            item.Cts?.Cancel();
        }
        try { Task.WaitAll(running.Select(i => i.Run ?? Task.CompletedTask).ToArray(), TimeSpan.FromSeconds(3)); } catch { /* shutting down anyway */ }
        // A private window's unfinished downloads cannot be resumed later (nothing remembers them): their pieces go too.
        if (IsPrivate) foreach (var item in Items.Where(i => i.State != DownloadState.Completed)) DiscardPartial(item);
        foreach (var item in Items) Save(item);
        _http.Dispose();
    }
}
