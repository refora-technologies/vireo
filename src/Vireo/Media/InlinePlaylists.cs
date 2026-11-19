using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Vireo.Media;

/// <summary>
/// Playlists that never cross the network: some players write the playlist in script and hand it to the video as a
/// <c>blob:</c> address, so only its pieces are ever downloaded. The page tells Vireo the text; it is kept here under an
/// address of its own, and <see cref="Handler"/> answers that address from memory for the probe and the downloader, which
/// then fetch the real pieces as usual. A live playlist rewritten every few seconds keeps one address and its newest text.
/// </summary>
public static class InlinePlaylists
{
    public const string Host = "inline.vireo.invalid";
    private const int MaxKept = 200;
    private const int MaxChars = 4 * 1024 * 1024;

    private sealed record Entry(string Text, MediaKind Kind, DateTime At);
    private static readonly ConcurrentDictionary<string, Entry> Entries = new();          // id -> playlist
    private static readonly ConcurrentDictionary<string, string> IdByShape = new();        // shape of a playlist -> id
    private static readonly ConcurrentDictionary<string, string> AddressByBlob = new();    // blob: address in the page -> our address

    public static bool IsInline(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host == Host;

    /// <summary>
    /// Keeps a playlist the page made, with its relative addresses resolved against the page (where a blob's addresses point)
    /// and any blob: addresses of playlists already kept replaced by ours. Returns the address to fetch it from, or null when
    /// the text is not a playlist.
    /// </summary>
    public static string? Add(string text, string pageUrl, string? blobUrl)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxChars) return null;
        text = text.TrimStart((char)0xFEFF,' ', '\r', '\n', '\t');
        var kind = MediaClassifier.SniffPlaylist(Encoding.UTF8.GetBytes(text.Length > 2048 ? text[..2048] : text));
        if (kind is null) return null;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)) return null;
        if (kind == MediaKind.Hls) text = AbsoluteHls(text, page);
        // A DASH description without a base address: its pieces are named relative to the page that carried it.
        else if (!text.Contains("<BaseURL", StringComparison.Ordinal))
        {
            var open = Regex.Match(text, @"<MPD\b[^>]*>");
            if (open.Success) text = text.Insert(open.Index + open.Length, $"<BaseURL>{System.Security.SecurityElement.Escape(new Uri(page, ".").ToString())}</BaseURL>");
        }

        var id = IdByShape.GetOrAdd(Shape(text, kind.Value), _ => Hash(text)[..16]);
        Entries[id] = new Entry(text, kind.Value, DateTime.UtcNow);
        var address = $"http://{Host}/{id}.{(kind == MediaKind.Hls ? "m3u8" : "mpd")}";
        if (blobUrl is not null && blobUrl.StartsWith("blob:", StringComparison.Ordinal))
        {
            AddressByBlob[blobUrl] = address;
            // A master playlist made before this one points at its blob: address; point it at ours instead.
            foreach (var (key, e) in Entries.ToArray())
                if (key != id && e.Text.Contains(blobUrl, StringComparison.Ordinal)) Entries[key] = e with { Text = e.Text.Replace(blobUrl, address) };
        }
        Trim();
        return address;
    }

    /// <summary>The text kept at one of our addresses (for tests and the handler).</summary>
    public static string? TextAt(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host == Host && Entries.TryGetValue(System.IO.Path.GetFileNameWithoutExtension(u.AbsolutePath), out var e) ? e.Text : null;

    /// <summary>Every address in an HLS playlist made absolute against the page, blob: ones swapped for ours when known.</summary>
    private static string AbsoluteHls(string text, Uri page)
    {
        string Fix(string raw)
        {
            raw = raw.Trim();
            if (raw.StartsWith("blob:", StringComparison.Ordinal)) return AddressByBlob.TryGetValue(raw, out var ours) ? ours : raw;
            return Uri.TryCreate(page, raw, out var abs) ? abs.ToString() : raw;
        }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            if (line[0] != '#') { lines[i] = Fix(line); continue; }
            if (line.Contains("URI=\"", StringComparison.Ordinal))
                lines[i] = Regex.Replace(line, "URI=\"([^\"]*)\"", m => $"URI=\"{Fix(m.Groups[1].Value)}\"");
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// What stays the same while a live playlist is rewritten: a master playlist is its whole text; a media playlist is where
    /// its pieces live (the folder of its first piece), not which pieces it lists right now.
    /// </summary>
    private static string Shape(string text, MediaKind kind)
    {
        if (kind == MediaKind.Dash || text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal)) return Hash(text);
        var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l[0] != '#');
        if (first is null || !Uri.TryCreate(first, UriKind.Absolute, out var u)) return Hash(text);
        return "media|" + u.GetLeftPart(UriPartial.Authority) + u.AbsolutePath[..(u.AbsolutePath.LastIndexOf('/') + 1)];
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void Trim()
    {
        if (Entries.Count <= MaxKept) return;
        foreach (var old in Entries.OrderBy(e => e.Value.At).Take(Entries.Count - MaxKept).ToList()) Entries.TryRemove(old.Key, out _);
    }

    // ---- Playlists the page already received ----

    private sealed record Seen(string Text, string Type, DateTime At);
    private static readonly ConcurrentDictionary<string, Seen> Received = new(StringComparer.Ordinal);
    private const int MaxReceived = 120;
    private const int MaxReceivedChars = 1024 * 1024;

    /// <summary>
    /// Keeps a playlist exactly as the page's player received it. Some addresses answer only once (a single-use token, an ad
    /// service's session): asking again gets "204 No Content" or an error page. The handler falls back to this copy then.
    /// </summary>
    public static void Remember(string url, string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxReceivedChars) return;
        var kind = MediaClassifier.SniffPlaylist(Encoding.UTF8.GetBytes(text.Length > 2048 ? text[..2048] : text));
        if (kind is null) return;
        Received[url] = new Seen(text, kind == MediaKind.Hls ? "application/vnd.apple.mpegurl" : "application/dash+xml", DateTime.UtcNow);
        if (Received.Count > MaxReceived)
            foreach (var old in Received.OrderBy(e => e.Value.At).Take(Received.Count - MaxReceived).ToList()) Received.TryRemove(old.Key, out _);
    }

    public static bool WasReceived(string url) => Received.ContainsKey(url);

    /// <summary>Answers our own addresses from memory, and a playlist that will not answer twice from the copy the page got.</summary>
    public sealed class Handler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.Host == Host) return await Inline(request);
            var url = request.RequestUri?.ToString() ?? "";
            if (!Received.TryGetValue(url, out var seen)) return await base.SendAsync(request, ct);
            // A fresh copy is better (a live playlist grows); the page's copy when the address will not answer again.
            HttpResponseMessage? fresh = null;
            try
            {
                fresh = await base.SendAsync(request, ct);
                if (fresh.IsSuccessStatusCode && fresh.StatusCode != HttpStatusCode.NoContent)
                {
                    var text = await fresh.Content.ReadAsStringAsync(ct);
                    if (MediaClassifier.SniffPlaylist(Encoding.UTF8.GetBytes(text.Length > 2048 ? text[..2048] : text)) is not null)
                    {
                        var copy = new HttpResponseMessage(fresh.StatusCode) { RequestMessage = fresh.RequestMessage, Content = new StringContent(text, Encoding.UTF8, seen.Type) };
                        fresh.Dispose();
                        return copy;
                    }
                }
            }
            catch (HttpRequestException) { /* fall back to the copy */ }
            fresh?.Dispose();
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(seen.Text, Encoding.UTF8, seen.Type) };
        }

        private static Task<HttpResponseMessage> Inline(HttpRequestMessage request)
        {
            var id = System.IO.Path.GetFileNameWithoutExtension(request.RequestUri?.AbsolutePath ?? "");
            if (!Entries.TryGetValue(id, out var entry)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            var type = entry.Kind == MediaKind.Hls ? "application/vnd.apple.mpegurl" : "application/dash+xml";
            var res = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(entry.Text, Encoding.UTF8, type) };
            return Task.FromResult(res);
        }
    }
}
