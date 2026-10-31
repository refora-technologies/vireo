using System.IO;
using System.Net.Http;
using Vireo.Downloads;
using Vireo.Services;

namespace Vireo.Media;

/// <summary>One way to save a piece of media: a quality, or audio only.</summary>
public sealed record MediaChoice(string Id, string Label, string Detail, long? SizeBytes, string Url, string? AudioUrl, bool AudioOnly = false, long? VideoSize = null, long? AudioSize = null, bool Files = false, string? Referer = null);

public enum MediaStatus { Probing, Ready, Protected, Failed }

/// <summary>A video or audio the page is playing, as far as Vireo can tell.</summary>
public sealed class DetectedMedia
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Url { get; init; } = "";
    public MediaKind Kind { get; init; }
    public string Container { get; init; } = "mp4";
    public string? Mime { get; init; }
    public long? SizeBytes { get; set; }
    public double? DurationSec { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool Live { get; set; }
    public bool IsAudio { get; set; }
    public string? Poster { get; set; }
    public string Title { get; set; } = "";
    public string? Referer { get; set; }
    /// <summary>Only read about (an address in a site's JSON, or a worker's request), not yet seen playing: shown once it answers as a stream, dropped quietly if not.</summary>
    public bool Quiet { get; set; }
    /// <summary>Found written in the page (not seen loading): a file must prove it is a real video (not a preview) to be shown.</summary>
    public bool FromPage { get; set; }
    /// <summary>Worth showing: ready, or being read when it was seen for itself.</summary>
    public bool Showable => Status == MediaStatus.Ready || (Status == MediaStatus.Probing && !Quiet);
    public List<string> CandidateReferers { get; } = [];
    public MediaStatus Status { get; set; } = MediaStatus.Probing;
    public string? Error { get; set; }
    public List<MediaChoice> Choices { get; set; } = [];
    public DownloadItem? Download { get; set; }
    public DateTime FoundAt { get; init; } = DateTime.Now;

    /// <summary>For YouTube: the video's id, so a page that changes video without reloading gets a fresh list.</summary>
    public string? VideoKey { get; init; }

    public string KindLabel => IsAudio ? "Audio" : Kind switch { MediaKind.Hls => "Stream", MediaKind.Dash => "Stream", MediaKind.YouTube => "YouTube", _ => "Video file" };
}

/// <summary>Reads what a detected stream offers: its qualities, length and whether it is live or protected.</summary>
public static class MediaProbe
{
    public static Task ProbeAsync(DetectedMedia m, HttpClient http, Func<string, HttpRequestMessage> request, CancellationToken ct) =>
        ProbeAsync(m, http, (u, _) => request(u), ct);

    public static async Task ProbeAsync(DetectedMedia m, HttpClient http, Func<string, string?, HttpRequestMessage> request, CancellationToken ct)
    {
        try
        {
            switch (m.Kind)
            {
                case MediaKind.File:
                    if (m.SizeBytes is null)
                    {
                        using var req = request(m.Url, m.Referer);
                        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                        m.SizeBytes = res.Content.Headers.ContentRange?.Length ?? res.Content.Headers.ContentLength;
                    }
                    m.Choices = [new MediaChoice("file", m.IsAudio ? $"Audio ({m.Container.ToUpperInvariant()})" : "Original", $"{m.Container.ToUpperInvariant()}{(m.Height is { } h ? $" · {h}p" : "")}", m.SizeBytes, m.Url, null, m.IsAudio, Referer: m.Referer)];
                    m.Status = MediaStatus.Ready;
                    break;
                case MediaKind.Hls:
                    await ProbeHlsAsync(m, http, request, ct);
                    break;
                case MediaKind.YouTube:
                    await ProbeYouTubeAsync(m, http, ct);
                    break;
                case MediaKind.Dash:
                    await ProbeDashAsync(m, http, request, ct);
                    break;
                default:
                    m.Status = MediaStatus.Failed;
                    m.Error = "Vireo does not know this kind of stream.";
                    break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            m.Status = MediaStatus.Failed;
            m.Error = e is HttpRequestException ? (e.Message.Contains("403") ? "Server answered 403" : "Could not read the stream.") : e.Message;
        }
    }

    public static string RootDomainOf(string hostname)
    {
        var parts = hostname.Split('.');
        if (parts.Length <= 2) return hostname;
        var sld = parts[^2];
        if (parts.Length > 2 && (sld == "co" || sld == "com" || sld == "net" || sld == "org" || sld == "gov" || sld == "edu") && parts[^1].Length <= 3)
            return string.Join('.', parts[^3..]);
        return string.Join('.', parts[^2..]);
    }

    public static List<string> BuildCandidateReferers(string mediaUrl, string? initialReferer, IEnumerable<string>? additional = null)
    {
        var candidates = new List<string>();
        void Add(string? r)
        {
            if (string.IsNullOrWhiteSpace(r)) return;
            var trimmed = r.Trim();
            if (!candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                candidates.Add(trimmed);
        }

        Add(initialReferer);

        if (Uri.TryCreate(initialReferer, UriKind.Absolute, out var initUri) && (initUri.Scheme == "http" || initUri.Scheme == "https"))
        {
            Add(initUri.GetLeftPart(UriPartial.Authority) + "/");
            Add(initUri.GetLeftPart(UriPartial.Authority));
        }

        if (additional is not null)
        {
            foreach (var extra in additional)
            {
                Add(extra);
                if (Uri.TryCreate(extra, UriKind.Absolute, out var eUri) && (eUri.Scheme == "http" || eUri.Scheme == "https"))
                {
                    Add(eUri.GetLeftPart(UriPartial.Authority) + "/");
                    Add(eUri.GetLeftPart(UriPartial.Authority));
                }
            }
        }

        if (Uri.TryCreate(mediaUrl, UriKind.Absolute, out var mUri))
        {
            Add(mUri.GetLeftPart(UriPartial.Authority) + "/");
            Add(mUri.GetLeftPart(UriPartial.Authority));
            var host = mUri.Host;
            var root = RootDomainOf(host);
            if (!string.Equals(root, host, StringComparison.OrdinalIgnoreCase))
            {
                Add($"{mUri.Scheme}://{root}/");
                Add($"{mUri.Scheme}://{root}");
            }
        }

        candidates.Add("");
        return candidates;
    }

    private static async Task<(string Text, string? WorkingReferer, string FinalUrl)> TextWithFallbackAsync(string url, DetectedMedia m, HttpClient http, Func<string, string?, HttpRequestMessage> request, CancellationToken ct)
    {
        var candidates = BuildCandidateReferers(url, m.Referer, m.CandidateReferers);
        if (candidates.Count == 0) candidates.Add("");

        Exception? lastEx = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var refToUse = string.IsNullOrEmpty(candidate) ? null : candidate;
                using var req = request(url, candidate); // an empty candidate means no referer
                using var res = await http.SendAsync(req, ct);
                if ((int)res.StatusCode is 401 or 403)
                {
                    lastEx = new HttpRequestException($"Server answered {(int)res.StatusCode}", null, res.StatusCode);
                    continue;
                }
                res.EnsureSuccessStatusCode();
                var text = (await res.Content.ReadAsStringAsync(ct)).TrimStart('\uFEFF');
                // Relative addresses in a playlist are relative to where it really came from, which is not where it was asked for after a redirect.
                return (text, refToUse, res.RequestMessage?.RequestUri?.ToString() ?? url);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            {
                lastEx = ex;
                continue;
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        throw lastEx ?? new HttpRequestException("Could not read the stream.");
    }

    private static async Task<string> TextAsync(string url, HttpClient http, Func<string, string?, HttpRequestMessage> request, string? referer, CancellationToken ct)
    {
        using var req = request(url, referer);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    private static async Task ProbeYouTubeAsync(DetectedMedia m, HttpClient http, CancellationToken ct)
    {
        var info = await YouTube.ResolveAsync(m.VideoKey!, http, ct);
        if (info.Title.Length > 0) m.Title = info.Title;
        m.DurationSec = info.DurationSec > 0 ? info.DurationSec : null;
        m.Poster = info.Thumbnail;
        m.Live = info.IsLive;
        if (info.Status == YtStatus.Protected) { m.Status = MediaStatus.Protected; return; }
        if (info.Status != YtStatus.Ok || info.Video.Count == 0 || info.Audio is null)
        {
            m.Status = MediaStatus.Failed;
            m.Error = info.Status switch
            {
                YtStatus.LoginRequired => "YouTube is asking for a sign-in or a check that you are not a bot, so it will not hand out this video right now. Play it once in this tab, then try again.",
                YtStatus.LiveOffline => "This live stream is not on yet.",
                _ => info.Reason ?? "YouTube did not offer any downloadable streams for this video.",
            };
            return;
        }
        var audio = info.Audio;
        m.Height = info.Video[0].Height;
        m.Width = info.Video[0].Width;
        m.Choices = [.. info.Video.Select(v => new MediaChoice($"y{v.Itag}", v.Label,
            string.Join(" · ", new[] { v.Width > 0 ? $"{v.Width}×{v.Height}" : null, YouTube.CodecName(v.Codec) is { Length: > 0 } c ? c : null }.Where(x => x is not null)),
            v.Length + audio.Length, v.Url, audio.Url, false, v.Length, audio.Length)),
            new MediaChoice("yaudio", "Audio only", "AAC · original quality", audio.Length, audio.Url, null, true, null, audio.Length)];
        m.Status = MediaStatus.Ready;
    }

    private static async Task ProbeDashAsync(DetectedMedia m, HttpClient http, Func<string, string?, HttpRequestMessage> request, CancellationToken ct)
    {
        var (text, workingRef, finalUrl) = await TextWithFallbackAsync(m.Url, m, http, request, ct);
        if (workingRef is not null) m.Referer = workingRef;
        if (!DashParser.IsManifest(text)) throw new InvalidDataException("Not a stream manifest.");
        var dash = DashParser.Parse(text, finalUrl);
        m.Live = dash.Live;
        m.DurationSec = dash.Duration;
        if (dash.Protected) { m.Status = MediaStatus.Protected; return; }

        string Address(DashRepresentation r) => r.FileUrl ?? DashManifest.WithRepresentation(m.Url, r.Id);
        var videos = dash.Videos;
        var choices = new List<MediaChoice>();
        var seen = new HashSet<string>();
        foreach (var v in videos)
        {
            if (!seen.Add($"{v.Height}x{v.Width}")) continue; // several bitrates of one size: the best one is enough
            var audio = dash.BestAudio(v.IsWebm);
            var detail = string.Join(" · ", new[] { v.Width is { } w && v.Height is { } h ? $"{w}×{h}" : null, v.FrameRate is > 31 ? $"{Math.Round(v.FrameRate.Value)}fps" : null, v.IsWebm ? "WebM" : null }.Where(x => x is not null));
            var vSize = Estimate(v.Bandwidth, dash.Duration);
            var aSize = audio is null ? null : Estimate(audio.Bandwidth, dash.Duration);
            choices.Add(new MediaChoice($"d{v.Id}", v.Label, detail, vSize is null ? null : vSize + (aSize ?? 0), Address(v), audio is null ? null : Address(audio), false, vSize, aSize, Files: v.FileUrl is not null, Referer: m.Referer));
        }
        var sound = dash.BestAudio(false);
        if (sound is not null && !dash.Live)
            choices.Add(new MediaChoice($"d{sound.Id}", "Audio only", sound.IsWebm ? "WebM audio" : "Original sound", Estimate(sound.Bandwidth, dash.Duration), Address(sound), null, true, null, Estimate(sound.Bandwidth, dash.Duration), Files: sound.FileUrl is not null, Referer: m.Referer));
        if (videos.Count > 0) { m.Width = videos[0].Width; m.Height = videos[0].Height; }
        m.IsAudio |= videos.Count == 0;
        m.Choices = choices;
        m.Status = choices.Count > 0 ? MediaStatus.Ready : MediaStatus.Failed;
        if (choices.Count == 0) m.Error = "The stream lists nothing to download.";
    }

    private static async Task ProbeHlsAsync(DetectedMedia m, HttpClient http, Func<string, string?, HttpRequestMessage> request, CancellationToken ct)
    {
        var (text, workingRef, finalUrl) = await TextWithFallbackAsync(m.Url, m, http, request, ct);
        if (workingRef is not null) m.Referer = workingRef;
        if (!HlsParser.IsPlaylist(text)) throw new InvalidDataException("Not a playlist.");
        var parsed = HlsParser.Parse(text, finalUrl);

        if (parsed is HlsMedia single)
        {
            Apply(m, single);
            m.Choices = [new MediaChoice("stream", "Best available", single.Live ? "Live recording" : "MP4", Estimate(null, m.DurationSec), m.Url, null, Referer: m.Referer)];
            return;
        }

        var master = (HlsMaster)parsed;
        if (master.Drm) { m.Status = MediaStatus.Protected; return; }
        var videos = OneOfEach(master.Variants.Where(v => !v.AudioOnly));
        // The best quality's own playlist tells us how long it is and whether it is live or protected.
        var look = videos.FirstOrDefault() ?? master.Variants.FirstOrDefault();
        if (look is not null)
        {
            var (innerText, innerRef, innerUrl) = await TextWithFallbackAsync(look.Url, m, http, request, ct);
            if (innerRef is not null) m.Referer = innerRef;
            var inner = HlsParser.Parse(innerText, innerUrl) as HlsMedia;
            if (inner is not null)
            {
                Apply(m, inner);
                if (inner.Drm) return;
            }
            m.Width = look.Width; m.Height = look.Height;
        }
        // The sound to use when a quality names none: the stereo AAC one if there is a choice, else the default.
        var aacGroup = videos.FirstOrDefault(v => IsAac(v) && v.AudioGroup is not null)?.AudioGroup;
        var defaultAudio = (aacGroup is null ? null : master.Audio.FirstOrDefault(a => a.GroupId == aacGroup && a.IsDefault && a.Url is not null) ?? master.Audio.FirstOrDefault(a => a.GroupId == aacGroup && a.Url is not null))
            ?? master.Audio.FirstOrDefault(a => a.IsDefault && a.Url is not null) ?? master.Audio.FirstOrDefault(a => a.Url is not null);
        var choices = videos.Select((v, i) =>
        {
            HlsAudioTrack? audio = null;
            if (v.AudioGroup is not null)
                audio = master.Audio.FirstOrDefault(a => a.GroupId == v.AudioGroup && a.Url is not null);
            audio ??= defaultAudio;
            return new MediaChoice($"v{i}", v.Label, string.Join(" · ", new[] { v.Width is { } w && v.Height is { } h ? $"{w}×{h}" : null, v.FrameRate is > 31 ? $"{Math.Round(v.FrameRate.Value)}fps" : null, VideoCodecName(v.Codecs) }.Where(x => x is not null)), Estimate(v.Bandwidth, m.DurationSec), v.Url, audio?.Url, Referer: m.Referer);
        }).ToList();
        // Audio only: the separate audio playlist, else the lightest quality (it carries the same sound).
        var audioUrl = defaultAudio?.Url ?? master.Variants.FirstOrDefault(v => v.AudioOnly)?.Url ?? videos.OrderBy(v => v.Bandwidth ?? long.MaxValue).FirstOrDefault()?.Url;
        if (audioUrl is not null && !m.Live) choices.Add(new MediaChoice("audio", "Audio only", "Original sound", m.DurationSec is { } d ? (long)(d * 128_000 / 8) : null, audioUrl, null, true, Referer: m.Referer));
        if (choices.Count == 0 && master.Variants.Count > 0) choices.Add(new MediaChoice("v0", master.Variants[0].Label, "", null, master.Variants[0].Url, null, master.Variants[0].AudioOnly, Referer: m.Referer));
        m.Choices = choices;
        m.IsAudio |= videos.Count == 0;
        if (m.Status == MediaStatus.Probing) m.Status = MediaStatus.Ready;
    }

    private static bool IsAvc(HlsVariant v) => v.Codecs?.Contains("avc1", StringComparison.OrdinalIgnoreCase) == true || v.Codecs?.Contains("avc3", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The video codec named in a CODECS list ("avc1.640028,mp4a.40.2" is H.264), or nothing when it is not given.</summary>
    public static string? VideoCodecName(string? codecs) =>
        codecs?.Split(',').Select(c => c.Trim().Replace("avc3", "avc1")).Select(YouTube.CodecName).FirstOrDefault(n => n.Length > 0);

    /// <summary>
    /// One video offered as two playlists, one per codec (an AV1 set and an H.264 set): one entry with every size, H.264 where
    /// both have it. Same title, same length, not live, and most of their sizes in common.
    /// </summary>
    public static bool SameVideoOtherCodec(DetectedMedia a, DetectedMedia b)
    {
        if (a.Status != MediaStatus.Ready || b.Status != MediaStatus.Ready || a.Live || b.Live) return false;
        if (a.Kind is not (MediaKind.Hls or MediaKind.Dash) || b.Kind is not (MediaKind.Hls or MediaKind.Dash)) return false;
        if (!string.Equals(a.Title, b.Title, StringComparison.Ordinal) || a.DurationSec is not { } da || b.DurationSec is not { } db || Math.Abs(da - db) > 1.5) return false;
        var la = a.Choices.Where(c => !c.AudioOnly).Select(c => c.Label).ToHashSet();
        var lb = b.Choices.Where(c => !c.AudioOnly).Select(c => c.Label).ToHashSet();
        var common = la.Intersect(lb).Count();
        return common >= 2 && common * 2 >= Math.Min(la.Count, lb.Count);
    }

    /// <summary>Takes <paramref name="other"/>'s sizes into <paramref name="keep"/>: every size once, H.264 preferred, biggest first.</summary>
    public static void MergeChoices(DetectedMedia keep, DetectedMedia other)
    {
        static int Height(MediaChoice c)
        {
            var m = System.Text.RegularExpressions.Regex.Match(c.Detail, @"×(\d+)");
            if (m.Success) return int.Parse(m.Groups[1].Value);
            if (c.Label == "4K") return 2160;
            var d = new string(c.Label.TakeWhile(char.IsDigit).ToArray());
            return d.Length > 0 ? int.Parse(d) : 0;
        }
        static bool Avc(MediaChoice c) => c.Detail.Contains("H.264", StringComparison.Ordinal);
        var videos = keep.Choices.Concat(other.Choices).Where(c => !c.AudioOnly)
            .GroupBy(c => c.Label)
            .Select(g => g.OrderByDescending(Avc).First())
            .OrderByDescending(Height).ThenByDescending(c => c.SizeBytes ?? 0)
            .Select((c, i) => c with { Id = $"v{i}" })
            .ToList();
        var audio = keep.Choices.FirstOrDefault(c => c.AudioOnly) ?? other.Choices.FirstOrDefault(c => c.AudioOnly);
        if (audio is not null) videos.Add(audio);
        keep.Choices = videos;
        if ((other.Height ?? 0) > (keep.Height ?? 0)) { keep.Width = other.Width; keep.Height = other.Height; }
    }

    private static bool IsAac(HlsVariant v) => v.Codecs?.Contains("mp4a", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// One entry per picture size and frame rate. A master playlist often lists the same picture several times, once for each
    /// kind of sound (stereo AAC, surround AC-3, E-AC-3); only the one that can be saved and joined (AAC) is kept.
    /// </summary>
    public static List<HlsVariant> OneOfEach(IEnumerable<HlsVariant> variants) =>
    [
        .. variants.GroupBy(v => (v.Width, v.Height, Fast: v.FrameRate is > 31))
            // Of one size: the one with AAC sound, then H.264 (it plays on every computer; AV1 and HEVC need newer hardware).
            .Select(g => g.OrderByDescending(IsAac).ThenByDescending(IsAvc).ThenByDescending(v => v.Bandwidth ?? 0).First())
            .OrderByDescending(v => v.Height ?? 0).ThenByDescending(v => v.Bandwidth ?? 0),
    ];

    private static void Apply(DetectedMedia m, HlsMedia media)
    {
        m.Live = media.Live;
        m.DurationSec = media.Live ? null : media.Duration;
        if (media.Drm || media.Segments.Any(s => s.Key is { Method: "OTHER" })) { m.Status = MediaStatus.Protected; return; }
        m.Status = MediaStatus.Ready;
    }

    public static long? Estimate(long? bandwidth, double? seconds) => bandwidth is { } b && seconds is { } s ? (long)(b * s / 8) : null;
}
