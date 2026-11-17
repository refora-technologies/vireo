using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Vireo.Media;
using Vireo.Models;

namespace Vireo;

// Sites whose player describes its video in the page rather than in a playlist the network shows: read that description.
public partial class MainWindow
{
    /// <summary>Bilibili: the page holds its DASH description (every quality and the sound as separate whole files).</summary>
    private const string BilibiliScript = """
        (() => {
          const p = window.__playinfo__;
          const d = p && (p.data || p.result);
          const dash = d && (d.dash || (d.video_info && d.video_info.dash));
          if (!dash || !dash.video) return null;
          const url = x => x.baseUrl || x.base_url || (x.backupUrl || x.backup_url || [])[0];
          return JSON.stringify({
            title: (document.querySelector('h1') || {}).textContent || document.title,
            duration: dash.duration || 0,
            video: dash.video.map(v => ({ id: v.id, url: url(v), w: v.width, h: v.height, bw: v.bandwidth, codecs: v.codecs || '' })),
            audio: (dash.audio || []).map(a => ({ id: a.id, url: url(a), bw: a.bandwidth }))
          });
        })()
        """;

    private async Task CheckSitesAsync(BrowserTab tab, CoreWebView2 core)
    {
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var page)) return;
        if (!page.Host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase) || !page.AbsolutePath.Contains("/video/", StringComparison.OrdinalIgnoreCase)) return;
        // The description appears once the page's script has run; a site that changes video in place is read again.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(1200);
            string json;
            try { json = await core.ExecuteScriptAsync(BilibiliScript); } catch { return; }
            if (json == "null" || JsonSerializer.Deserialize<string>(json) is not { } text) continue;
            var media = BilibiliMedia(text, core.Source);
            if (media is null) return;
            if (tab.Media.Any(m => m.Url == media.Url)) return;
            AddMedia(tab, core, media);
            return;
        }
    }

    /// <summary>The page's description as a ready video: each height once (H.264 first, so the file plays everywhere), with the best sound.</summary>
    internal static DetectedMedia? BilibiliMedia(string json, string pageUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var duration = root.TryGetProperty("duration", out var du) && du.TryGetDouble(out var dd) ? dd : 0;
        var audio = root.GetProperty("audio").EnumerateArray()
            .Select(a => (Url: a.GetProperty("url").GetString() ?? "", Bw: a.GetProperty("bw").GetInt64()))
            .Where(a => a.Url.StartsWith("http")).OrderByDescending(a => a.Bw).FirstOrDefault();
        var videos = root.GetProperty("video").EnumerateArray()
            .Select(v => (Url: v.GetProperty("url").GetString() ?? "", H: v.GetProperty("h").GetInt32(), W: v.GetProperty("w").GetInt32(), Bw: v.GetProperty("bw").GetInt64(), Codecs: v.GetProperty("codecs").GetString() ?? ""))
            .Where(v => v.Url.StartsWith("http") && v.H > 0).ToList();
        if (videos.Count == 0) return null;
        long? Size(long bandwidth) => duration > 0 ? (long)(bandwidth * duration / 8) : null;
        var choices = new List<MediaChoice>();
        foreach (var group in videos.GroupBy(v => v.H).OrderByDescending(g => g.Key))
        {
            var v = group.OrderBy(x => x.Codecs.StartsWith("avc") ? 0 : x.Codecs.StartsWith("hev") || x.Codecs.StartsWith("hvc") ? 1 : 2).First();
            var codec = v.Codecs.StartsWith("avc") ? "H.264" : v.Codecs.StartsWith("hev") || v.Codecs.StartsWith("hvc") ? "HEVC" : "AV1";
            choices.Add(new MediaChoice($"b{v.H}", $"{v.H}p", $"{v.W}×{v.H} · {codec}", Size(v.Bw) + (audio.Url is null ? 0 : Size(audio.Bw) ?? 0),
                v.Url, audio.Url, false, Size(v.Bw), audio.Url is null ? null : Size(audio.Bw), Files: true, Referer: "https://www.bilibili.com/"));
        }
        if (audio.Url is not null) choices.Add(new MediaChoice("baudio", "Audio only", "AAC", Size(audio.Bw), audio.Url, null, true, null, Size(audio.Bw), Files: true, Referer: "https://www.bilibili.com/"));
        return new DetectedMedia
        {
            Url = pageUrl.Split('?')[0], Kind = MediaKind.Dash, Container = "mp4", Title = root.TryGetProperty("title", out var t) ? (t.GetString() ?? "").Trim() : "",
            DurationSec = duration > 0 ? duration : null, Height = videos.Max(v => v.H), Referer = "https://www.bilibili.com/",
            Status = MediaStatus.Ready, Choices = choices,
        };
    }
}
