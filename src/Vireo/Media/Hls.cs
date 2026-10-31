using System.Text.RegularExpressions;

namespace Vireo.Media;

public sealed record HlsVariant(string Url, int? Width, int? Height, long? Bandwidth, string? Codecs, double? FrameRate, bool AudioOnly, string? AudioGroup = null)
{
    public string Label => AudioOnly ? (Bandwidth is { } b ? $"Audio {b / 1000} kbps" : "Audio")
        : Height is { } h ? (h >= 2160 ? "4K" : h >= 1440 ? "1440p" : $"{h}p") : Bandwidth is { } bw ? $"{bw / 1000} kbps" : "Default";
}

public sealed record HlsAudioTrack(string Name, string? Language, string? Url, bool IsDefault, string? GroupId = null);

public sealed record HlsKey(string Method, string? Uri, byte[]? Iv);

public sealed record HlsMap(string Url, long? Offset, long? Length);

public sealed record HlsSegment(string Url, double Duration, long Sequence, long? Offset, long? Length, HlsKey? Key, HlsMap? Map, bool Discontinuity = false);

/// <summary>A playlist that lists qualities (the "master" playlist).</summary>
public sealed record HlsMaster(IReadOnlyList<HlsVariant> Variants, IReadOnlyList<HlsAudioTrack> Audio, int Subtitles, bool Drm, bool Aes128);

/// <summary>A playlist that lists the pieces of one stream.</summary>
public sealed record HlsMedia(IReadOnlyList<HlsSegment> Segments, double TargetDuration, bool Live, long MediaSequence, bool Drm, bool Aes128, string? Container = null)
{
    public double Duration => Segments.Sum(s => s.Duration);
    public bool IsFragmentedMp4 => Segments.Count > 0 && Segments[0].Map is not null;
}

public static partial class HlsParser
{
    public static bool IsPlaylist(string text) => text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);

    [GeneratedRegex("""([A-Z0-9-]+)=("[^"]*"|[^,]*)""")]
    private static partial Regex Attribute();

    public static Dictionary<string, string> Attributes(string input)
    {
        var result = new Dictionary<string, string>();
        foreach (Match m in Attribute().Matches(input))
        {
            var v = m.Groups[2].Value;
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
            result[m.Groups[1].Value] = v;
        }
        return result;
    }

    public static string Resolve(string uri, string baseUrl)
    {
        try { return new Uri(new Uri(baseUrl), uri).ToString(); }
        catch { return uri; }
    }

    /// <summary>Whether a key line means protected content (anything but plain AES-128 handed out openly).</summary>
    private static void ReadKey(string line, ref bool drm, ref bool aes)
    {
        var a = Attributes(line[(line.IndexOf(':') + 1)..]);
        var method = a.GetValueOrDefault("METHOD", "NONE").ToUpperInvariant();
        if (method == "NONE") return;
        var format = a.GetValueOrDefault("KEYFORMAT", "identity");
        if (method == "AES-128" && format == "identity") { aes = true; return; }
        if (format.Contains("clearkey", StringComparison.OrdinalIgnoreCase)) return;
        // SAMPLE-AES with Apple's open "identity" format is not DRM but needs sample decryption, which is not supported.
        drm = true;
    }

    public static object Parse(string text, string baseUrl)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        bool drm = false, aes = false;
        foreach (var l in lines.Where(l => l.StartsWith("#EXT-X-KEY:") || l.StartsWith("#EXT-X-SESSION-KEY:"))) ReadKey(l, ref drm, ref aes);

        return lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF:")) ? ParseMaster(lines, baseUrl, drm, aes) : ParseMedia(lines, baseUrl, drm, aes);
    }

    private static HlsMaster ParseMaster(List<string> lines, string baseUrl, bool drm, bool aes)
    {
        var variants = new List<HlsVariant>();
        var audio = new List<HlsAudioTrack>();
        var subtitles = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                var a = Attributes(line[18..]);
                if (i + 1 >= lines.Count || lines[i + 1].StartsWith('#')) continue;
                int? w = null, h = null;
                if (a.TryGetValue("RESOLUTION", out var res) && res.Split('x') is [var rw, var rh] && int.TryParse(rw, out var pw) && int.TryParse(rh, out var ph)) { w = pw; h = ph; }
                long? bw = long.TryParse(a.GetValueOrDefault("AVERAGE-BANDWIDTH") ?? a.GetValueOrDefault("BANDWIDTH"), out var b) ? b : null;
                var codecs = a.GetValueOrDefault("CODECS");
                var audioGroup = a.GetValueOrDefault("AUDIO");
                var audioOnly = w is null && codecs is not null && !Regex.IsMatch(codecs, "avc|hvc|hev|av01|vp0?9|vp8");
                variants.Add(new HlsVariant(Resolve(lines[i + 1], baseUrl), w, h, bw, codecs, double.TryParse(a.GetValueOrDefault("FRAME-RATE"), System.Globalization.CultureInfo.InvariantCulture, out var fr) ? fr : null, audioOnly, audioGroup));
            }
            else if (line.StartsWith("#EXT-X-MEDIA:"))
            {
                var a = Attributes(line[13..]);
                if (a.GetValueOrDefault("TYPE") == "AUDIO")
                    audio.Add(new HlsAudioTrack(a.GetValueOrDefault("NAME") ?? a.GetValueOrDefault("LANGUAGE") ?? $"Track {audio.Count + 1}", a.GetValueOrDefault("LANGUAGE"),
                        a.TryGetValue("URI", out var u) ? Resolve(u, baseUrl) : null, a.GetValueOrDefault("DEFAULT") == "YES", a.GetValueOrDefault("GROUP-ID")));
                else if (a.GetValueOrDefault("TYPE") == "SUBTITLES") subtitles++;
            }
        }
        // Best first: tallest, then highest bitrate.
        variants = [.. variants.OrderByDescending(v => v.Height ?? 0).ThenByDescending(v => v.Bandwidth ?? 0)];
        return new HlsMaster(variants, audio, subtitles, drm, aes);
    }

    private static HlsMedia ParseMedia(List<string> lines, string baseUrl, bool drm, bool aes)
    {
        var segments = new List<HlsSegment>();
        double target = 0, duration = 0;
        long sequence = 0, lastEnd = 0;
        HlsKey? key = null;
        HlsMap? map = null;
        long? offset = null, length = null;
        var ended = false;
        var discontinuity = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-TARGETDURATION:")) double.TryParse(line[22..], System.Globalization.CultureInfo.InvariantCulture, out target);
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) long.TryParse(line[22..], out sequence);
            else if (line is "#EXT-X-ENDLIST" or "#EXT-X-PLAYLIST-TYPE:VOD") ended = true;
            else if (line == "#EXT-X-DISCONTINUITY") discontinuity = true;
            else if (line.StartsWith("#EXTINF:"))
            {
                var d = line[8..];
                var comma = d.IndexOf(',');
                double.TryParse(comma >= 0 ? d[..comma] : d, System.Globalization.CultureInfo.InvariantCulture, out duration);
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:"))
            {
                var parts = line[17..].Split('@');
                length = long.Parse(parts[0]);
                offset = parts.Length > 1 ? long.Parse(parts[1]) : lastEnd;
            }
            else if (line.StartsWith("#EXT-X-KEY:"))
            {
                var a = Attributes(line[11..]);
                var method = a.GetValueOrDefault("METHOD", "NONE").ToUpperInvariant();
                var plain = method == "AES-128" && a.GetValueOrDefault("KEYFORMAT", "identity") == "identity";
                key = method == "NONE" ? null : new HlsKey(plain ? "AES-128" : "OTHER", a.TryGetValue("URI", out var u) ? Resolve(u, baseUrl) : null, a.TryGetValue("IV", out var iv) ? HexToBytes(iv) : null);
            }
            else if (line.StartsWith("#EXT-X-MAP:"))
            {
                var a = Attributes(line[11..]);
                long? mo = null, ml = null;
                if (a.TryGetValue("BYTERANGE", out var br)) { var p = br.Split('@'); ml = long.Parse(p[0]); mo = p.Length > 1 ? long.Parse(p[1]) : 0; }
                map = a.TryGetValue("URI", out var mu) ? new HlsMap(Resolve(mu, baseUrl), mo, ml) : null;
            }
            else if (!line.StartsWith('#'))
            {
                segments.Add(new HlsSegment(Resolve(line, baseUrl), duration, sequence + segments.Count, offset, length, key, map, discontinuity));
                discontinuity = false;
                if (offset is { } o && length is { } l) lastEnd = o + l;
                offset = length = null;
                duration = 0;
            }
        }
        return new HlsMedia(segments, target, !ended, sequence, drm, aes);
    }

    public static byte[] HexToBytes(string hex)
    {
        hex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        hex = hex.PadLeft(32, '0');
        return Convert.FromHexString(hex[^32..]);
    }

    /// <summary>When a playlist gives no IV, the segment's sequence number is used, as the HLS spec says.</summary>
    public static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (var i = 15; i >= 8 && sequence > 0; i--, sequence >>= 8) iv[i] = (byte)(sequence & 0xff);
        return iv;
    }
}
