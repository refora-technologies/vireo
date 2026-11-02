using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vireo.Media;

public enum YtStatus { Ok, Unavailable, LoginRequired, Protected, LiveOffline }

public sealed record YtFormat(int Itag, string Mime, int Width, int Height, int Fps, long Bitrate, long Length, string Url, string Label, string Codec);

public sealed record YtInfo(string VideoId, string Title, string? Author, double DurationSec, string? Thumbnail, bool IsLive, IReadOnlyList<YtFormat> Video, YtFormat? Audio, YtStatus Status, string? Reason);

/// <summary>
/// Finds a YouTube video's streams the way the YouTube apps ask for them. Asks as the iPhone and Vision Pro apps (their
/// addresses serve whole files), retries with a visitor session when YouTube wants one, and checks that the addresses
/// really deliver the end of the file before offering them.
/// </summary>
public static partial class YouTube
{
    private sealed record Client(string Name, string Id, object Context, string Agent);

    private static readonly Client[] Clients =
    [
        new("IOS", "5", new { clientName = "IOS", clientVersion = "20.11.6", deviceMake = "Apple", deviceModel = "iPhone10,4", osName = "iOS", osVersion = "16.7.7.20H330", platform = "MOBILE" },
            "com.google.ios.youtube/20.11.6 (iPhone10,4; U; CPU iOS 16_7_7 like Mac OS X)"),
        new("VISIONOS", "101", new { clientName = "VISIONOS", clientVersion = "1.02", deviceMake = "Apple", deviceModel = "RealityDevice17,1", osName = "visionOS", osVersion = "26.5.23O471", platform = "MOBILE" },
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15"),
        new("ANDROID_VR", "28", new { clientName = "ANDROID_VR", clientVersion = "1.65.10", deviceMake = "Oculus", deviceModel = "Quest 3", osName = "Android", osVersion = "12L", platform = "MOBILE", clientFormFactor = "SMALL_FORM_FACTOR" },
            "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip"),
    ];

    [GeneratedRegex("^[a-zA-Z0-9_-]{11}$")]
    private static partial Regex IdPattern();

    /// <summary>The 11-character id in a watch, short, shorts, embed or live address.</summary>
    public static string? VideoId(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        if (host == "youtu.be")
        {
            var id = u.AbsolutePath.Trim('/').Split('/')[0];
            return IdPattern().IsMatch(id) ? id : null;
        }
        if (host != "youtube.com" && !host.EndsWith(".youtube.com")) return null;
        var v = System.Web.HttpUtility.ParseQueryString(u.Query)["v"];
        if (v is not null && IdPattern().IsMatch(v)) return v;
        var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] is "shorts" or "embed" or "v" or "live" && IdPattern().IsMatch(parts[1]) ? parts[1] : null;
    }

    private static async Task<JsonDocument> AskAsync(HttpClient http, Client c, string videoId, string? visitor, CancellationToken ct)
    {
        var context = JsonSerializer.SerializeToNode(c.Context)!.AsObject();
        context["hl"] = "en";
        context["gl"] = "US";
        if (visitor is not null) context["visitorData"] = visitor;
        var body = new { context = new { client = context }, videoId, contentCheckOk = true, racyCheckOk = true };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("X-Youtube-Client-Name", c.Id);
        req.Headers.TryAddWithoutValidation("X-Youtube-Client-Version", ((dynamic)c.Context).clientVersion);
        req.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
        req.Headers.TryAddWithoutValidation("User-Agent", c.Agent);
        if (visitor is not null) req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    private static bool Playable(JsonElement root) =>
        root.TryGetProperty("playabilityStatus", out var p) && p.GetProperty("status").GetString() == "OK"
        && root.TryGetProperty("streamingData", out var s) && (s.TryGetProperty("adaptiveFormats", out var a) && a.GetArrayLength() > 0 || s.TryGetProperty("hlsManifestUrl", out _));

    /// <summary>Whether the addresses serve the end of the biggest stream. Some answer "playable" but refuse everything past the first part.</summary>
    private static async Task<bool> ServesWholeFileAsync(HttpClient http, JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("streamingData", out var s) || !s.TryGetProperty("adaptiveFormats", out var formats)) return true;
        var biggest = formats.EnumerateArray().Where(f => f.TryGetProperty("url", out _) && Length(f) > 40 * 1024 * 1024).OrderByDescending(Length).FirstOrDefault();
        if (biggest.ValueKind != JsonValueKind.Object) return true; // small files fit in the part every client serves
        var start = (long)(Length(biggest) * 0.8);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, biggest.GetProperty("url").GetString());
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, start + 1023);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return (int)res.StatusCode != 403;
        }
        catch (HttpRequestException) { return true; } // a network hiccup says nothing about the address
    }

    private static long Length(JsonElement f) => f.TryGetProperty("contentLength", out var l) && long.TryParse(l.GetString(), out var n) ? n : 0;

    public static async Task<YtInfo> ResolveAsync(string videoId, HttpClient http, CancellationToken ct = default)
    {
        string? visitor = null;
        JsonDocument? best = null;
        foreach (var client in Clients)
        {
            try
            {
                var doc = await AskAsync(http, client, videoId, visitor, ct);
                var offered = doc.RootElement.TryGetProperty("responseContext", out var rc) && rc.TryGetProperty("visitorData", out var vd) ? vd.GetString() : null;
                var usable = Playable(doc.RootElement) && await ServesWholeFileAsync(http, doc.RootElement, ct);
                // Not usable yet and YouTube gave us a visitor id we did not send: ask once more with it, as the apps do.
                if (!usable && offered is not null && offered != visitor)
                {
                    visitor = offered;
                    doc.Dispose();
                    doc = await AskAsync(http, client, videoId, visitor, ct);
                    usable = Playable(doc.RootElement) && await ServesWholeFileAsync(http, doc.RootElement, ct);
                }
                if (usable) return Parse(doc);
                var status = Status(doc.RootElement, out _);
                best?.Dispose();
                best = doc;
                if (status is YtStatus.Protected or YtStatus.LiveOffline) break; // another client will not change that
            }
            catch (HttpRequestException) { /* try the next client */ }
        }
        if (best is not null) return Parse(best);
        return new YtInfo(videoId, "", null, 0, null, false, [], null, YtStatus.Unavailable, "Could not reach YouTube.");
    }

    private static YtStatus Status(JsonElement root, out string? reason)
    {
        reason = null;
        if (!root.TryGetProperty("playabilityStatus", out var p)) return YtStatus.Unavailable;
        var status = p.GetProperty("status").GetString();
        reason = p.TryGetProperty("reason", out var r) ? r.GetString() : null;
        if (status == "OK") return Playable(root) ? YtStatus.Ok : YtStatus.Unavailable;
        if (status == "LIVE_STREAM_OFFLINE") return YtStatus.LiveOffline;
        if (Regex.IsMatch(reason ?? "", "drm|protected|rental|purchase", RegexOptions.IgnoreCase)) return YtStatus.Protected;
        return status == "LOGIN_REQUIRED" ? YtStatus.LoginRequired : YtStatus.Unavailable;
    }

    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "";

    public static YtInfo Parse(JsonDocument doc)
    {
        var root = doc.RootElement;
        var status = Status(root, out var reason);
        var details = root.TryGetProperty("videoDetails", out var d) ? d : default;
        string title = details.ValueKind == JsonValueKind.Object ? Text(details, "title") : "", id = details.ValueKind == JsonValueKind.Object ? Text(details, "videoId") : "";
        double.TryParse(details.ValueKind == JsonValueKind.Object ? Text(details, "lengthSeconds") : "", System.Globalization.CultureInfo.InvariantCulture, out var seconds);
        string? thumb = null;
        if (details.ValueKind == JsonValueKind.Object && details.TryGetProperty("thumbnail", out var t) && t.TryGetProperty("thumbnails", out var ts))
            thumb = ts.EnumerateArray().OrderByDescending(x => x.TryGetProperty("width", out var w) ? w.GetInt32() : 0).Select(x => x.GetProperty("url").GetString()).FirstOrDefault();
        var live = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("isLive", out var il) && il.ValueKind == JsonValueKind.True;
        var author = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("author", out var au) ? au.GetString() : null;
        if (status != YtStatus.Ok) return new YtInfo(id, title, author, seconds, thumb, live, [], null, status, reason);

        var formats = root.GetProperty("streamingData").GetProperty("adaptiveFormats").EnumerateArray().Where(f => f.TryGetProperty("url", out _) && Length(f) > 0).ToList();

        // Audio: the original language (videos with dubbing list one track per language), AAC, best bitrate.
        var audio = formats.Where(f => Text(f, "mimeType").StartsWith("audio/mp4"))
            .OrderByDescending(f => f.TryGetProperty("audioTrack", out var at) && at.TryGetProperty("audioIsDefault", out var def) && def.ValueKind == JsonValueKind.True)
            .ThenByDescending(f => f.TryGetProperty("bitrate", out var b) ? b.GetInt64() : 0).FirstOrDefault();

        // Video: MP4 only (they can be joined with the audio without re-encoding), one per height, H.264 first.
        var video = formats.Where(f => Text(f, "mimeType").StartsWith("video/mp4") && f.TryGetProperty("height", out _))
            .GroupBy(f => f.GetProperty("height").GetInt32())
            .Select(g => g.OrderByDescending(f => Text(f, "mimeType").Contains("avc1")).ThenByDescending(f => f.TryGetProperty("bitrate", out var b) ? b.GetInt64() : 0).First())
            .OrderByDescending(f => f.GetProperty("height").GetInt32())
            .Select(f => ToFormat(f)).ToList();

        return new YtInfo(id, title, author, seconds, thumb, live, video, audio.ValueKind == JsonValueKind.Object ? ToFormat(audio) : null, YtStatus.Ok, null);
    }

    private static YtFormat ToFormat(JsonElement f)
    {
        var mime = Text(f, "mimeType");
        var codec = Regex.Match(mime, "codecs=\"([^\"]+)\"").Groups[1].Value;
        var h = f.TryGetProperty("height", out var hh) ? hh.GetInt32() : 0;
        var label = f.TryGetProperty("qualityLabel", out var q) && q.GetString() is { Length: > 0 } ql ? ql : h > 0 ? $"{h}p" : "Audio";
        return new YtFormat(f.GetProperty("itag").GetInt32(), mime, f.TryGetProperty("width", out var w) ? w.GetInt32() : 0, h, f.TryGetProperty("fps", out var fps) ? fps.GetInt32() : 0,
            f.TryGetProperty("bitrate", out var b) ? b.GetInt64() : 0, Length(f), f.GetProperty("url").GetString()!, label, codec);
    }

    public static string CodecName(string codec) => codec.StartsWith("avc1") ? "H.264" : codec.StartsWith("av01") ? "AV1" : codec.StartsWith("vp9") ? "VP9" : codec.StartsWith("hev") || codec.StartsWith("hvc") ? "HEVC" : "";
}
