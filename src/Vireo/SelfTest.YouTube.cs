using System.IO;
using System.Net.Http;
using System.Text.Json;
using Vireo.Downloads;
using Vireo.Media;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>The real thing: a YouTube page, the button on its player, one click, a playing file. Skipped when offline.</summary>
    private async Task RunYouTubeStepsAsync()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            (await probe.GetAsync("https://www.youtube.com/generate_204")).EnsureSuccessStatusCode();
        }
        catch
        {
            Console.WriteLine("SKIP  YouTube steps (no internet)");
            return;
        }

        var ffprobe = FfprobeRuns();
        var tab = await w.OpenTabAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        var yt = await WaitMedia(tab, m => m.Kind == MediaKind.YouTube, 45000);
        Check("YouTube's video is found from the page address", yt is { Status: MediaStatus.Ready }, yt is null ? "not found" : $"{yt.Status} {yt.Error}");
        Check("with every quality and an audio-only choice", yt?.Choices.Count(c => !c.AudioOnly) >= 5 && yt.Choices.Any(c => c.AudioOnly), string.Join(",", yt?.Choices.Select(c => c.Label) ?? []));
        Check("and its real title and length", yt is not null && yt.Title.Contains("Never Gonna Give You Up") && yt.DurationSec is > 200 and < 225, $"{yt?.Title} {yt?.DurationSec}");
        Check("the toolbar button counts it", w.MediaBadge == "1", w.MediaBadge);

        // The button on YouTube's own player
        // YouTube lays its player out late when the computer is busy: keep moving over it until the button is there (up to 20 s).
        var state = await OverlayState(tab);
        for (var i = 0; i < 25 && state.GetProperty("buttons").GetInt32() < 1; i++)
        {
            await Task.Delay(800);
            await w.EvalAsync(tab, "window.__vireoTest && window.__vireoTest.hover()");
            await Task.Delay(300);
            state = await OverlayState(tab);
        }
        Check("a Download button appears on YouTube's player", state.GetProperty("buttons").GetInt32() >= 1, state.ToString());
        // The test does not need the video to keep playing: paused, YouTube's autoplay cannot move the tab to the next video.
        await w.EvalAsync(tab, "document.querySelector('video') && document.querySelector('video').pause()");
        // A hover button hides again when the (pretend) mouse goes quiet: hover and press until the menu is open.
        for (var attempt = 0; attempt < 6 && (await OverlayState(tab)).GetProperty("rows").GetArrayLength() == 0; attempt++)
        {
            await w.EvalAsync(tab, "window.__vireoTest.hover()");
            await Task.Delay(200);
            await w.EvalAsync(tab, "window.__vireoTest.press()");
            for (var i = 0; i < 20 && (await OverlayState(tab)).GetProperty("rows").GetArrayLength() == 0; i++) await Task.Delay(150);
        }
        state = await OverlayState(tab);
        var rows = state.GetProperty("rows").EnumerateArray().Select(r => r.GetString() ?? "").ToList();
        Check("pressing it lists the qualities", rows.Count >= 6 && rows.Any(r => r.StartsWith("1080p")), string.Join(" | ", rows.Take(4)) + $" | at {tab.Url} | {state}");
        await Shot("27-youtube-menu");

        var index = rows.FindIndex(r => r.StartsWith("144p"));
        var before = w.Downloads.Items.Count;
        // The menu closes when the (pretend) mouse goes quiet, as it would if a person moved away: if it closed while the
        // screenshot was taken, open it again before choosing.
        var picked = await w.EvalAsync(tab, $"window.__vireoTest.pick({index})");
        for (var again = 0; again < 4 && picked != "true"; again++)
        {
            await w.EvalAsync(tab, "window.__vireoTest.hover()");
            await Task.Delay(200);
            await w.EvalAsync(tab, "window.__vireoTest.press()");
            for (var i = 0; i < 20 && (await OverlayState(tab)).GetProperty("rows").GetArrayLength() == 0; i++) await Task.Delay(150);
            picked = await w.EvalAsync(tab, $"window.__vireoTest.pick({index})");
        }
        await Task.Delay(8000);
        Console.WriteLine($"INFO  after picking row {index} ({picked}): " + string.Join(" || ", w.Downloads.Items.Select(i => $"{i.FileName} {i.State} {i.Error} pair={i.Pair is not null} stream={i.Stream is not null} {i.Downloaded}/{i.Total} {i.Url[..Math.Min(70, i.Url.Length)]}")));
        var d = await Finish(i => i.Pair is not null, 240000);
        Check("one click downloads video and audio together", d is { State: DownloadState.Completed } && w.Downloads.Items.Count == before + 1, $"{d?.State} {d?.Error} | rows: {string.Join(", ", rows)} | media: {string.Join(", ", tab.Media.Select(m => $"{m.Kind}/{m.Status}/{m.Quiet}/{m.Url[..Math.Min(80, m.Url.Length)]}"))}");
        Check("joined into one file, nothing left over", d is { Note: null } && Directory.GetFiles(w.Settings.DownloadFolder, "*.vireopart").Length == 0, d?.Note);
        if (ffprobe && d is { State: DownloadState.Completed })
        {
            var p = Probe(d.FinalPath);
            Check("that plays: video and sound, the full length", p is { Video: 1, Audio: 1 } && Math.Abs(p.Value.Duration - 213) < 3, p is null ? "ffprobe could not read it" : $"{p.Value.Duration:0.0}s, {p.Value.Video} video, {p.Value.Audio} audio");
        }

        // Audio only
        var aIndex = rows.FindIndex(r => r.StartsWith("Audio only"));
        await w.EvalAsync(tab, "window.__vireoTest.hover()");
        await Task.Delay(400);
        await w.EvalAsync(tab, "window.__vireoTest.press()");
        for (var i = 0; i < 40 && (await OverlayState(tab)).GetProperty("rows").GetArrayLength() == 0; i++) await Task.Delay(150);
        await w.EvalAsync(tab, $"window.__vireoTest.pick({aIndex})");
        var a = await Finish(i => i.FileName.EndsWith(".m4a") && i.Pair is null, 60000);
        Check("audio only is saved as a music file", a is { State: DownloadState.Completed } && File.Exists(a.FinalPath), $"{a?.State} {a?.Error}");
        if (ffprobe && a is { State: DownloadState.Completed })
        {
            var p = Probe(a.FinalPath);
            Check("that plays", p is { Video: 0, Audio: 1 } && p.Value.Duration > 200, p is null ? "ffprobe could not read it" : $"{p.Value.Duration:0.0}s");
        }
        await w.ShowMediaAndWait();
        await Shot("28-youtube-panel");
        w.CloseDrawer();
    }
}
