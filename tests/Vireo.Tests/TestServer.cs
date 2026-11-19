using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace Vireo.Tests;

/// <summary>A small web server that behaves like the awkward servers real downloads meet.</summary>
public sealed partial class TestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private int _concurrent;
    private int _dropped;

    public byte[] Data { get; private set; }
    public string BaseUrl { get; }
    public bool AcceptRanges { get; set; } = true;
    /// <summary>Pause after each 16 KB sent, so downloads take a visible time. Applies to requests for which the callback says yes.</summary>
    public int ChunkDelayMs { get; set; }
    public Func<long, bool>? SlowWhenRangeStarts { get; set; }
    /// <summary>Cut the first N responses short, after this many bytes, like a connection dropping.</summary>
    public int DropFirst { get; set; }
    public int DropAfterBytes { get; set; } = 100_000;
    public int MaxConcurrent { get; private set; }
    public List<string> Requests { get; } = [];
    /// <summary>The first N requests are answered "503 service unavailable", like a server having a moment.</summary>
    public int Unavailable503First { get; set; }
    private int _unavailable;
    /// <summary>The first N responses go silent after this many bytes (0: before even answering), and stay open, like a
    /// connection a server has quietly abandoned.</summary>
    public int HangFirst { get; set; }
    public int HangAfterBytes { get; set; } = 100_000;
    private int _hung;
    /// <summary>Until this time every request is cut off at once, like the network going away.</summary>
    public DateTime OutageUntil { get; set; }
    /// <summary>More connections at once than this are answered "429 too many requests", like a free file host.</summary>
    public int AllowedAtOnce { get; set; }
    /// <summary>More connections at once than this get pixeldrain's "max_concurrent_downloads" refusal (403 with JSON).</summary>
    public int HostAllowsAtOnce { get; set; }
    public int TooManyAnswers;
    private readonly CancellationTokenSource _closing = new();
    /// <summary>The headers of every request, so tests can see what the downloader claimed.</summary>
    public List<System.Collections.Specialized.NameValueCollection> Headers { get; } = [];

    /// <summary>Serves exactly these bytes (for example a real video file).</summary>
    public TestServer(byte[] data) : this(1) { Data = data; }

    public TestServer(int size = 6 * 1024 * 1024)
    {
        Data = new byte[size];
        new Random(42).NextBytes(Data);
        for (var port = 20000 + Random.Shared.Next(20000); ; port++)
        {
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                BaseUrl = $"http://127.0.0.1:{port}";
                break;
            }
            catch (HttpListenerException) { /* port taken; try the next */ }
        }
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    [GeneratedRegex(@"bytes=(\d+)-(\d*)")]
    private static partial Regex RangeHeader();

    private async Task Handle(HttpListenerContext ctx)
    {
        var now = Interlocked.Increment(ref _concurrent);
        lock (Requests) { MaxConcurrent = Math.Max(MaxConcurrent, now); Requests.Add($"{ctx.Request.Url!.AbsolutePath} {ctx.Request.Headers["Range"]}"); Headers.Add(new System.Collections.Specialized.NameValueCollection(ctx.Request.Headers)); }
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            var res = ctx.Response;
            if (DateTime.UtcNow < OutageUntil) { res.Abort(); return; }
            if (AllowedAtOnce > 0 && now > AllowedAtOnce) { Interlocked.Increment(ref TooManyAnswers); res.StatusCode = 429; return; }
            if (path == "/missing") { res.StatusCode = 404; return; }
            if (path == "/denied") { res.StatusCode = 403; return; }
            // A file host explaining a refusal, the way pixeldrain does (https://pixeldrain.com/api).
            if (path.StartsWith("/host-refuses/", StringComparison.Ordinal))
            {
                res.StatusCode = path.EndsWith("/bandwidth") ? 509 : 403;
                res.ContentType = "application/json";
                var body = System.Text.Encoding.UTF8.GetBytes($"{{\"success\":false,\"value\":\"{path["/host-refuses/".Length..]}\",\"message\":\"Refused.\"}}");
                await res.OutputStream.WriteAsync(body);
                return;
            }
            // A free file host that allows this many connections at once, saying so in words above that.
            if (HostAllowsAtOnce > 0 && now > HostAllowsAtOnce)
            {
                Interlocked.Increment(ref TooManyAnswers);
                res.StatusCode = 403;
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync("{\"success\":false,\"value\":\"max_concurrent_downloads\"}"u8.ToArray());
                return;
            }
            if (path == "/unavailable" || Interlocked.Increment(ref _unavailable) <= Unavailable503First) { res.StatusCode = 503; return; }
            if (path == "/needs-referer" && string.IsNullOrEmpty(ctx.Request.Headers["Referer"])) { res.StatusCode = 403; return; }
            if (path == "/refuses-referer" && !string.IsNullOrEmpty(ctx.Request.Headers["Referer"])) { res.StatusCode = 403; return; }
            if (path == "/named") res.AddHeader("Content-Disposition", "attachment; filename*=UTF-8''report%20final.pdf");

            long start = 0, end = Data.Length - 1;
            var partial = false;
            var m = RangeHeader().Match(ctx.Request.Headers["Range"] ?? "");
            if (AcceptRanges && m.Success)
            {
                start = long.Parse(m.Groups[1].Value);
                if (m.Groups[2].Length > 0) end = Math.Min(end, long.Parse(m.Groups[2].Value));
                partial = true;
                res.StatusCode = 206;
                res.AddHeader("Content-Range", $"bytes {start}-{end}/{Data.Length}");
            }
            res.AddHeader("Accept-Ranges", AcceptRanges ? "bytes" : "none");
            var length = end - start + 1;
            res.ContentLength64 = length;
            res.SendChunked = false;

            var drop = DropFirst > 0 && length > DropAfterBytes && Interlocked.Increment(ref _dropped) <= DropFirst;
            var hang = HangFirst > 0 && length > HangAfterBytes && Interlocked.Increment(ref _hung) <= HangFirst;
            if (hang && HangAfterBytes == 0) { await Task.Delay(Timeout.Infinite, _closing.Token); return; }
            var slow = ChunkDelayMs > 0 && (SlowWhenRangeStarts?.Invoke(partial ? start : 0) ?? true);
            var sent = 0L;
            var stream = res.OutputStream;
            while (sent < length)
            {
                var n = (int)Math.Min(16 * 1024, length - sent);
                await stream.WriteAsync(Data.AsMemory((int)(start + sent), n));
                sent += n;
                if (drop && sent >= DropAfterBytes) { res.Abort(); return; }
                if (hang && sent >= HangAfterBytes) { await stream.FlushAsync(); await Task.Delay(Timeout.Infinite, _closing.Token); return; }
                if (slow) await Task.Delay(ChunkDelayMs);
            }
            stream.Close();
        }
        catch { /* the client went away */ }
        finally
        {
            try { ctx.Response.Close(); } catch { /* already closed */ }
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public void Dispose()
    {
        _closing.Cancel();
        _listener.Close();
    }
}
