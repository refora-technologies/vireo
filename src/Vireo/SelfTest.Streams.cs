using System.IO;
using System.Net.Http;
using System.Text;
using Vireo.Downloads;
using Vireo.Media;
using Vireo.Models;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>
    /// A stream the way an awkward site serves it: the video page holds a player frame from a second site, which asks a third
    /// (the CDN) for a playlist disguised as a picture; the CDN only answers requests that name the player as the referer, and
    /// every piece has a few bytes of a PNG picture in front of it.
    /// </summary>
    private async Task RunStreamStepsAsync()
    {
        var folder = Path.Combine(outDir, "stream-downloads");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        w.Settings.DownloadFolder = folder;
        w.Settings.BlockAds = false;
        w.Settings.Commit();
        foreach (var old in w.Downloads.Items.ToList()) w.Downloads.Remove(old);

        // real pieces from the Media Lab test media
        using var http = new HttpClient();
        string playlist;
        var pieces = new Dictionary<string, byte[]>();
        try
        {
            playlist = await http.GetStringAsync(baseUrl + "/hls/720/index.m3u8");
            foreach (var name in playlist.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
                pieces[name] = await http.GetByteArrayAsync(baseUrl + "/hls/720/" + name);
        }
        catch
        {
            Console.WriteLine("SKIP  stream steps (test media not available)");
            return;
        }

        using var cdn = new PageSite();
        using var player = new PageSite();
        using var host = new PageSite();
        var refused = 0;
        cdn.Intercept = c =>
        {
            var referer = c.Request.Headers["Referer"] ?? "";
            var path = c.Request.Url!.AbsolutePath;
            if (!referer.StartsWith(player.Base))
            {
                Interlocked.Increment(ref refused);
                c.Response.StatusCode = 403;
                return true;
            }
            if (path == "/v/index.m3u8")
            {
                var bytes = Encoding.UTF8.GetBytes(playlist);
                c.Response.ContentType = "image/jpeg"; // a playlist posing as a picture
                c.Response.ContentLength64 = bytes.Length;
                c.Response.OutputStream.Write(bytes);
                return true;
            }
            var name = path.StartsWith("/v/") ? path[3..] : "";
            if (pieces.TryGetValue(name, out var piece))
            {
                var png = new byte[120];
                new Random(9).NextBytes(png);
                png[0] = 0x89; png[1] = 0x50; png[2] = 0x4E; png[3] = 0x47;
                for (var i = 4; i < png.Length; i++) if (png[i] == 0x47) png[i] = 0x46;
                byte[] wrapped = [.. png, .. piece];
                c.Response.ContentType = "image/png";
                c.Response.ContentLength64 = wrapped.Length;
                c.Response.OutputStream.Write(wrapped);
                return true;
            }
            return false;
        };
        player.Html("/player.html", $"<!doctype html><title>Player</title><script>fetch('{cdn.Base}/v/index.m3u8').then(r => r.text())</script>");
        host.Html("/watch.html", $"<!doctype html><title>Episode 1</title><body><h1>Episode 1</h1><iframe src=\"{player.Base}/player.html\" width=\"640\" height=\"360\"></iframe></body>");

        var tab = await w.OpenTabAsync(host.Base + "/watch.html");
        await WaitFor(() => !tab.IsLoading && tab.Title == "Episode 1");
        var found = await WaitMedia(tab, m => m.Kind == MediaKind.Hls, 20000);
        Check("a stream asked for by a player frame on another site is found", found is not null, string.Join(",", tab.Media.Select(m => $"{m.Kind}:{m.Status}")));
        Check("although its playlist is disguised as a picture and locked to the player", found is { Status: MediaStatus.Ready }, $"{found?.Status} {found?.Error} (refused {refused})");
        if (found is not { Status: MediaStatus.Ready }) return;
        w.ShowMedia();
        await Task.Delay(500);
        await HoverEveryButtonAsync("Media panel");
        w.CloseDrawer();
        var choice = found.Choices.First(c => !c.AudioOnly);
        await w.StartChoiceAsync(tab, found, choice);
        var d = await Finish(i => i.Stream is not null && i.Url == choice.Url, 60000);
        Check("it downloads", d is { State: DownloadState.Completed }, $"{d?.State} {d?.Error}");
        Check("as an MP4, with nothing left over", d is { Note: null } && d.FileName.EndsWith(".mp4") && Directory.GetFiles(folder, "*.vireopart").Length == 0, $"{d?.FileName} {d?.Note}");
        if (FfprobeRuns() && d is { State: DownloadState.Completed })
        {
            var probe = Probe(d.FinalPath);
            Check("that plays, the full length", probe is { Video: 1 } && Math.Abs(probe.Value.Duration - 8) < 1.5, probe is null ? "ffprobe could not read it" : $"{probe.Value.Duration:0.0}s, {probe.Value.Video} video, {probe.Value.Audio} audio");
        }
    }
}
