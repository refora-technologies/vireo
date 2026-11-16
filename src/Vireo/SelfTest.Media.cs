using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Vireo.Downloads;
using Vireo.Media;
using Vireo.Models;

namespace Vireo;

internal sealed partial class SelfTest
{
    private static (double Duration, int Video, int Audio)? Probe(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe", $"-v error -show_format -show_streams -of json \"{path}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) return null;
            using var doc = JsonDocument.Parse(json);
            var streams = doc.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()).ToList();
            var dur = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            return (dur, streams.Count(x => x == "video"), streams.Count(x => x == "audio"));
        }
        catch { return null; }
    }

    private static bool FfprobeRuns()
    {
        try { using var p = Process.Start(new ProcessStartInfo("ffprobe", "-version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }); p!.WaitForExit(); return p.ExitCode == 0; }
        catch { return false; }
    }

    private async Task<DetectedMedia?> WaitMedia(BrowserTab tab, Func<DetectedMedia, bool> match, int ms = 15000)
    {
        await WaitFor(() => tab.Media.Any(m => match(m) && m.Status != MediaStatus.Probing), ms);
        return tab.Media.FirstOrDefault(m => match(m) && m.Status != MediaStatus.Probing);
    }

    private async Task<DownloadItem?> Finish(Func<DownloadItem, bool> match, int ms = 40000)
    {
        await WaitFor(() => w.Downloads.Items.Any(i => match(i) && i.State is DownloadState.Completed or DownloadState.Failed), ms);
        return w.Downloads.Items.FirstOrDefault(i => match(i));
    }

    private async Task RunMediaStepsAsync()
    {
        var folder = Path.Combine(outDir, "media-downloads");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        w.Settings.DownloadFolder = folder;
        w.Settings.ShowDownloadButtons = true;
        w.Settings.DownloadButtonMode = "hover";
        w.Settings.BlockAds = false;
        w.Settings.Commit();
        foreach (var old in w.Downloads.Items.ToList()) w.Downloads.Remove(old);
        var ffprobe = FfprobeRuns();

        // ---- A plain video file ----
        var tab = await w.OpenTabAsync(baseUrl + "/pages/file.html");
        await WaitFor(() => !tab.IsLoading && tab.Title.Length > 0);
        var file = await WaitMedia(tab, m => m.Url.EndsWith("/clip.mp4"));
        Check("a video file on the page is found", file is { Kind: MediaKind.File, Status: MediaStatus.Ready }, file?.Status.ToString() ?? "not found");
        Check("with its size", file?.SizeBytes > 100_000, $"{file?.SizeBytes} bytes");
        Check("the toolbar button counts it", w.MediaBadge == "1", $"'{w.MediaBadge}'");

        // ---- The button drawn on the video ----
        await Task.Delay(1200);
        await w.EvalAsync(tab, "window.__vireoTest && window.__vireoTest.hover()");
        await Task.Delay(500);
        var state = await OverlayState(tab);
        Check("a Download button appears when you move over the video", state.GetProperty("buttons").GetInt32() == 1, state.ToString());
        await Shot("23-video-button");
        await w.EvalAsync(tab, "window.__vireoTest.press()");
        for (var i = 0; i < 40 && (await OverlayState(tab)).GetProperty("rows").GetArrayLength() == 0; i++) await Task.Delay(150);
        await Task.Delay(500);
        state = await OverlayState(tab);
        Check("pressing it lists the ways to save", state.GetProperty("menu").GetBoolean() && state.GetProperty("rows").GetArrayLength() >= 1, state.GetProperty("rows").ToString());
        await Shot("24-video-menu");
        var before = w.Downloads.Items.Count;
        await w.EvalAsync(tab, "window.__vireoTest.pick(0)");
        var d1 = await Finish(i => i.Url.EndsWith("/clip.mp4"));
        Check("choosing one downloads the file over several connections", d1 is { State: DownloadState.Completed } && w.Downloads.Items.Count == before + 1, $"{d1?.State} {d1?.Error}");
        Check("the saved file is the whole video", d1 is not null && File.Exists(d1.FinalPath) && new FileInfo(d1.FinalPath).Length == file!.SizeBytes, d1?.FileName);

        // ---- Settings turn the buttons off ----
        w.Settings.ShowDownloadButtons = false;
        w.Settings.Commit();
        await Task.Delay(600);
        await w.EvalAsync(tab, "window.__vireoTest.hover()");
        await Task.Delay(500);
        state = await OverlayState(tab);
        Check("switching the buttons off hides them", state.GetProperty("buttons").GetInt32() == 0, state.ToString());
        w.Settings.ShowDownloadButtons = true;
        w.Settings.Commit();

        // ---- A stream with qualities ----
        var hlsTab = await w.OpenTabAsync(baseUrl + "/pages/hls.html");
        await WaitFor(() => !hlsTab.IsLoading);
        var hls = await WaitMedia(hlsTab, m => m.Kind == MediaKind.Hls);
        Check("a stream's qualities are read", hls is { Status: MediaStatus.Ready } && hls.Choices.Any(c => c.Label == "720p") && hls.Choices.Any(c => c.Label == "360p"), string.Join(",", hls?.Choices.Select(c => c.Label) ?? []));
        Check("including audio only", hls?.Choices.Any(c => c.AudioOnly) == true);
        Check("and how long it is", hls?.DurationSec is > 5 and < 12, $"{hls?.DurationSec}");
        Check("the pieces a player loads are not listed as videos", hlsTab.Media.Count(m => m.Kind == MediaKind.Hls) == 1, $"{hlsTab.Media.Count} items");
        await w.ShowMediaAndWait();
        await Shot("25-media-panel");
        w.CloseDrawer();
        await Task.Delay(300);

        var q360 = hls!.Choices.First(c => c.Label == "360p");
        await w.StartChoiceAsync(hlsTab, hls, q360);
        var d2 = await Finish(i => i.Stream is not null && i.FileName.Contains("360p"));
        Check("a stream is downloaded as one finished file", d2 is { State: DownloadState.Completed } && File.Exists(d2.FinalPath), $"{d2?.State} {d2?.Error} {d2?.FileName}");
        if (ffprobe && d2 is { State: DownloadState.Completed })
        {
            var probe = Probe(d2.FinalPath);
            Check("that plays: video and sound, the right length", probe is { Video: 1, Audio: 1 } && Math.Abs(probe.Value.Duration - 8) < 1.5, probe is null ? "ffprobe could not read it" : $"{probe.Value.Duration:0.0}s, {probe.Value.Video} video, {probe.Value.Audio} audio");
        }

        // ---- Audio kept separate by the stream: joined into one file ----
        var demux = await w.OpenTabAsync(baseUrl + "/pages/hls-demuxed.html");
        await WaitFor(() => !demux.IsLoading);
        var dm = await WaitMedia(demux, m => m.Kind == MediaKind.Hls);
        Check("a stream with its audio in a separate list is understood", dm?.Choices.Any(c => !c.AudioOnly && c.AudioUrl is not null) == true, string.Join(",", dm?.Choices.Select(c => $"{c.Label}/{c.AudioUrl is not null}") ?? []));
        var withAudio = dm!.Choices.First(c => !c.AudioOnly);
        await w.StartChoiceAsync(demux, dm, withAudio);
        var d3 = await Finish(i => i.Stream?.AudioUrl is not null);
        Check("and downloaded as one file", d3 is { State: DownloadState.Completed }, $"{d3?.State} {d3?.Error} {d3?.Note}");
        if (ffprobe && d3 is { State: DownloadState.Completed } && d3.Note is null)
        {
            var probe = Probe(d3.FinalPath);
            Check("with both video and sound", probe is { Video: 1, Audio: 1 }, probe is null ? "ffprobe could not read it" : $"{probe.Value.Video} video, {probe.Value.Audio} audio");
        }

        // ---- fMP4 pieces: joined natively with no help ----
        var frag = await w.OpenTabAsync(baseUrl + "/pages/hls-fmp4.html");
        await WaitFor(() => !frag.IsLoading);
        var fm = await WaitMedia(frag, m => m.Kind == MediaKind.Hls);
        await w.StartChoiceAsync(frag, fm!, fm!.Choices.First(c => !c.AudioOnly));
        var d4 = await Finish(i => i.Stream is not null && i.Url == fm.Choices.First(c => !c.AudioOnly).Url);
        Check("a stream of fragmented MP4 pieces becomes an MP4", d4 is { State: DownloadState.Completed } && d4.FileName.EndsWith(".mp4"), $"{d4?.State} {d4?.Error} {d4?.FileName}");
        if (ffprobe && d4 is { State: DownloadState.Completed })
        {
            var probe = Probe(d4.FinalPath);
            Check("that plays", probe is { Video: 1 } && probe.Value.Duration > 4, probe is null ? "ffprobe could not read it" : $"{probe.Value.Duration:0.0}s");
        }

        // ---- Encrypted (plain AES-128) ----
        var aes = await w.OpenTabAsync(baseUrl + "/pages/hls-aes.html");
        await WaitFor(() => !aes.IsLoading);
        var am = await WaitMedia(aes, m => m.Kind == MediaKind.Hls);
        await w.StartChoiceAsync(aes, am!, am!.Choices[0]);
        var d5 = await Finish(i => i.Stream is not null && i.Url == am.Choices[0].Url);
        Check("an encrypted stream is decrypted and saved", d5 is { State: DownloadState.Completed }, $"{d5?.State} {d5?.Error}");
        if (ffprobe && d5 is { State: DownloadState.Completed })
        {
            var probe = Probe(d5.FinalPath);
            Check("and plays", probe is { Video: 1 } && probe.Value.Duration > 5, probe is null ? "ffprobe could not read it (still encrypted?)" : $"{probe.Value.Duration:0.0}s");
        }

        // ---- Protected content is declared and refused ----
        var drm = await w.OpenTabAsync(baseUrl + "/pages/drm.html");
        await WaitFor(() => !drm.IsLoading);
        await Task.Delay(1500);
        var dd = drm.Media.FirstOrDefault(m => m.Kind == MediaKind.Hls);
        await WaitFor(() => drm.Media.Any(m => m.Status == MediaStatus.Protected), 8000);
        Check("a protected stream is declared as such", drm.Media.Any(m => m.Status == MediaStatus.Protected), string.Join(",", drm.Media.Select(m => m.Status)));
        Check("and offers nothing to download", drm.Media.Where(m => m.Status == MediaStatus.Protected).All(m => m.Choices.Count == 0));

        // ---- A DASH stream (.mpd) ----
        var dash = await w.OpenTabAsync(baseUrl + "/pages/dash-real.html");
        await WaitFor(() => !dash.IsLoading);
        var dsm = await WaitMedia(dash, m => m.Kind == MediaKind.Dash);
        Check("a DASH stream is found and read", dsm is { Status: MediaStatus.Ready }, $"{dsm?.Status} {dsm?.Error}");
        Check("with its qualities and audio only", dsm?.Choices.Select(c => c.Label).SequenceEqual(["720p", "360p", "Audio only"]) == true, string.Join(",", dsm?.Choices.Select(c => c.Label) ?? []));
        var dashChoice = dsm!.Choices[1];
        await w.StartChoiceAsync(dash, dsm, dashChoice);
        var d7 = await Finish(i => i.Stream is not null && i.Url == dashChoice.Url);
        Check("it downloads as one MP4 with picture and sound", d7 is { State: DownloadState.Completed, Note: null } && d7.FileName.EndsWith(".mp4"), $"{d7?.State} {d7?.Error} {d7?.Note}");
        if (ffprobe && d7 is { State: DownloadState.Completed })
        {
            var probe = Probe(d7.FinalPath);
            Check("that plays", probe is { Video: 1, Audio: 1 } && Math.Abs(probe.Value.Duration - 8) < 1.5, probe is null ? "ffprobe could not read it" : $"{probe.Value.Duration:0.0}s, {probe.Value.Video} video, {probe.Value.Audio} audio");
        }

        // ---- A live stream, recorded until stopped ----
        var live = await w.OpenTabAsync(baseUrl + "/pages/live-dyn.html");
        await WaitFor(() => !live.IsLoading);
        var lm = await WaitMedia(live, m => m.Kind == MediaKind.Hls);
        Check("a live stream is recognised", lm?.Live == true, $"live={lm?.Live}");
        await w.StartChoiceAsync(live, lm!, lm!.Choices[0]);
        await WaitFor(() => w.Downloads.Items.Any(i => i.Stream?.Live == true && i.Done0 >= 3), 20000);
        var rec = w.Downloads.Items.First(i => i.Stream?.Live == true);
        Check("it records while it plays", rec.Done0 >= 3 && rec.Note?.StartsWith("Recording") == true, rec.Note);
        w.Downloads.StopLive(rec);
        var d6 = await Finish(i => i.Stream?.Live == true);
        Check("and Stop saves what it has", d6 is { State: DownloadState.Completed } && File.Exists(d6.FinalPath) && new FileInfo(d6.FinalPath).Length > 50_000, $"{d6?.State} {d6?.Error}");

        // ---- The finished list ----
        w.ShowDownloads();
        await Task.Delay(800);
        await Shot("26-downloads-media");
        w.CloseDrawer();
    }

    private async Task<JsonElement> OverlayState(BrowserTab tab)
    {
        var raw = await w.EvalAsync(tab, "window.__vireoTest ? window.__vireoTest.state() : null");
        var json = JsonSerializer.Deserialize<string>(raw) ?? "{\"buttons\":0,\"menu\":false,\"rows\":[]}";
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
