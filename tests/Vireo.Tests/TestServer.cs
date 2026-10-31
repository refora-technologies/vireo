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
            if (path == "/missing") { res.StatusCode = 404; return; }
            if (path == "/denied") { res.StatusCode = 403; return; }
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
            var slow = ChunkDelayMs > 0 && (SlowWhenRangeStarts?.Invoke(partial ? start : 0) ?? true);
            var sent = 0L;
            var stream = res.OutputStream;
            while (sent < length)
            {
                var n = (int)Math.Min(16 * 1024, length - sent);
                await stream.WriteAsync(Data.AsMemory((int)(start + sent), n));
                sent += n;
                if (drop && sent >= DropAfterBytes) { res.Abort(); return; }
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

    public void Dispose() => _listener.Close();
}
