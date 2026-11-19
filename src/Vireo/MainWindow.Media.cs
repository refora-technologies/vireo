using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Vireo.Controls;
using Vireo.Downloads;
using Vireo.Media;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

// Finding the video and audio a page plays, the Media panel, and the download buttons drawn on the page.
public partial class MainWindow
{
    private static readonly HttpClient MediaHttp = new(new InlinePlaylists.Handler(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.None, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10) })) { Timeout = TimeSpan.FromSeconds(25) };
    private string _overlayScript = "";
    private int _mediaShown;

    private void StartMedia()
    {
        try { _overlayScript = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "overlay.js")); } catch { _overlayScript = ""; }
        _settings.Changed += () => Dispatcher.BeginInvoke(() => PostOverlayConfig());
    }

    internal static bool SelfTesting;

    private static string? s_playOnScript;
    private static string PlayOnScript => s_playOnScript ??= ReadAsset("playon.js").Replace("/*TEST*/false", SelfTesting ? "true" : "false");

    private void AttachMedia(BrowserTab tab, CoreWebView2 core)
    {
        if (SelfTesting) _ = core.AddScriptToExecuteOnDocumentCreatedAsync("window.__vireoSelfTest = true;");
        if (_overlayScript.Length > 0) _ = core.AddScriptToExecuteOnDocumentCreatedAsync(_overlayScript);
        // Keeps playing in a hidden tab or a minimized window (Assets/playon.js; acts on YouTube only).
        if (PlayOnScript.Length > 0) _ = core.AddScriptToExecuteOnDocumentCreatedAsync(PlayOnScript);
        MainWindow O() => tab.Owner ?? this;
        core.WebResourceResponseReceived += (_, e) => O().OnMediaResponse(tab, core, e);
        WatchWorkers(tab, core);
        core.FrameNavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                tab.AddFrameUrl(e.Uri);
        };

        void RegisterFrame(CoreWebView2Frame f)
        {
            lock (tab.Frames) tab.Frames.Add(f);
            f.Destroyed += (_, _) =>
            {
                lock (tab.Frames) tab.Frames.Remove(f);
            };
            f.NavigationStarting += (_, fe) =>
            {
                if (fe.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    tab.AddFrameUrl(fe.Uri);
            };
            f.WebMessageReceived += (_, fe) => O().OnPageMessage(tab, core, fe, f);
            f.FrameCreated += (_, fe) => RegisterFrame(fe.Frame);
        }

        core.FrameCreated += (_, e) => RegisterFrame(e.Frame);
        core.SourceChanged += (_, _) => O().CheckYouTube(tab, core);
        core.NavigationCompleted += (_, _) => O().CheckYouTube(tab, core);
        core.NavigationCompleted += (_, _) => _ = O().CheckSitesAsync(tab, core);
        core.SourceChanged += (_, _) => _ = O().CheckSitesAsync(tab, core);
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            tab.ClearMedia();
            if (tab == O().Active) O().UpdateMediaButton();
        };
    }

    // ---- Finding media ----

    private void OnMediaResponse(BrowserTab tab, CoreWebView2 core, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        using var _crumb = Breadcrumbs.Scope("looking at a response");
        var uri = e.Request.Uri;
        if (!uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        // Scripts, styles, fonts and pictures are most of what a page loads and are never media: skip them before asking for their headers.
        if (MediaClassifier.ClearlyNotMedia(uri)) return;
        var h = e.Response.Headers;
        string? Get(string name) => h.Contains(name) ? h.GetHeader(name) : null;
        var cl = long.TryParse(Get("content-length"), out var l) ? l : (long?)null;
        var status = e.Response.StatusCode;
        var type = Get("content-type");
        var found = MediaClassifier.Classify(uri, status, type, cl, Get("content-range"), Get("content-disposition"));
        // A page's own video element asked for it: media, whatever type its server gave it (a whole file; stream pieces are not
        // loaded by video elements). Too small to be a video is decided once its size is known.
        if (found is null && status is >= 200 and < 300 && tab.MediaElementRequests.ContainsKey(uri) && !MediaClassifier.IsStreamPiece(uri))
        {
            var ext = MediaClassifier.Extension(uri);
            var audioType = (type ?? "").StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
            found = new Classified(MediaKind.File, string.IsNullOrEmpty(type) ? null : type.Split(';')[0].Trim(), ext is "webm" or "mkv" or "mp3" or "m4a" or "ogg" or "opus" ? ext : "mp4",
                MediaClassifier.TotalFromContentRange(Get("content-range")) ?? (status == 206 ? null : cl));
            if (audioType) found = found with { Container = ext.Length is > 0 and <= 4 ? ext : "m4a" };
        }
        // An address that names itself a playlist is read and checked, whatever its answer's type.
        if (found is null && MediaClassifier.NamesAPlaylist(uri))
        {
            AddMedia(tab, core, new DetectedMedia { Url = uri, Kind = uri.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ? MediaKind.Dash : MediaKind.Hls, Container = "mp4", Title = tab.DisplayTitle, Referer = RefererOf(tab, core, e, uri), Quiet = true });
            return;
        }
        if (found is null)
        {
            // Neither the address nor the type gives it away: look inside small, plain answers. A playlist starts "#EXTM3U"
            // or holds an <MPD>; a site's JSON often names the playlist its player is about to load.
            if (tab.Sniffs >= BrowserTab.MaxSniffs) return;
            var sniff = MediaClassifier.ShouldSniff(uri, status, type, cl);
            if (!sniff && !MediaClassifier.ShouldScanForLinks(uri, status, type, cl)) return;
            tab.Sniffs++;
            Task<Stream> body;
            try { body = e.Response.GetContentAsync(); } catch { return; }
            _ = LookInsideAsync(tab, core, uri, RefererOf(tab, core, e, uri), body, links: !sniff);
            return;
        }
        // Keep the playlist the player received: some addresses answer only once.
        if (found.Kind is MediaKind.Hls or MediaKind.Dash && cl is null or < 1024 * 1024)
        {
            try { _ = RememberPlaylistAsync(uri, e.Response.GetContentAsync()); } catch { /* no body to keep */ }
        }
        AddMedia(tab, core, new DetectedMedia
        {
            Url = uri, Kind = found.Kind, Container = found.Container, Mime = found.Mime, SizeBytes = found.Size,
            IsAudio = MediaClassifier.IsAudio(found.Mime, found.Container), Title = tab.DisplayTitle,
            Referer = RefererOf(tab, core, e, uri),
        });
    }

    private static async Task RememberPlaylistAsync(string url, Task<Stream> content)
    {
        try
        {
            await using var body = await content;
            if (body is null) return;
            using var copy = new MemoryStream();
            var chunk = new byte[64 * 1024];
            // The engine's stream reads only into the start of a buffer.
            for (int r; copy.Length < 1024 * 1024 && (r = body.Read(chunk, 0, chunk.Length)) > 0;) copy.Write(chunk, 0, r);
            InlinePlaylists.Remember(url, System.Text.Encoding.UTF8.GetString(copy.ToArray()).TrimStart('\uFEFF'));
        }
        catch { /* nothing kept: the address is asked again, as before */ }
    }

    /// <summary>Who asked for a response: the page or player frame named in its Referer, else the page.</summary>
    private static string RefererOf(BrowserTab tab, CoreWebView2 core, CoreWebView2WebResourceResponseReceivedEventArgs e, string uri)
    {
        string? reqRef = null;
        try { if (e.Request.Headers.Contains("Referer")) reqRef = e.Request.Headers.GetHeader("Referer"); } catch { }
        if (string.IsNullOrWhiteSpace(reqRef) && tab.RequestReferers.TryGetValue(uri, out var capturedRef)) reqRef = capturedRef;
        return !string.IsNullOrWhiteSpace(reqRef) ? reqRef : (core.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? core.Source : tab.Url);
    }

    /// <summary>Reads the start of an answer (a playlist?) or a small JSON answer (stream addresses in it?).</summary>
    private async Task LookInsideAsync(BrowserTab tab, CoreWebView2 core, string uri, string referer, Task<Stream> content, bool links)
    {
        try
        {
            await using var body = await content;
            if (body is null) return;
            if (!links)
            {
                var head = new byte[1024];
                var n = 0;
                // The engine's stream is already in memory, and only reads into the start of a buffer (an offset throws).
                var chunk = new byte[head.Length];
                while (n < head.Length)
                {
                    var r = body.Read(chunk, 0, head.Length - n);
                    if (r <= 0) break;
                    Array.Copy(chunk, 0, head, n, r);
                    n += r;
                }
                if (MediaClassifier.SniffPlaylist(head.AsSpan(0, n)) is not { } kind) return;
                AddMedia(tab, core, new DetectedMedia { Url = uri, Kind = kind, Container = "mp4", Title = tab.DisplayTitle, Referer = referer });
                return;
            }
            var buffer = new char[4 * 1024 * 1024];
            using var reader = new StreamReader(body);
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            foreach (var link in MediaClassifier.FindStreamLinks(new string(buffer, 0, read)))
            {
                var kind = link.Contains(".mpd", StringComparison.OrdinalIgnoreCase) && !link.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? MediaKind.Dash : MediaKind.Hls;
                // Named, not yet loaded: offered only if it answers as a stream (an advert's address that fails is dropped quietly).
                AddMedia(tab, core, new DetectedMedia { Url = link, Kind = kind, Container = "mp4", Title = tab.DisplayTitle, Referer = referer, Quiet = true });
            }
        }
        catch { /* the page went on without us */ }
    }

    /// <summary>A playlist asked for by a service worker or shared worker: the engine shows no response for those, only the request.</summary>
    internal void OnWorkerRequest(BrowserTab tab, CoreWebView2 core, string uri, string? referer)
    {
        var hls = uri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        if (!hls && !uri.Contains(".mpd", StringComparison.OrdinalIgnoreCase)) return;
        AddMedia(tab, core, new DetectedMedia { Url = uri, Kind = hls ? MediaKind.Hls : MediaKind.Dash, Container = "mp4", Title = tab.DisplayTitle, Quiet = true,
            Referer = !string.IsNullOrWhiteSpace(referer) ? referer : core.Source });
    }

    private DetectedMedia? AddMedia(BrowserTab tab, CoreWebView2 core, DetectedMedia m)
    {
        var key = MediaClassifier.Key(m.Url);
        if (tab.ChildKeys.Contains(key) || tab.Media.Any(x => MediaClassifier.Key(x.Url) == key)) return null;
        tab.Media.Add(m);
        tab.RaiseMediaChanged();
        if (tab == Active) UpdateMediaButton();
        // A site's own description arrives complete (its qualities known): nothing to read.
        if (!(m.Status == MediaStatus.Ready && m.Choices.Count > 0)) _ = ProbeAsync(tab, core, m);
        return m;
    }

    private async Task ProbeAsync(BrowserTab tab, CoreWebView2 core, DetectedMedia m)
    {
        try
        {
            var iframesJson = await core.ExecuteScriptAsync(
                "JSON.stringify([...document.querySelectorAll('iframe')].map(f => f.src).filter(s => s && s.startsWith('http')))"
            );
            if (!string.IsNullOrWhiteSpace(iframesJson) && iframesJson != "null")
            {
                var iframeUrls = JsonSerializer.Deserialize<List<string>>(iframesJson);
                if (iframeUrls is not null)
                {
                    foreach (var u in iframeUrls) tab.AddFrameUrl(u);
                }
            }
        }
        catch { }

        lock (tab.FrameUrls)
        {
            foreach (var f in tab.FrameUrls)
            {
                if (!m.CandidateReferers.Contains(f, StringComparer.OrdinalIgnoreCase))
                    m.CandidateReferers.Add(f);
            }
        }
        if (tab.RequestReferers.TryGetValue(m.Url, out var captured) && !string.IsNullOrWhiteSpace(captured))
        {
            if (!m.CandidateReferers.Contains(captured, StringComparer.OrdinalIgnoreCase))
                m.CandidateReferers.Insert(0, captured);
        }

        var cookie = await CookieHeaderAsync(core, m.Url);
        var referer = m.Referer ?? (core.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? core.Source : tab.Url);
        var agent = string.IsNullOrWhiteSpace(core.Settings.UserAgent)
            ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0"
            : core.Settings.UserAgent;

        HttpRequestMessage Request(string url, string? overrideReferer = null)
        {
            // An empty override means "send no referer at all".
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            RequestHeaders.Apply(r, url, agent, overrideReferer ?? referer, cookie, script: true);
            return r;
        }
        await Task.Run(() => MediaProbe.ProbeAsync(m, MediaHttp, Request, CancellationToken.None));
        await Dispatcher.InvokeAsync(() =>
        {
            // The qualities of a stream are the stream: the pieces a player loads from them are not separate videos.
            // The same video reached by another road (a second server, or its master playlist and one of its parts) is shown once.
            // The same video as a second playlist in another codec (an AV1 set and an H.264 set): its sizes join the first entry,
            // H.264 where both have a size. Before the rule below, which would drop the second set whole.
            if (m.Download is null && tab.Media.FirstOrDefault(o => o != m && o.Download is null && MediaProbe.SameVideoOtherCodec(o, m)) is { } twin)
            {
                MediaProbe.MergeChoices(twin, m);
                tab.Media.Remove(m);
            }
            else if (m.Status == MediaStatus.Ready && m.Download is null && tab.Media.Any(o => o != m && SameVideo(o, m))) tab.Media.Remove(m);
            // A stream address only read about (in JSON, or from a worker) that does not answer as one is not offered.
            if (m.Quiet && m.Status is MediaStatus.Failed or MediaStatus.Protected && m.Download is null) tab.Media.Remove(m);
            // A sound or clip that turns out tiny once its size is known is part of the site (a click, a "found it" chime), not something to save.
            if (m.Kind == MediaKind.File && m.Download is null && m.SizeBytes is { } size
                && size < (m.FromPage ? 2_000_000 : m.IsAudio ? MediaClassifier.MinFileBytes : MediaClassifier.MinVideoBytes)) tab.Media.Remove(m);
            // A file only named in the page whose size cannot be learnt is not offered either (it may not be a video at all).
            if (m.FromPage && m.Kind == MediaKind.File && m.SizeBytes is null && m.Download is null) tab.Media.Remove(m);
            // Hover previews of this or other videos (a few seconds each) are not offered once the page's real video is known.
            if (tab.Media.Any(IsMainVideo))
                tab.Media.RemoveAll(x => x.Download is null && x.Kind == MediaKind.File && MediaClassifier.LooksLikePreviewClip(x.Url));
            foreach (var c in m.Choices) tab.ChildKeys.Add(MediaClassifier.Key(c.Url));
            foreach (var c in m.Choices.Where(c => c.AudioUrl is not null)) tab.ChildKeys.Add(MediaClassifier.Key(c.AudioUrl!));
            tab.Media.RemoveAll(x => x != m && tab.ChildKeys.Contains(MediaClassifier.Key(x.Url)));
            tab.RaiseMediaChanged();
            if (tab == Active) UpdateMediaButton();
        });
    }

    /// <summary>A found video that is the page's own: a stream with qualities, or a file that is not a preview clip.</summary>
    private static bool IsMainVideo(DetectedMedia m) =>
        m.Status == MediaStatus.Ready && !m.IsAudio &&
        (m.Kind is MediaKind.Hls or MediaKind.Dash or MediaKind.YouTube || (m.Kind == MediaKind.File && !MediaClassifier.LooksLikePreviewClip(m.Url)));

    /// <summary>Two finds that are one video: both streams, read, same title, same length (or both live), same best quality.</summary>
    internal static bool SameVideo(DetectedMedia a, DetectedMedia b)
    {
        if (a.Status != MediaStatus.Ready || a.Kind is not (MediaKind.Hls or MediaKind.Dash) || b.Kind is not (MediaKind.Hls or MediaKind.Dash)) return false;
        if (!string.Equals(a.Title, b.Title, StringComparison.Ordinal) || a.Live != b.Live) return false;
        if (!a.Live && (a.DurationSec is not { } da || b.DurationSec is not { } db || Math.Abs(da - db) > 1.5)) return false;
        static string? Best(DetectedMedia m) => m.Choices.FirstOrDefault(c => !c.AudioOnly)?.Label;
        // A master lists several sizes; one of its parts lists one. The part is covered when the master has its size.
        var labelsA = a.Choices.Select(c => c.Label).ToHashSet();
        return Best(a) == Best(b) || b.Choices.All(c => labelsA.Contains(c.Label) || c.Label == "Best available");
    }

    internal void RetryProbe(BrowserTab tab, DetectedMedia m)
    {
        m.Status = MediaStatus.Probing;
        m.Error = null;
        m.Choices.Clear();
        tab.RaiseMediaChanged();
        if (tab.View?.CoreWebView2 is { } core)
            _ = ProbeAsync(tab, core, m);
    }

    private static async Task<string?> CookieHeaderAsync(CoreWebView2 core, string url)
    {
        try { return string.Join("; ", (await core.CookieManager.GetCookiesAsync(url)).Select(c => $"{c.Name}={c.Value}")); }
        catch { return null; }
    }

    /// <summary>YouTube plays through its own player, so its videos are found by the page address, not the network.</summary>
    private void CheckYouTube(BrowserTab tab, CoreWebView2 core)
    {
        var id = YouTube.VideoId(core.Source);
        // The page can change video without reloading: drop the old video's entry.
        if (tab.Media.RemoveAll(m => m.Kind == MediaKind.YouTube && m.VideoKey != id) > 0)
        {
            tab.RaiseMediaChanged();
            if (tab == Active) UpdateMediaButton();
        }
        if (id is null || tab.Media.Any(m => m.Kind == MediaKind.YouTube && m.VideoKey == id)) return;
        AddMedia(tab, core, new DetectedMedia { Url = $"https://www.youtube.com/watch?v={id}", Kind = MediaKind.YouTube, VideoKey = id, Title = tab.DisplayTitle });
    }

    // ---- The toolbar button ----

    private void UpdateMediaButton()
    {
        using var _crumb = Breadcrumbs.Scope("media button");
        var count = Active?.Media.Count(m => m.Showable) ?? 0;
        MediaCount.Text = count > 0 ? count.ToString() : "";
        MediaCountHost.Visibility = Visibility.Collapsed; // the pill says it in words
        MediaIcon.Foreground = count > 0 ? UiKit.Brush("Accent") : UiKit.Brush("Text2");
        var audioOnly = count > 0 && Active!.Media.Where(m => m.Showable).All(m => m.IsAudio);
        MediaPillText.Text = count switch { 0 => "", 1 => audioOnly ? "Save audio" : "Save video", _ => $"{count} to save" };
        MediaPillText.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaPill.Background = count > 0 ? UiKit.Brush("AccentSoft") : System.Windows.Media.Brushes.Transparent;
        // The best quality on offer, as a small label beside the words: "4K", "1080p".
        var best = count == 0 ? 0 : Active!.Media.Where(m => m.Showable).SelectMany(m => m.Choices.Select(c => QualityOf(c.Label)).Append(m.Height ?? 0)).DefaultIfEmpty(0).Max();
        MediaQualityText.Text = best >= 2160 ? "4K" : best >= 1440 ? "2K" : best > 0 ? $"{best}p" : "";
        MediaQuality.Visibility = MediaQualityText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaButton.ToolTip = count > 0 ? $"{count} video or audio found on this page" : "Videos and audio on this page";
        if (count > _mediaShown) PopIn(MediaPill);
        _mediaShown = count;
    }

    internal void ShowMedia() => OpenDrawer("Media", new MediaPanel(this));
    private void OnMedia(object sender, RoutedEventArgs e) => ToggleDrawer("Media", ShowMedia);

    // ---- Starting a download ----

    internal async Task StartChoiceAsync(BrowserTab tab, DetectedMedia item, MediaChoice choice)
    {
        var core = tab.View?.CoreWebView2;
        var referer = choice.Referer ?? item.Referer ?? (core?.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? core.Source : tab.Url);
        var cookie = core is null ? null : await CookieHeaderAsync(core, choice.Url);
        var agent = core?.Settings.UserAgent;
        var title = NiceTitle(string.IsNullOrWhiteSpace(item.Title) ? tab.DisplayTitle : item.Title, choice.Url);
        var stem = FileNaming.Sanitize(title);
        var quality = choice.AudioOnly ? "" : item.Kind == MediaKind.File ? "" : $" ({choice.Label})";

        if (item.Kind == MediaKind.YouTube)
        {
            // YouTube wants clean requests: no page cookies or Referer. Video and audio come as two files and are joined.
            var yt = FileNaming.Sanitize(string.IsNullOrWhiteSpace(item.Title) ? title : item.Title);
            var ytRequest = choice.AudioOnly
                ? new DownloadRequest(choice.Url, $"{yt}.m4a")
                : new DownloadRequest(choice.Url, $"{yt}{quality}.mp4", Pair: new PairSpec(choice.Url, choice.VideoSize ?? 0, choice.AudioUrl!, choice.AudioSize ?? 0));
            var started = Downloads.Add(ytRequest);
            item.Download = started;
            tab.RaiseMediaChanged();
            ShowToast("Downloading", started.FileName, ShowDownloads);
            return;
        }

        DownloadRequest request;
        if (item.Kind == MediaKind.Dash && choice.Files)
        {
            // Whole files for the picture and the sound: fetched side by side, then joined.
            request = choice.AudioUrl is null || choice.AudioOnly
                ? new DownloadRequest(choice.Url, $"{stem}{quality}.{(choice.AudioOnly ? "m4a" : "mp4")}", referer, agent, cookie)
                : new DownloadRequest(choice.Url, $"{stem}{quality}.mp4", referer, agent, cookie, Pair: new PairSpec(choice.Url, choice.VideoSize ?? 0, choice.AudioUrl, choice.AudioSize ?? 0));
        }
        else if (item.Kind == MediaKind.File)
        {
            request = new DownloadRequest(choice.Url, $"{stem}.{item.Container}", referer, agent, cookie);
        }
        else
        {
            var estimate = (choice.SizeBytes ?? 0) + (choice.AudioUrl is not null && item.DurationSec is { } d ? (long)(d * 128_000 / 8) : 0);
            request = new DownloadRequest(choice.Url, $"{stem}{quality}.mp4", referer, agent, cookie,
                Stream: new StreamSpec(choice.Url, choice.AudioUrl, item.Live, estimate));
        }
        var added = Downloads.Add(request);
        item.Download = added;
        tab.RaiseMediaChanged();
        ShowToast(item.Live ? "Recording" : "Downloading", added.FileName.Length > 0 ? added.FileName : stem, ShowDownloads);
    }

    private static int QualityOf(string label)
    {
        var m = System.Text.RegularExpressions.Regex.Match(label, @"(\d{3,4})p");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>A name to save under. A page title that is really an address (the page had none) gives way to the file's own name.</summary>
    internal static string NiceTitle(string title, string mediaUrl)
    {
        var looksLikeAddress = title.Length == 0 || title.Contains("://") || System.Net.IPAddress.TryParse(title, out _) || (!title.Contains(' ') && title.Contains('.') && Uri.CheckHostName(title) != UriHostNameType.Unknown);
        if (!looksLikeAddress) return title;
        if (!Uri.TryCreate(mediaUrl.Split('#')[0], UriKind.Absolute, out var u)) return title;
        // the file's name, or the folder it sits in when the file is a generic "index"/"master"/"playlist"
        var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(parts[i]);
            if (name.Length > 1 && name.ToLowerInvariant() is not ("index" or "master" or "playlist" or "manifest" or "prog_index" or "chunklist" or "video" or "hls" or "dash" or "stream" or "720" or "1080" or "480" or "360"))
                return name;
        }
        return u.Host + " video " + DateTime.Now.ToString("yyyy-MM-dd HH.mm");
    }

    // ---- The buttons on the page ----

    private void PostOverlayConfig() => PostOverlayConfig(null, null, null);

    private void PostOverlayConfig(BrowserTab? singleTab, CoreWebView2? singleCore, CoreWebView2Frame? singleFrame)
    {
        if (singleTab is not null && singleCore is not null)
        {
            SendConfigToTab(singleTab, singleCore, singleFrame);
            return;
        }
        foreach (var tab in _tabs)
        {
            if (tab.View?.CoreWebView2 is { } core)
                SendConfigToTab(tab, core);
        }
    }

    private void SendConfigToTab(BrowserTab tab, CoreWebView2 core, CoreWebView2Frame? targetFrame = null)
    {
        var host = Uri.TryCreate(core.Source, UriKind.Absolute, out var u) ? u.Host : "";
        var off = !_settings.ShowDownloadButtons || _settings.NoButtonSites.Contains(host, StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(new { t = "dl-config", mode = off ? "off" : _settings.DownloadButtonMode });
        if (targetFrame is not null)
        {
            try { targetFrame.PostWebMessageAsJson(json); } catch { }
            return;
        }
        try { core.PostWebMessageAsJson(json); } catch { }
        lock (tab.Frames)
        {
            foreach (var f in tab.Frames)
            {
                try { f.PostWebMessageAsJson(json); } catch { }
            }
        }
    }

    private bool OnMediaMessage(BrowserTab tab, CoreWebView2 core, CoreWebView2Frame? frame, JsonElement root, string type)
    {
        switch (type)
        {
            case "dl-hello":
                PostOverlayConfig(tab, core, frame);
                return true;
            case "els":
                var anyChanged = false;
                if (frame is null && root.TryGetProperty("og", out var og) && og.GetString() is { Length: > 0 } ogImage
                    && Uri.TryCreate(ogImage, UriKind.Absolute, out var ogUri) && ogUri.Scheme is "http" or "https" && tab.PageImage != ogUri.ToString())
                {
                    tab.PageImage = ogUri.ToString();
                    anyChanged = true;
                }
                foreach (var el in root.GetProperty("items").EnumerateArray())
                {
                    var src = el.GetProperty("src").GetString() ?? "";
                    if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue; // blob: players are found through the network instead
                    var key = MediaClassifier.Key(src);
                    var known = tab.Media.FirstOrDefault(x => MediaClassifier.Key(x.Url) == key);
                    var poster = el.GetProperty("poster").GetString();
                    var duration = el.GetProperty("duration").GetDouble();
                    var audio = el.GetProperty("audio").GetBoolean();
                    if (known is null)
                    {
                        var ext = MediaClassifier.Extension(src);
                        var isHls = ext == "m3u8" || src.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
                        var isDash = ext == "mpd" || src.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
                        var kind = isHls ? MediaKind.Hls : isDash ? MediaKind.Dash : MediaKind.File;
                        var page = root.TryGetProperty("page", out var p) ? p.GetString() : null;
                        known = AddMedia(tab, core, new DetectedMedia { Url = src, Kind = kind, Container = ext is "" or "m3u8" or "mpd" ? "mp4" : ext, IsAudio = audio, Title = tab.DisplayTitle,
                            Referer = frame is not null && page is not null && page.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? page : null });
                    }
                    if (known is null) continue;
                    if (!string.IsNullOrEmpty(poster)) known.Poster = poster;
                    if (duration > 0 && known.Kind == MediaKind.File) known.DurationSec = duration;
                    if (el.GetProperty("h").GetInt32() is > 0 and var h) { known.Height = h; known.Width = el.GetProperty("w").GetInt32(); }
                    anyChanged = true;
                }
                if (anyChanged) tab.RaiseMediaChanged();
                return true;
            case "dl-open":
                // Read now: the message is gone by the time the second answer below is sent.
                var openedSrc = root.GetProperty("src").GetString() ?? "";
                ReplyMenu(tab, core, frame, openedSrc);
                // Streams are still being read when the button is first pressed: answer again when they are ready.
                if (tab.Media.Any(m => m.Status == MediaStatus.Probing))
                {
                    void Again()
                    {
                        if (tab.Media.All(m => m.Status != MediaStatus.Probing))
                        {
                            tab.MediaChanged -= Again;
                            ReplyMenu(tab, core, frame, openedSrc);
                        }
                    }
                    tab.MediaChanged += Again;
                }
                return true;
            case "page-links":
                // Addresses written into the page: each is read; only those that answer as a video are shown.
                var linkPage = root.TryGetProperty("page", out var lp) ? lp.GetString() ?? "" : "";
                if (!linkPage.StartsWith("http", StringComparison.OrdinalIgnoreCase)) linkPage = core.Source;
                foreach (var l in root.GetProperty("links").EnumerateArray().Take(30))
                {
                    var link = l.GetProperty("url").GetString() ?? "";
                    if (!Uri.TryCreate(link, UriKind.Absolute, out var lu) || lu.Scheme is not ("http" or "https") || MediaClassifier.IsStreamPiece(link)) continue;
                    var playlist = MediaClassifier.NamesAPlaylist(link);
                    var linkExt = MediaClassifier.Extension(link);
                    AddMedia(tab, core, new DetectedMedia
                    {
                        Url = link, Kind = playlist ? (lu.AbsolutePath.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ? MediaKind.Dash : MediaKind.Hls) : MediaKind.File,
                        Container = playlist ? "mp4" : linkExt is "webm" or "mkv" or "mov" ? linkExt : "mp4", Title = tab.DisplayTitle, Referer = linkPage, Quiet = true, FromPage = true,
                    });
                }
                return true;
            case "blob-playlist":
                // A player wrote a playlist in script and gave the video a blob: address for it: only its pieces travel the network.
                var plText = root.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                var plFrom = root.TryGetProperty("page", out var pg) ? pg.GetString() ?? "" : "";
                var plBlob = root.TryGetProperty("blob", out var bl) ? bl.GetString() : null;
                if (!plFrom.StartsWith("http", StringComparison.OrdinalIgnoreCase)) plFrom = core.Source;
                if (InlinePlaylists.Add(plText, plFrom, plBlob) is { } address)
                {
                    var already = tab.Media.FirstOrDefault(x => x.Url == address);
                    if (already is null)
                        AddMedia(tab, core, new DetectedMedia { Url = address, Kind = address.EndsWith(".mpd") ? MediaKind.Dash : MediaKind.Hls, Container = "mp4", Title = tab.DisplayTitle, Referer = plFrom });
                }
                return true;
            case "dl-pick":
                var itemId = root.GetProperty("item").GetString();
                var choiceId = root.GetProperty("choice").GetString();
                var item = tab.Media.FirstOrDefault(m => m.Id == itemId);
                var choice = item?.Choices.FirstOrDefault(c => c.Id == choiceId);
                if (item is not null && choice is not null) _ = StartChoiceAsync(tab, item, choice);
                return true;
        }
        return false;
    }

    private static string Sz(MediaChoice c, DetectedMedia m) => c.SizeBytes is { } b ? (m.Kind == MediaKind.File ? "" : "~") + Format.Bytes(b) : "";

    private void ReplyMenu(BrowserTab tab, CoreWebView2 core, CoreWebView2Frame? frame, string src)
    {
        var key = src.Length > 0 ? MediaClassifier.Key(src) : "";
        var items = tab.Media.OrderByDescending(m => key.Length > 0 && MediaClassifier.Key(m.Url) == key).ThenBy(m => m.Kind == MediaKind.File ? 1 : 0).ToList();
        var ready = items.Where(m => m.Status == MediaStatus.Ready && m.Choices.Count > 0).Take(3).ToList();
        object reply;
        if (ready.Count > 0)
        {
            reply = new
            {
                t = "dl-menu",
                sections = ready.Select(m => new
                {
                    id = m.Id,
                    head = m.Live ? "Live stream" : m.IsAudio ? "Audio" : "Video",
                    title = m.DurationSec is { } d && d > 0 ? $"{tab.DisplayTitle} · {Format.Duration(d)}" : tab.DisplayTitle,
                    choices = m.Choices.Take(10).Select(c => new { id = c.Id, label = c.Label, detail = c.Detail, size = Sz(c, m), audio = c.AudioOnly }),
                }).ToArray(),
            };
        }
        else if (items.Any(m => m.Status == MediaStatus.Probing)) reply = new { t = "dl-menu", note = "Reading the stream…" };
        else if (items.Any(m => m.Status == MediaStatus.Protected)) reply = new { t = "dl-menu", note = "This video is protected (DRM). Vireo does not download protected content.", warn = true };
        else if (items.Any(m => m.Status == MediaStatus.Failed)) reply = new { t = "dl-menu", note = items.First(m => m.Status == MediaStatus.Failed).Error ?? "Could not read this video.", warn = true };
        else reply = new { t = "dl-menu", note = "Nothing to save yet. Press play for a moment: Vireo sees a video when the page asks for it." };

        var replyJson = JsonSerializer.Serialize(reply);
        if (frame != null)
        {
            try { frame.PostWebMessageAsJson(replyJson); } catch { }
        }
        try { core.PostWebMessageAsJson(replyJson); } catch { }
        lock (tab.Frames)
        {
            foreach (var f in tab.Frames)
            {
                if (f != frame)
                {
                    try { f.PostWebMessageAsJson(replyJson); } catch { }
                }
            }
        }
    }
}
