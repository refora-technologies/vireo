using System.IO;

namespace Vireo.Media;

public enum MediaKind { File, Hls, Dash, YouTube }

public sealed record Classified(MediaKind Kind, string? Mime, string Container, long? Size);

/// <summary>Decides whether a network response is media worth offering, and what kind.</summary>
public static class MediaClassifier
{
    /// <summary>Files smaller than this are almost always beacons, previews or ad pixels.</summary>
    public const long MinFileBytes = 150 * 1024;
    /// <summary>Real video clips can be small; a sound that small is a click or a notification, not something to save.</summary>
    public const long MinVideoBytes = 64 * 1024;

    private static readonly string[] HlsTypes = ["application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl", "application/m3u8", "video/m3u8", "text/x-m3u8"];
    private static readonly string[] DashTypes = ["application/dash+xml", "video/vnd.mpeg.dash.mpd", "audio/vnd.mpeg.dash.mpd"];
    private static readonly string[] SegmentTypes = ["video/mp2t", "video/iso.segment", "audio/iso.segment", "application/octet-stream-segment"];
    private static readonly string[] GenericTypes = ["application/octet-stream", "binary/octet-stream", "text/plain", "application/binary", ""];
    /// <summary>Some sites serve a playlist under a picture's or a page's type to put off downloaders; the address still gives it away.</summary>
    private static readonly string[] DisguisedTypes = [.. GenericTypes, "image/jpeg", "image/png", "image/gif", "text/html"];

    private static readonly Dictionary<string, string> FileExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp4"] = "mp4", ["m4v"] = "mp4", ["mov"] = "mov", ["webm"] = "webm", ["mkv"] = "mkv", ["ogv"] = "ogv", ["flv"] = "flv", ["avi"] = "avi",
        ["wmv"] = "wmv", ["3gp"] = "3gp", ["f4v"] = "mp4", ["ts"] = "ts", ["asf"] = "asf", ["m2ts"] = "m2ts", ["vob"] = "vob",
        ["mp3"] = "mp3", ["m4a"] = "m4a", ["m4b"] = "m4a", ["aac"] = "aac", ["ogg"] = "ogg", ["oga"] = "ogg", ["opus"] = "opus", ["flac"] = "flac", ["wav"] = "wav",
        ["wma"] = "wma", ["alac"] = "alac", ["aif"] = "aiff", ["aiff"] = "aiff", ["mid"] = "mid", ["midi"] = "mid", ["weba"] = "weba",
    };

    private static readonly HashSet<string> SegmentExt = new(StringComparer.OrdinalIgnoreCase) { "ts", "m4s", "cmfv", "cmfa", "m4f", "fmp4", "aac_seg", "vtt", "key" };

    private static readonly Dictionary<string, string> MimeContainer = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = "mp4", ["video/webm"] = "webm", ["video/quicktime"] = "mov", ["video/x-matroska"] = "mkv", ["video/ogg"] = "ogv", ["video/x-flv"] = "flv",
        ["video/x-msvideo"] = "avi", ["video/3gpp"] = "3gp", ["video/x-ms-wmv"] = "wmv", ["video/x-ms-asf"] = "asf",
        ["audio/mpeg"] = "mp3", ["audio/mp3"] = "mp3", ["audio/mp4"] = "m4a", ["audio/x-m4a"] = "m4a",
        ["audio/aac"] = "aac", ["audio/ogg"] = "ogg", ["audio/opus"] = "opus", ["audio/webm"] = "weba", ["audio/flac"] = "flac", ["audio/wav"] = "wav", ["audio/x-wav"] = "wav",
        ["audio/x-ms-wma"] = "wma", ["audio/aiff"] = "aiff", ["audio/x-aiff"] = "aiff", ["audio/midi"] = "mid",
    };

    /// <summary>Kinds of file no site serves a video or playlist as. Pictures, text, JSON and XML are not here: streaming sites
    /// do serve playlists as "poster.png" or "manifest.xml", and only the response's own type gives them away.</summary>
    private static readonly HashSet<string> NeverMedia = new(StringComparer.OrdinalIgnoreCase)
        { "js", "mjs", "cjs", "css", "woff", "woff2", "ttf", "otf", "eot", "svg", "ico", "map", "wasm" };

    /// <summary>The address alone says this is not media (a script, a style, a font), so its headers need not be read.</summary>
    public static bool ClearlyNotMedia(string url)
    {
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || url.Contains(".mpd", StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Extension(url);
        return ext.Length > 0 && NeverMedia.Contains(ext);
    }

    /// <summary>Cheap test on the address alone: does it look like a video, audio or stream playlist?</summary>
    public static bool LooksLikeMediaUrl(string url)
    {
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || url.Contains(".mpd", StringComparison.OrdinalIgnoreCase)) return true;
        var ext = Extension(url);
        return ext.Length > 0 && !SegmentExt.Contains(ext) && FileExt.ContainsKey(ext);
    }

    public static string Extension(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var last = path[(path.LastIndexOf('/') + 1)..];
            var dot = last.LastIndexOf('.');
            return dot > 0 ? last[(dot + 1)..].ToLowerInvariant() : "";
        }
        catch { return ""; }
    }

    public static long? TotalFromContentRange(string? range)
    {
        if (range is null) return null;
        var slash = range.LastIndexOf('/');
        return slash >= 0 && long.TryParse(range[(slash + 1)..].Trim(), out var n) ? n : null;
    }

    /// <summary>The same file asked for with different byte ranges or cache-busting tags maps to one key.</summary>
    public static string Key(string url)
    {
        try
        {
            var u = new UriBuilder(url) { Fragment = "" };
            var kept = (u.Query.TrimStart('?')).Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !new[] { "range", "_", "t", "cb", "cache", "rnd" }.Contains(p.Split('=')[0], StringComparer.OrdinalIgnoreCase));
            u.Query = string.Join('&', kept);
            return u.Uri.ToString();
        }
        catch { return url; }
    }

    private static bool IsGoogleVideo(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pieces of a stream served as ranged requests, never a whole video on their own.</summary>
    private static bool IsRangedPiece(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var q = u.Query;
        return q.Contains("range=", StringComparison.OrdinalIgnoreCase) || q.Contains("bytestart=", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(u.AbsolutePath, @"/range/\d+-\d+");
    }

    private static bool IsAnalytics(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host;
        return host.EndsWith("jwpltx.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("google-analytics.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("googletagmanager.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("doubleclick.net", StringComparison.OrdinalIgnoreCase)
            || u.AbsolutePath.EndsWith("/ping.gif", StringComparison.OrdinalIgnoreCase)
            || u.AbsolutePath.EndsWith("/beacon", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSegmentUrl(string url, string ext)
    {
        if (SegmentExt.Contains(ext)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var path = u.AbsolutePath;
        return path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)
            // One piece of an adaptive stream served as ".../segment.mp4?...&st=video" (Vimeo and others): never a whole video.
            || path.EndsWith("/segment.mp4", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(u.Query, @"[?&]st=(video|audio)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || path.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/seg-", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/segment-", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/chunk-", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/frag-", StringComparison.OrdinalIgnoreCase)
            || path.Contains("-fragment-", StringComparison.OrdinalIgnoreCase);
    }

    public static Classified? Classify(string url, int status, string? contentType, long? contentLength = null, string? contentRange = null, string? disposition = null)
    {
        if (status is not (0 or (>= 200 and < 300))) return null;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
        var mime = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        var ext = Extension(url);

        // YouTube's own hosts and analytics beacons are not media streams
        if (IsGoogleVideo(url) || IsAnalytics(url)) return null;

        if (SegmentTypes.Contains(mime)) return null;

        // Playlists first: "/chunk-list/index.m3u8" is a playlist even though its folder looks like a piece of one.
        var isHlsUrl = ext == "m3u8" || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        var isDashUrl = ext == "mpd" || url.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
        if (HlsTypes.Contains(mime) || (isHlsUrl && (DisguisedTypes.Contains(mime) || mime.Contains("mpegurl")))) return new(MediaKind.Hls, mime.Length > 0 ? mime : null, "mp4", null);
        if (DashTypes.Contains(mime) || (isDashUrl && (DisguisedTypes.Contains(mime) || mime.Contains("xml")))) return new(MediaKind.Dash, mime.Length > 0 ? mime : null, "mp4", null);

        // Pieces of streams and ranged chunks are never offered as videos of their own
        if (IsSegmentUrl(url, ext) || IsRangedPiece(url)) return null;
        // Anything served from a DRM path is encrypted: its manifest is reported as protected, its pieces are not files to save.
        if (url.Contains("/drm/", StringComparison.OrdinalIgnoreCase)) return null;

        // Images, tracking pixels, scripts, and stylesheets without stream signatures are never media streams
        if (ext is "gif" or "png" or "jpg" or "jpeg" or "webp" or "ico" or "svg" or "js" or "css" or "html" or "json") return null;

        var isMediaMime = mime.StartsWith("video/") || mime.StartsWith("audio/");
        FileExt.TryGetValue(ext, out var extContainer);
        // An untyped download that names itself a video ("attachment; filename=ep01.mp4") is one.
        if (!isMediaMime && extContainer is null && ContainerFromDisposition(disposition) is { } named && GenericTypes.Concat(["application/force-download", "application/x-download"]).Contains(mime))
            extContainer = named;
        if (!isMediaMime && !(extContainer is not null && GenericTypes.Contains(mime))) return null;

        var size = TotalFromContentRange(contentRange) ?? (status == 206 ? null : contentLength);
        var audio = mime.StartsWith("audio/") || (extContainer is "mp3" or "m4a" or "aac" or "ogg" or "opus" or "flac" or "wav" or "weba" or "wma");
        if (size is { } known && known < (audio ? MinFileBytes : MinVideoBytes)) return null;

        return new(MediaKind.File, mime.Length > 0 ? mime : null, MimeContainer.GetValueOrDefault(mime) ?? extContainer ?? "mp4", size);
    }

    // ---- Looking inside a response, for streams that neither their address nor their type gives away ----

    /// <summary>Types a playlist hides behind when a site serves it from an address with no ".m3u8" or ".mpd" in it.</summary>
    private static readonly string[] SniffTypes = ["", "text/plain", "application/octet-stream", "binary/octet-stream", "application/binary", "application/xml", "text/xml", "application/x-mpegurl", "application/force-download"];

    /// <summary>
    /// Worth reading the first bytes of this response to see whether it is a playlist? Only small, untyped or plain-text answers
    /// that are not already known to be something else; a large octet-stream must say its size, so a video is never read whole.
    /// </summary>
    public static bool ShouldSniff(string url, int status, string? contentType, long? contentLength)
    {
        if (status != 200 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        var mime = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (!SniffTypes.Contains(mime)) return false;
        var ext = Extension(url);
        if (ext is "m3u8" or "mpd" || NeverMedia.Contains(ext) || FileExt.ContainsKey(ext) || IsSegmentUrl(url, ext) || IsGoogleVideo(url) || IsAnalytics(url)) return false;
        if (ext is "html" or "htm" or "php" or "asp" or "aspx" or "jpg" or "jpeg" or "png" or "gif" or "webp") return contentLength is > 0 and <= 512 * 1024;
        if (mime.Contains("octet") || mime is "application/binary" or "application/force-download") return contentLength is > 0 and <= 2 * 1024 * 1024;
        return contentLength is null or <= 2 * 1024 * 1024;
    }

    /// <summary>The first bytes of a response: an HLS playlist starts "#EXTM3U", a DASH manifest has an &lt;MPD&gt; root.</summary>
    public static MediaKind? SniffPlaylist(ReadOnlySpan<byte> head)
    {
        var start = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) start = 3; // UTF-8 mark
        while (start < head.Length && head[start] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') start++;
        var text = System.Text.Encoding.ASCII.GetString(head[start..Math.Min(head.Length, start + 1024)]);
        if (text.StartsWith("#EXTM3U", StringComparison.Ordinal)) return MediaKind.Hls;
        if (text.StartsWith("<?xml", StringComparison.Ordinal) || text.StartsWith("<MPD", StringComparison.Ordinal))
            return System.Text.RegularExpressions.Regex.IsMatch(text, @"<MPD[\s>]") ? MediaKind.Dash : null;
        return null;
    }

    /// <summary>Worth reading this answer for stream addresses inside it? Small JSON from a site's own API often names the
    /// playlist the player is about to load (and some never load it until you press play).</summary>
    public static bool ShouldScanForLinks(string url, int status, string? contentType, long? contentLength)
    {
        if (status != 200 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || IsAnalytics(url)) return false;
        var mime = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (mime is not ("application/json" or "text/json" or "application/ld+json" or "application/vnd.api+json")) return false;
        return contentLength is null or <= 4 * 1024 * 1024;
    }

    private static readonly System.Text.RegularExpressions.Regex PreviewWords = new(
        @"(?:^|[/_.\-])(?:preview|previews|thumb|thumbs|thumbnail|thumbnails|teaser|vidthumb|videopreview|hover)(?:$|[/_.\-])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A short clip a site plays over a thumbnail ("…/thumbs/…/123-preview.webm"): it is not the video the page is about.
    /// Only the address's path is read, so a site's name or a query word cannot make a real video look like one.
    /// </summary>
    public static bool LooksLikePreviewClip(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && PreviewWords.IsMatch(u.AbsolutePath);

    /// <summary>The address itself says it is a playlist (".m3u8" or ".mpd" in its path, not just somewhere in its query).</summary>
    public static bool NamesAPlaylist(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || IsGoogleVideo(url) || IsAnalytics(url)) return false;
        var path = u.AbsolutePath;
        return path.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || path.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One piece of a stream (".ts", ".m4s", a "segment.mp4", a ranged chunk): never a video of its own.</summary>
    public static bool IsStreamPiece(string url) => IsSegmentUrl(url, Extension(url)) || IsRangedPiece(url);

    private static readonly System.Text.RegularExpressions.Regex StreamLink = new(
        @"https?:(?:\\?/){2}[^\s""'<>\\]*?(?:\\/[^\s""'<>\\]*?)*?\.(?:m3u8|mpd)(?:\?[^\s""'<>]*)?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Absolute stream addresses (".m3u8", ".mpd") written in some text, JSON's "\/" escapes undone. A few at most.</summary>
    public static List<string> FindStreamLinks(string text, int max = 20)
    {
        var found = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in StreamLink.Matches(text))
        {
            var url = m.Value.Replace("\\/", "/").Replace("\\u0026", "&").Replace("&amp;", "&");
            // Trim what follows the address in its text (a comma, a bracket) only after the ".m3u8"/".mpd": commas inside the
            // path are real ("…/video-,360p,720p,.mp4.urlset/master.m3u8" is how nginx-vod and Akamai list a set of qualities).
            var name = url.LastIndexOf(".m3u8", StringComparison.OrdinalIgnoreCase);
            if (name < 0) name = url.LastIndexOf(".mpd", StringComparison.OrdinalIgnoreCase);
            var cut = url.IndexOfAny(['"', '\'', '\\', ',', '}', ']', ' '], Math.Max(0, name));
            if (cut > 0) url = url[..cut];
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) continue;
            if (IsAnalytics(url) || found.Contains(u.ToString(), StringComparer.OrdinalIgnoreCase)) continue;
            found.Add(u.ToString());
            if (found.Count >= max) break;
        }
        return found;
    }

    /// <summary>A download offered with a file name ("Content-Disposition: attachment; filename=episode.mp4") names its kind even
    /// when the address and the type say nothing.</summary>
    public static string? ContainerFromDisposition(string? disposition)
    {
        if (string.IsNullOrEmpty(disposition)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(disposition, @"filename\*?=(?:UTF-8'')?""?([^"";]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var name = Uri.UnescapeDataString(m.Groups[1].Value.Trim());
        var dot = name.LastIndexOf('.');
        return dot > 0 && FileExt.TryGetValue(name[(dot + 1)..], out var c) && !SegmentExt.Contains(name[(dot + 1)..]) ? c : null;
    }

    public static bool IsAudio(string? mime, string container) => (mime?.StartsWith("audio/") ?? false) || container is "mp3" or "m4a" or "aac" or "ogg" or "opus" or "flac" or "wav" or "weba";
}
