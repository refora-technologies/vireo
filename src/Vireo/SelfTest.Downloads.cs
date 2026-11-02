using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Vireo.Downloads;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>A local site with a signed-in session: the file is only served to requests that carry the cookie and the page's address.</summary>
    private sealed partial class Site : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _concurrent;
        public byte[] Data { get; } = new byte[24 * 1024 * 1024];
        public string Base { get; }
        public int MaxConcurrent { get; private set; }
        public int FileRequests { get; private set; }
        public int WithoutLogin { get; private set; }
        public int Refused { get; private set; }

        public Site()
        {
            new Random(7).NextBytes(Data);
            for (var port = 30000 + Random.Shared.Next(20000); ; port++)
            {
                try
                {
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    Base = $"http://127.0.0.1:{port}";
                    break;
                }
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

        [GeneratedRegex(@"bytes=(\d+)-(\d*)")]
        private static partial Regex Range();

        private async Task Handle(HttpListenerContext c)
        {
            try
            {
                var res = c.Response;
                if (c.Request.Url!.AbsolutePath == "/page.html")
                {
                    res.AddHeader("Set-Cookie", "session=abc123; Path=/");
                    var html = "<!doctype html><title>Reports</title><h1>Reports</h1><a id='dl' href='/dl/quarterly report.bin'>Download</a>"u8.ToArray();
                    res.ContentType = "text/html";
                    res.ContentLength64 = html.Length;
                    await res.OutputStream.WriteAsync(html);
                    return;
                }
                if (!c.Request.Url.AbsolutePath.StartsWith("/dl/")) { res.StatusCode = 404; return; }

                FileRequests++;
                var cookie = c.Request.Headers["Cookie"] ?? "";
                var referer = c.Request.Headers["Referer"] ?? "";
                if (!cookie.Contains("session=abc123")) WithoutLogin++;
                if (!cookie.Contains("session=abc123") || !referer.EndsWith("/page.html")) { Refused++; res.StatusCode = 403; return; }

                var now = Interlocked.Increment(ref _concurrent);
                MaxConcurrent = Math.Max(MaxConcurrent, now);
                try
                {
                    long start = 0, end = Data.Length - 1;
                    var m = Range().Match(c.Request.Headers["Range"] ?? "");
                    if (m.Success)
                    {
                        start = long.Parse(m.Groups[1].Value);
                        if (m.Groups[2].Length > 0) end = Math.Min(end, long.Parse(m.Groups[2].Value));
                        res.StatusCode = 206;
                        res.AddHeader("Content-Range", $"bytes {start}-{end}/{Data.Length}");
                    }
                    res.AddHeader("Content-Disposition", "attachment; filename=\"quarterly report.bin\"");
                    res.ContentType = "application/octet-stream";
                    res.ContentLength64 = end - start + 1;
                    for (var sent = 0L; sent < end - start + 1; sent += 16384)
                    {
                        await res.OutputStream.WriteAsync(Data.AsMemory((int)(start + sent), (int)Math.Min(16384, end - start + 1 - sent)));
                        await Task.Delay(10);
                    }
                }
                finally { Interlocked.Decrement(ref _concurrent); }
            }
            catch { /* the browser went away */ }
            finally { try { c.Response.Close(); } catch { /* closed */ } }
        }

        public void Dispose() => _listener.Close();
    }

    /// <summary>A page starts a download: the engine takes it over, with the page's cookie, over several connections.</summary>
    private async Task RunDownloadStepsAsync()
    {
        using var site = new Site();
        var folder = Path.Combine(outDir, "downloaded");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        w.Settings.DownloadFolder = folder;
        w.Settings.DownloadConnections = 8;
        w.Settings.Commit();

        var tab = await w.OpenTabAsync(site.Base + "/page.html");
        await WaitFor(() => !tab.IsLoading && tab.Title == "Reports");
        Check("a signed-in page opens", tab.Title == "Reports", tab.Title);

        w.ShowDownloads();
        await Task.Delay(500);
        await w.RunScriptAsync(tab, "document.getElementById('dl').click()");
        await WaitFor(() => w.Downloads.Items.Count == 1, 8000);
        var item = w.Downloads.Items.FirstOrDefault();
        Check("the browser hands the download to Vireo's engine", item is not null && Uri.UnescapeDataString(item.Url).EndsWith("quarterly report.bin"), item?.Url ?? "none");
        if (item is null) return;

        await Task.Delay(900);
        Check("it is already running over several connections", item.ActiveConnections > 1 && item.Downloaded > 0, $"{item.ActiveConnections} connections, {item.Downloaded / 1024} KB");
        Check("the downloads button shows it", w.DownloadBadgeShown, w.DownloadBadgeText);
        await Shot("13-downloading");

        await WaitFor(() => item.State is DownloadState.Completed or DownloadState.Failed, 40000);
        Check("it finishes", item.State == DownloadState.Completed, $"{item.State} {item.Error}");
        var path = Path.Combine(folder, "quarterly report.bin");
        Check("saved under the name the server gave", File.Exists(path), item.FileName);
        Check("byte for byte the same file", File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == Convert.ToHexString(SHA256.HashData(site.Data)));
        Check("several connections ran at once", site.MaxConcurrent >= 3, $"{site.MaxConcurrent} at once");
        Check("every request carried the page's login", site.WithoutLogin == 0 && site.Refused == 0, $"{site.FileRequests} requests, {site.Refused} refused");
        Check("the browser did not also save it itself", Directory.GetFiles(folder).Length == 1, string.Join(", ", Directory.GetFiles(folder).Select(Path.GetFileName)));
        await Task.Delay(600);
        await Shot("14-download-done");
        w.CloseDrawer();
        await Task.Delay(400);
    }
}
