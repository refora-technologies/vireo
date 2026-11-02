using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Vireo.Media;

/// <summary>One quality (or one sound) of a DASH stream, with the pieces it is made of.</summary>
public sealed record DashRepresentation(
    string Id, bool IsAudio, string Mime, string? Codecs, int? Width, int? Height, long? Bandwidth, double? FrameRate,
    string? Language, bool IsMain, string? FileUrl, HlsMap? Init, IReadOnlyList<HlsSegment> Segments)
{
    public bool IsWebm => Mime.Contains("webm", StringComparison.OrdinalIgnoreCase);

    public string Label => IsAudio ? (Bandwidth is { } b ? $"Audio {b / 1000} kbps" : "Audio")
        : Height is { } h ? (h >= 2160 ? "4K" : h >= 1440 ? "1440p" : $"{h}p") : Bandwidth is { } bw ? $"{bw / 1000} kbps" : "Default";

    /// <summary>The pieces as the same shape an HLS stream has, so one downloader handles both.</summary>
    public HlsMedia ToMedia(bool live, double target)
    {
        var withInit = Init is null ? Segments : [.. Segments.Select(s => s with { Map = Init })];
        return new HlsMedia(withInit, target, live, withInit.Count > 0 ? withInit[0].Sequence : 0, false, false, IsWebm ? "webm" : null);
    }
}

/// <summary>A DASH manifest (.mpd): the qualities and sounds a stream offers.</summary>
public sealed record DashManifest(bool Live, double? Duration, double TargetDuration, bool Protected, IReadOnlyList<DashRepresentation> Representations)
{
    /// <summary>Marks which quality or sound of a manifest a download wants: it is added to the manifest's address after a '#', which is never sent to the server.</summary>
    public const string Tag = "vireo-rep=";

    public static string WithRepresentation(string url, string id) => url.Split('#')[0] + "#" + Tag + Uri.EscapeDataString(id);

    public static string? RepresentationOf(string url)
    {
        var hash = url.IndexOf('#');
        if (hash < 0) return null;
        var frag = url[(hash + 1)..];
        return frag.StartsWith(Tag, StringComparison.Ordinal) ? Uri.UnescapeDataString(frag[Tag.Length..]) : null;
    }

    /// <summary>Qualities best first. MP4 is preferred because it can be joined with its sound; WebM is offered only when there is no MP4.</summary>
    public IReadOnlyList<DashRepresentation> Videos
    {
        get
        {
            var all = Representations.Where(r => !r.IsAudio).ToList();
            var mp4 = all.Where(r => !r.IsWebm).ToList();
            return [.. (mp4.Count > 0 ? mp4 : all).OrderByDescending(r => r.Height ?? 0).ThenByDescending(r => r.Bandwidth ?? 0)];
        }
    }

    /// <summary>The sound to join with a quality: the main language, best bitrate, in the same container family as the video.</summary>
    public DashRepresentation? BestAudio(bool webm)
    {
        var sounds = Representations.Where(r => r.IsAudio && r.IsWebm == webm).ToList();
        if (sounds.Count == 0) sounds = [.. Representations.Where(r => r.IsAudio)];
        return sounds.OrderByDescending(r => r.IsMain).ThenByDescending(r => r.Bandwidth ?? 0).FirstOrDefault();
    }

    public DashRepresentation? Find(string? id) => id is null ? null : Representations.FirstOrDefault(r => r.Id == id);
}

public static partial class DashParser
{
    public static bool IsManifest(string text) => text.Contains("<MPD", StringComparison.Ordinal);

    [GeneratedRegex(@"\$(RepresentationID|Number|Bandwidth|Time)(?:%0(\d+)d)?\$")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^P(?:(\d+(?:\.\d+)?)Y)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)D)?(?:T(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?)?$")]
    private static partial Regex IsoDuration();

    /// <summary>"PT1H2M3.5S" to seconds.</summary>
    public static double? Seconds(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        var m = IsoDuration().Match(iso.Trim());
        if (!m.Success) return null;
        double G(int i) => m.Groups[i].Success ? double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        return G(1) * 31536000 + G(2) * 2592000 + G(3) * 86400 + G(4) * 3600 + G(5) * 60 + G(6);
    }

    private static string? A(XElement? e, string name) => e?.Attribute(name)?.Value;
    private static IEnumerable<XElement> Kids(XElement e, string name) => e.Elements().Where(x => x.Name.LocalName == name);
    private static XElement? Kid(XElement? e, string name) => e is null ? null : Kids(e, name).FirstOrDefault();
    private static double D(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static long L(string? s, long fallback = 0) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Resolve(string baseUrl, XElement? baseElement)
    {
        var text = baseElement?.Value.Trim();
        return string.IsNullOrEmpty(text) ? baseUrl : HlsParser.Resolve(text, baseUrl);
    }

    private static string Expand(string pattern, string id, long number, long time, long? bandwidth) =>
        Placeholder().Replace(pattern.Replace("$$", "\u0001"), m =>
        {
            var width = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            var value = m.Groups[1].Value switch
            {
                "RepresentationID" => id,
                "Number" => number.ToString(CultureInfo.InvariantCulture),
                "Bandwidth" => (bandwidth ?? 0).ToString(CultureInfo.InvariantCulture),
                "Time" => time.ToString(CultureInfo.InvariantCulture),
                _ => (bandwidth ?? 0).ToString(CultureInfo.InvariantCulture),
            };
            return width > 0 ? value.PadLeft(width, '0') : value;
        }).Replace("\u0001", "$");

    /// <summary>Reads a manifest. <paramref name="now"/> lets tests fix the clock for live streams.</summary>
    public static DashManifest Parse(string text, string manifestUrl, DateTimeOffset? now = null)
    {
        var root = XDocument.Parse(text).Root ?? throw new InvalidDataException("The stream manifest is empty.");
        if (root.Name.LocalName != "MPD") throw new InvalidDataException("This is not a stream manifest.");
        var live = A(root, "type") == "dynamic";
        var shiftDepth = Seconds(A(root, "timeShiftBufferDepth"));
        var total = Seconds(A(root, "mediaPresentationDuration"));
        var location = Kid(root, "Location")?.Value.Trim();
        if (!string.IsNullOrEmpty(location)) manifestUrl = HlsParser.Resolve(location, manifestUrl);
        var baseMpd = Resolve(manifestUrl, Kid(root, "BaseURL"));
        var periods = Kids(root, "Period").ToList();

        // A stream cut into periods (often content with ads between) is read as its longest period, which is the programme itself.
        var best = periods.Select((p, i) =>
        {
            var start = Seconds(A(p, "start")) ?? 0;
            var length = Seconds(A(p, "duration")) ?? (i + 1 < periods.Count ? (Seconds(A(periods[i + 1], "start")) ?? 0) - start : (total ?? 0) - start);
            return (Period: p, Start: start, Length: length > 0 ? length : (double?)null);
        }).Where(x => Kids(x.Period, "AdaptationSet").Any()).OrderByDescending(x => x.Length ?? 0).FirstOrDefault();
        if (best.Period is null) throw new InvalidDataException("The stream lists nothing to download.");

        var period = best.Period;
        var periodLength = best.Length ?? total;
        var basePeriod = Resolve(baseMpd, Kid(period, "BaseURL"));
        var availability = DateTimeOffset.TryParse(A(root, "availabilityStartTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var avail) ? avail : (DateTimeOffset?)null;
        var drm = Kids(root, "ContentProtection").Any() || Kids(period, "ContentProtection").Any();

        var reps = new List<DashRepresentation>();
        double target = 0;
        foreach (var set in Kids(period, "AdaptationSet"))
        {
            var setMime = A(set, "mimeType");
            var kind = A(set, "contentType") ?? (setMime ?? "").Split('/')[0];
            var baseSet = Resolve(basePeriod, Kid(set, "BaseURL"));
            var setProtected = Kids(set, "ContentProtection").Any();
            var main = Kids(set, "Role").Any(r => A(r, "value") == "main") || !Kids(set, "Role").Any();

            foreach (var rep in Kids(set, "Representation"))
            {
                var mime = A(rep, "mimeType") ?? setMime ?? "";
                var type = kind.Length > 0 && kind != "application" ? kind : mime.Split('/')[0];
                if (type is not ("video" or "audio")) continue; // subtitles, thumbnails and the like
                if (setProtected || Kids(rep, "ContentProtection").Any()) drm = true;

                var id = A(rep, "id") ?? reps.Count.ToString(CultureInfo.InvariantCulture);
                var bandwidth = long.TryParse(A(rep, "bandwidth"), out var bw) ? bw : (long?)null;
                var baseRep = Resolve(baseSet, Kid(rep, "BaseURL"));
                var frameRate = A(rep, "frameRate") ?? A(set, "frameRate");
                double? fps = null;
                if (frameRate is not null) { var f = frameRate.Split('/'); fps = f.Length == 2 && D(f[1]) > 0 ? D(f[0]) / D(f[1]) : D(f[0]); }

                var (init, segments, fileUrl, segTarget) = Segments(rep, set, id, bandwidth, baseRep, periodLength, live, now ?? DateTimeOffset.UtcNow, availability, best.Start, shiftDepth);
                target = Math.Max(target, segTarget);
                if (segments.Count == 0 && fileUrl is null) continue;
                reps.Add(new DashRepresentation(id, type == "audio", mime, A(rep, "codecs") ?? A(set, "codecs"),
                    int.TryParse(A(rep, "width") ?? A(set, "width"), out var w) ? w : null, int.TryParse(A(rep, "height") ?? A(set, "height"), out var h) ? h : null,
                    bandwidth, fps, A(set, "lang"), main, fileUrl, init, segments));
            }
        }
        if (reps.Count == 0 && !drm) throw new InvalidDataException("The stream lists nothing to download.");
        return new DashManifest(live, live ? null : periodLength ?? total, target > 0 ? target : 4, drm, reps);
    }

    private static (HlsMap? Init, List<HlsSegment> Segments, string? FileUrl, double Target) Segments(
        XElement rep, XElement set, string id, long? bandwidth, string baseUrl, double? periodLength, bool live, DateTimeOffset now, DateTimeOffset? availability, double periodStart, double? shiftDepth)
    {
        var list = new List<HlsSegment>();
        var template = Kid(rep, "SegmentTemplate");
        var inherited = Kid(set, "SegmentTemplate");
        if (template is not null || inherited is not null)
        {
            string? T(string name) => A(template, name) ?? A(inherited, name);
            var timeline = Kid(Kid(template, "SegmentTimeline") is null ? inherited : template, "SegmentTimeline");
            var scale = Math.Max(1, D(T("timescale") is { } ts ? ts : "1"));
            var start = L(T("startNumber"), 1);
            var media = T("media");
            if (media is null) return (null, list, null, 0);
            HlsMap? init = null;
            if (T("initialization") is { } ip) init = new HlsMap(HlsParser.Resolve(Expand(ip, id, 0, 0, bandwidth), baseUrl), null, null);
            var byTime = media.Contains("$Time", StringComparison.Ordinal);
            double biggest = 0;

            void Add(long number, long time, double seconds)
            {
                list.Add(new HlsSegment(HlsParser.Resolve(Expand(media, id, number, time, bandwidth), baseUrl), seconds, byTime ? time : number, null, null, null, null));
                biggest = Math.Max(biggest, seconds);
            }

            if (timeline is not null)
            {
                long time = 0, number = start;
                var first = true;
                foreach (var s in Kids(timeline, "S"))
                {
                    if (A(s, "t") is { } t) time = L(t);
                    else if (first) time = 0;
                    first = false;
                    var d = L(A(s, "d"));
                    if (d <= 0) continue;
                    var repeat = (int)L(A(s, "r"));
                    if (repeat < 0) repeat = periodLength is { } pl ? (int)Math.Max(0, Math.Ceiling((pl * scale - time) / d) - 1) : 0;
                    for (var i = 0; i <= repeat; i++, number++, time += d) Add(number, time, d / scale);
                }
            }
            else if (D(T("duration")) is > 0 and var dur)
            {
                var seconds = dur / scale;
                if (!live && periodLength is { } len)
                {
                    var count = (int)Math.Ceiling(len / seconds - 1e-6);
                    for (var i = 0; i < count; i++) Add(start + i, (long)(i * dur), seconds);
                }
                else if (live && availability is { } av)
                {
                    // A live stream with numbered pieces: work out which ones exist by the clock, and keep the last minute and a half.
                    var elapsed = (now - av).TotalSeconds - periodStart;
                    var latest = (long)Math.Floor(elapsed / seconds) - 1;
                    // the last minute and a half, but never older than the server keeps (its time-shift buffer), with a margin
                    var window = shiftDepth is { } depth ? Math.Clamp(depth - seconds * 3, seconds * 2, 90) : 90;
                    var keep = (long)Math.Ceiling(window / seconds);
                    for (var n = Math.Max(0, latest - keep + 1); n <= latest; n++) Add(start + n, (long)(n * dur), seconds);
                }
            }
            return (init, list, null, biggest);
        }

        var segList = Kid(rep, "SegmentList") ?? Kid(set, "SegmentList");
        if (segList is not null)
        {
            var scale = Math.Max(1, D(A(segList, "timescale") ?? "1"));
            var seconds = D(A(segList, "duration")) / scale;
            var initEl = Kid(segList, "Initialization");
            HlsMap? init = null;
            if (initEl is not null)
            {
                var (o, n) = Range(A(initEl, "range"));
                init = new HlsMap(A(initEl, "sourceURL") is { } src ? HlsParser.Resolve(src, baseUrl) : baseUrl, o, n);
            }
            var number = L(A(segList, "startNumber"), 1);
            foreach (var s in Kids(segList, "SegmentURL"))
            {
                var (o, n) = Range(A(s, "mediaRange"));
                list.Add(new HlsSegment(A(s, "media") is { } m ? HlsParser.Resolve(m, baseUrl) : baseUrl, seconds, number++, o, n, null, null));
            }
            return (init, list, null, seconds);
        }

        // Just an address (with an index inside the file): the whole thing is one downloadable file.
        return (null, list, Kid(rep, "BaseURL") is not null || Kid(rep, "SegmentBase") is not null ? baseUrl : null, 0);
    }

    private static (long? Offset, long? Length) Range(string? range)
    {
        if (range is null) return (null, null);
        var p = range.Split('-');
        if (p.Length != 2 || !long.TryParse(p[0], out var a) || !long.TryParse(p[1], out var b)) return (null, null);
        return (a, b - a + 1);
    }
}
