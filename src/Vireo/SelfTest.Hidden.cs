using System.Net.Http;
using System.Text;
using Vireo.Media;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>
    /// Streams that hide from a downloader: a playlist named like a picture, one from an API address sent as plain text, and one
    /// only named in a site's JSON (the player has not asked for it yet). Each must be found and read as a stream.
    /// </summary>
    private async Task RunHiddenStreamStepsAsync()
    {
        string playlist;
        try
        {
            using var http = new HttpClient();
            // the test media's playlist, with its pieces made absolute so it can be served from anywhere
            playlist = string.Join('\n', (await http.GetStringAsync(baseUrl + "/hls/720/index.m3u8")).Split('\n')
                .Select(l => l.Trim()).Select(l => l.Length > 0 && !l.StartsWith('#') ? $"{baseUrl}/hls/720/{l}" : l));
        }
        catch
        {
            Console.WriteLine("SKIP  hidden stream steps (test media not available)");
            return;
        }

        using var site = new PageSite();
        var body = Encoding.UTF8.GetBytes(playlist);
        site.Add("/s/poster.png", "application/vnd.apple.mpegurl", body);       // a playlist named like a picture
        site.Add("/api/source", "text/plain", body);                              // no telling address, no telling type
        var link = $"{baseUrl}/hls/720/index.m3u8".Replace("/", "\\/");
        site.Add("/api/episode", "application/json", Encoding.UTF8.GetBytes($$"""{"title":"Episode 2","sources":[{"file":"{{link}}","label":"auto"}]}"""));
        site.Html("/picture.html", "<!doctype html><title>Picture name</title><script>fetch('/s/poster.png').then(r => r.text())</script>");
        site.Html("/api.html", "<!doctype html><title>Plain answer</title><script>fetch('/api/source?id=7').then(r => r.text())</script>");
        site.Html("/json.html", "<!doctype html><title>Named in JSON</title><script>fetch('/api/episode').then(r => r.json())</script>");

        var seen = "";
        async Task<DetectedMedia?> Visit(string path, string title)
        {
            var tab = await w.OpenTabAsync(site.Base + path);
            await WaitFor(() => !tab.IsLoading && tab.Title == title);
            var m = await WaitMedia(tab, m => m.Kind == MediaKind.Hls && m.Status == MediaStatus.Ready, 15000);
            seen = $"looked inside {tab.Sniffs}; " + string.Join(", ", tab.Media.Select(x => $"{x.Kind}:{x.Status}:{x.Url}:{x.Error}"));
            return m;
        }

        var a = await Visit("/picture.html", "Picture name");
        Check("a playlist named like a picture is found by its type", a is { Status: MediaStatus.Ready } && a.Choices.Count > 0, $"{a?.Url} {a?.Status}");
        var b = await Visit("/api.html", "Plain answer");
        Check("a playlist sent as plain text from an API address is found by its first bytes", b is { Status: MediaStatus.Ready } && b.Url.Contains("/api/source"), $"{b?.Url} {b?.Status} | {seen}");
        var c = await Visit("/json.html", "Named in JSON");
        Check("a stream only named in a site's JSON is offered before the player asks for it", c is { Status: MediaStatus.Ready } && c.Url == $"{baseUrl}/hls/720/index.m3u8", $"{c?.Url} {c?.Status}");

        // Detection beyond file types: a playlist address served under an odd type, an address only written in a player's
        // script (never fetched), and a whole DASH description carried in the page (as Facebook does).
        site.Add("/cdn/v/master.m3u8", "application/json", body);
        site.Html("/oddtype.html", "<!doctype html><title>Odd type</title><script>fetch('/cdn/v/master.m3u8').then(r => r.text())</script>");
        var ot = await w.OpenTabAsync(site.Base + "/oddtype.html");
        await WaitFor(() => !ot.IsLoading && ot.Title == "Odd type");
        var odd = await WaitMedia(ot, m => m.Url.EndsWith("/cdn/v/master.m3u8") && m.Status == MediaStatus.Ready, 15000);
        Check("a playlist address answered under an odd type (JSON) is read", odd is { Status: MediaStatus.Ready }, string.Join(", ", ot.Media.Select(x => $"{x.Kind}:{x.Status}:{x.Url}")));

        var hlsLink = $"{baseUrl}/hls/360/index.m3u8".Replace("/", "\\/");
        site.Html("/player.html", $$"""<!doctype html><title>Player config</title><div id=p></div><script>window.playerConfig = {"sources":{"hls":"{{hlsLink}}"},"autoplay":false};</script>""");
        var pc = await w.OpenTabAsync(site.Base + "/player.html");
        await WaitFor(() => !pc.IsLoading && pc.Title == "Player config");
        var cfg = await WaitMedia(pc, m => m.Url == $"{baseUrl}/hls/360/index.m3u8" && m.Status == MediaStatus.Ready, 20000);
        Check("a stream only written in the player's script is offered before pressing play", cfg is { Status: MediaStatus.Ready }, string.Join(", ", pc.Media.Select(x => $"{x.Kind}:{x.Status}:{x.Url}")));

        string mpd;
        using (var h = new HttpClient()) mpd = await h.GetStringAsync(baseUrl + "/dash-real/manifest.mpd");
        mpd = System.Text.RegularExpressions.Regex.Replace(mpd, @"(<MPD\b[^>]*>)", $"$1<BaseURL>{baseUrl}/dash-real/</BaseURL>");
        var escaped = System.Text.Json.JsonSerializer.Serialize(mpd);
        site.Html("/fb.html", "<!doctype html><title>Embedded DASH</title><script>var data = {\"video\":{\"dash_manifest_xml_string\":" + escaped + "}};</script>");
        var fb = await w.OpenTabAsync(site.Base + "/fb.html");
        await WaitFor(() => !fb.IsLoading && fb.Title == "Embedded DASH");
        var dash = await WaitMedia(fb, m => m.Kind == MediaKind.Dash && m.Status == MediaStatus.Ready, 20000);
        Check("a DASH description carried inside the page (Facebook's way) is read with its qualities", dash is { Status: MediaStatus.Ready } && dash.Choices.Any(c => c.Label == "720p"),
            string.Join(", ", fb.Media.Select(x => $"{x.Kind}:{x.Status}:{x.Error}:{string.Join("/", x.Choices.Select(c => c.Label))}")));

        // A player that fetches its playlist from a Web Worker (Twitch's and Kick's do): the engine reports nothing a worker loads.
        site.Add("/worker/stream.m3u8", "application/vnd.apple.mpegurl", body);
        site.Add("/player-worker.js", "text/javascript", Encoding.UTF8.GetBytes("setTimeout(() => fetch('/worker/stream.m3u8').then(r => r.text()).then(t => postMessage(t.length)), 1500);"));
        site.Html("/worker.html", "<!doctype html><title>Worker player</title><script>const w = new Worker('/player-worker.js'); w.onmessage = e => document.title = 'Worker player ' + e.data;</script>");
        var wt = await w.OpenTabAsync(site.Base + "/worker.html");
        await WaitFor(() => !wt.IsLoading && wt.Title.StartsWith("Worker player"));
        var fromWorker = await WaitMedia(wt, m => m.Url.EndsWith("/worker/stream.m3u8") && m.Status == MediaStatus.Ready, 15000);
        Check("a playlist a player fetches from a Web Worker is found", fromWorker is { Status: MediaStatus.Ready }, string.Join(", ", wt.Media.Select(x => $"{x.Kind}:{x.Status}:{x.Url}")) + " | " + wt.Title);

        // A player that writes the playlist in script: the page hands the video a blob: address, only pieces cross the network.
        var json = System.Text.Json.JsonSerializer.Serialize(playlist);
        site.Html("/blob.html", $"<!doctype html><title>Made in script</title><body><video id=v width=480 height=270 muted></video><script>const text = {json}; document.getElementById('v').src = URL.createObjectURL(new Blob([text], {{ type: 'application/vnd.apple.mpegurl' }}));</script></body>");
        var tab = await w.OpenTabAsync(site.Base + "/blob.html");
        await WaitFor(() => !tab.IsLoading && tab.Title == "Made in script");
        var made = await WaitMedia(tab, m => InlinePlaylists.IsInline(m.Url) && m.Status == MediaStatus.Ready, 15000);
        Check("a playlist a player made in script (a blob: address) is found", made is { Status: MediaStatus.Ready } && made.Choices.Count > 0,
            string.Join(", ", tab.Media.Select(x => $"{x.Kind}:{x.Status}:{x.Url}:{x.Error}")));
        if (made is not { Status: MediaStatus.Ready }) return;
        var folder = System.IO.Path.Combine(outDir, "hidden-downloads");
        if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true);
        System.IO.Directory.CreateDirectory(folder);
        w.Settings.DownloadFolder = folder;
        w.Settings.Commit();
        var choice = made.Choices.First(x => !x.AudioOnly);
        await w.StartChoiceAsync(tab, made, choice);
        var d = await Finish(i => i.Stream is not null && i.Url == choice.Url, 60000);
        Check("and downloads as one file", d is { State: Downloads.DownloadState.Completed }, $"{d?.State} {d?.Error}");
        if (FfprobeRuns() && d is { State: Downloads.DownloadState.Completed })
        {
            var probe = Probe(d.FinalPath);
            Check("that plays, the full length", probe is { Video: 1 } && Math.Abs(probe.Value.Duration - 8) < 1.5, probe is null ? "ffprobe could not read it" : $"{probe.Value.Duration:0.0}s, {probe.Value.Video} video, {probe.Value.Audio} audio");
        }
    }
}
