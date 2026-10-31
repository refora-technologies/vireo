using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Vireo.Downloads;
using Vireo.Media;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

// The start page: live download speed, what is downloading, your sites, what was blocked, and a box to download any link.
public partial class MainWindow
{
    private DispatcherTimer? _homeTimer;
    private readonly List<double> _speeds = [];
    private const int SpeedSamples = 60;
    private string _homeTopKey = "";

    private void StartHome()
    {
        _homeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _homeTimer.Tick += (_, _) => { SampleSpeed(); SendHome(); };
        _homeTimer.Start();
        Closed += (_, _) => _homeTimer?.Stop();
    }

    private void SampleSpeed()
    {
        var speed = Downloads.Items.Where(i => i.State == DownloadState.Downloading).Sum(i => i.Speed);
        _speeds.Add(speed);
        if (_speeds.Count > SpeedSamples) _speeds.RemoveAt(0);
    }

    internal bool HandleHomeMessage(BrowserTab tab, CoreWebView2 core, JsonElement root, string type)
    {
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var from) || from.Host != StartHost) return false;
        switch (type)
        {
            case "home-hello":
                SendHome(core);
                return true;
            case "site-add" or "site-edit":
                // Validated here: only web addresses reach the start page (it builds links from them).
                var title = (root.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "").Trim();
                var raw = (root.TryGetProperty("url", out var uu) ? uu.GetString() ?? "" : "").Trim();
                if (!Uri.TryCreate(raw.Contains("://") ? raw : "https://" + raw, UriKind.Absolute, out var site) || site.Scheme is not ("http" or "https") || !site.Host.Contains('.')) return true;
                if (title.Length == 0) title = site.Host.StartsWith("www.") ? site.Host[4..] : site.Host;
                if (title.Length > 40) title = title[..40];
                var old = type == "site-edit" && root.TryGetProperty("old", out var oo) ? oo.GetString() : null;
                var at = old is null ? -1 : _settings.PinnedSites.FindIndex(p => p.Url == old);
                var entry = new PinnedSite(title, site.ToString());
                _settings.HiddenSites.Remove(SiteIcons.Key(entry.Url) ?? "");
                _settings.PinnedSites.RemoveAll(p => p.Url == entry.Url && p.Url != old);
                if (at >= 0) _settings.PinnedSites[at] = entry;
                else if (old is not null && _settings.PinnedSites.Count < 10) _settings.PinnedSites.Add(entry); // editing a most-visited site pins it
                else if (_settings.PinnedSites.Count < 10) _settings.PinnedSites.Add(entry);
                if (old is not null && at < 0) _settings.HiddenSites.Add(SiteIcons.Key(old) ?? old);
                _settings.PinnedSitesChangedAt = DateTime.UtcNow;
                _settings.Commit();
                SendHome(core);
                return true;
            case "site-remove":
                var gone = root.TryGetProperty("url", out var gg) ? gg.GetString() ?? "" : "";
                if (_settings.PinnedSites.RemoveAll(p => p.Url == gone) == 0 && SiteIcons.Key(gone) is { } key && !_settings.HiddenSites.Contains(key))
                    _settings.HiddenSites.Add(key);
                _settings.PinnedSitesChangedAt = DateTime.UtcNow;
                _settings.Commit();
                SendHome(core);
                return true;
            case "open-panel":
                ShowDownloads();
                return true;
            case "quick-dl":
                var link = root.GetProperty("url").GetString() ?? "";
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    var (ok, text) = await QuickDownloadAsync(link);
                    try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "quick-dl-result", ok, text })); } catch { /* the page went away */ }
                });
                return true;
        }
        return false;
    }

    /// <summary>Only start pages that are on screen get the numbers: hidden ones would only waste the effort.</summary>
    private void SendHome(CoreWebView2? only = null)
    {
        if (_private && only is null) return;
        var targets = only is not null ? [only] : _tabs.Where(t => t == Active && t.IsStartPage).Select(t => t.View?.CoreWebView2).OfType<CoreWebView2>().ToList();
        if (targets.Count == 0) return;
        using var _crumb = Breadcrumbs.Scope("start page");

        var items = Downloads.Items.Where(i => i.Parent is null && i.State != DownloadState.Cancelled).ToList();
        var active = items.Count(i => i.State == DownloadState.Downloading);
        var queued = items.Count(i => i.State == DownloadState.Queued);
        var done = items.Count(i => i.State == DownloadState.Completed && i.CompletedAt?.Date == DateTime.Today);
        var speed = _speeds.Count > 0 ? _speeds[^1] : 0;
        var text = Format.Speed(speed);
        var (number, unit) = text.Length == 0 ? ("0", "KB/s") : (text[..text.IndexOf(' ')], text[(text.IndexOf(' ') + 1)..]);
        var shown = items.OrderByDescending(i => i.State is DownloadState.Downloading or DownloadState.Queued or DownloadState.Paused).ThenByDescending(i => i.CompletedAt ?? i.CreatedAt).Take(5)
            .Select(i => new
            {
                name = i.FileName.Length > 0 ? i.FileName : i.Url,
                pct = i.State == DownloadState.Completed ? 1.0 : i.Total > 0 ? Math.Clamp((double)i.Downloaded / i.Total, 0, 1) : 0,
                state = i.State switch { DownloadState.Completed => "done", DownloadState.Paused => "paused", DownloadState.Failed => "failed", _ => "" },
                detail = i.State switch
                {
                    DownloadState.Completed => $"{Format.Bytes(i.Total)} · {Format.When(i.CompletedAt ?? i.CreatedAt)}",
                    DownloadState.Failed => i.Error ?? "Failed",
                    DownloadState.Paused => $"Paused · {Format.Bytes(i.Downloaded)}",
                    DownloadState.Queued => "Waiting for a free slot",
                    _ => string.Join(" · ", new[] { Format.Bytes(i.Downloaded) + (i.Total > 0 ? " / " + Format.Bytes(i.Total) : ""), Format.Speed(i.Speed), i.Speed > 1 && i.Total > 0 ? Format.Eta(TimeSpan.FromSeconds((i.Total - i.Downloaded) / i.Speed)) : "", i.ActiveConnections > 1 ? $"×{i.ActiveConnections}" : "" }.Where(s => s.Length > 0)),
                },
            }).ToList();
        // The person's own sites first, then the ones they visit most (minus any they took off), ten in all.
        var pinned = _settings.PinnedSites.Select(p => new { title = p.Title, url = p.Url, pinned = true }).ToList();
        var pinnedHosts = pinned.Select(p => SiteIcons.Key(p.url)).ToHashSet();
        var top = pinned.Concat(_library.History.Where(h => !h.Url.Contains(StartHost)).GroupBy(h => Uri.TryCreate(h.Url, UriKind.Absolute, out var u) ? u.Host : h.Url)
            .Select(g => (Site: g.OrderByDescending(h => h.Visits).First(), Visits: g.Sum(h => h.Visits))).OrderByDescending(x => x.Visits)
            .Select(x => new { title = SiteName(x.Site), url = Uri.TryCreate(x.Site.Url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) + "/" : x.Site.Url, pinned = false })
            .Where(x => !pinnedHosts.Contains(SiteIcons.Key(x.url)) && !_settings.HiddenSites.Contains(SiteIcons.Key(x.url) ?? x.url))
            .Take(Math.Max(0, 10 - pinned.Count))).Take(10).ToList();
        // The sites (with their icons) only travel when they change or a page has just opened: they are the heavy part.
        var topKey = string.Join("|", top.Select(t => t.title + "@" + t.url + (t.pinned ? "!" : "") + (SiteIcons.DataUri(t.url) is null ? "" : "*")));
        var sendTop = only is not null || topKey != _homeTopKey;
        _homeTopKey = topKey;
        var topWithIcons = sendTop ? top.Select(t => new { t.title, t.url, t.pinned, icon = SiteIcons.DataUri(t.url) }).ToList() : null;

        var json = JsonSerializer.Serialize(new
        {
            t = "home", speedNumber = number, speedUnit = unit, speeds = _speeds, active, queued, done, downloads = shown, top = topWithIcons,
            ads = BrowserTab.TotalAds, trackers = BrowserTab.TotalTrackers, found = _tabs.Sum(t => t.Media.Count(m => m.Status == MediaStatus.Ready)),
            engine = SearchEngines.For(_settings.SearchTemplate)?.Name ?? "Search",
        });
        foreach (var core in targets) try { core.PostWebMessageAsJson(json); } catch { /* going away */ }
    }

    private static string SiteName(HistoryEntry h)
    {
        var host = Uri.TryCreate(h.Url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : h.Url;
        var title = h.Title.Split([" - ", " | ", " — ", " · "], StringSplitOptions.None)[0].Trim();
        return title.Length is > 0 and < 24 ? title : host;
    }

    /// <summary>
    /// The start page's "Download from a link": a stream or a video page is read and its best quality saved (joined into one
    /// file when picture and sound come apart); anything else is downloaded as a file.
    /// </summary>
    internal async Task<(bool Ok, string Text)> QuickDownloadAsync(string text)
    {
        var raw = text.Trim().Trim('"');
        if (!Uri.TryCreate(raw.Contains("://") ? raw : "https://" + raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.Host.Contains('.') && uri.Host != "localhost" && !System.Net.IPAddress.TryParse(uri.Host, out _))
            return (false, "That does not look like a web address.");
        if (Active is not { View.CoreWebView2: { } core } tab) return (false, "Open a tab first.");
        var url = uri.ToString();
        var videoId = YouTube.VideoId(url);
        var ext = MediaClassifier.Extension(url);
        MediaKind? kind = videoId is not null ? MediaKind.YouTube
            : ext == "m3u8" || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? MediaKind.Hls
            : ext == "mpd" || url.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ? MediaKind.Dash : null;

        if (kind is null)
        {
            var item = Downloads.Add(new DownloadRequest(url, null, null, core.Settings.UserAgent, null));
            ShowToast("Downloading", uri.Host, ShowDownloads);
            return (true, $"Downloading from {uri.Host}…");
        }

        var media = AddMedia(tab, core, new DetectedMedia { Url = kind == MediaKind.YouTube ? $"https://www.youtube.com/watch?v={videoId}" : url, Kind = kind.Value, VideoKey = videoId, Title = uri.Host, Referer = "" });
        if (media is null) return (false, "That link is already on the list in the Media panel.");
        for (var i = 0; i < 300 && media.Status == MediaStatus.Probing; i++) await Task.Delay(100);
        switch (media.Status)
        {
            case MediaStatus.Protected: return (false, "This video is protected (DRM). Vireo does not download protected video.");
            case MediaStatus.Failed: return (false, media.Error ?? "The link could not be read.");
            case MediaStatus.Probing: return (false, "The link took too long to answer.");
        }
        var best = media.Choices.FirstOrDefault(c => !c.AudioOnly) ?? media.Choices.FirstOrDefault();
        if (best is null) return (false, "Nothing to download was found at that link.");
        await StartChoiceAsync(tab, media, best);
        return (true, $"Downloading {best.Label}{(media.Live ? " (recording live)" : "")}: {(media.Title.Length > 0 ? media.Title : uri.Host)}");
    }
}
