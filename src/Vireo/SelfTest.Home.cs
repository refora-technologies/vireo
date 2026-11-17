using System.IO;
using Vireo.Downloads;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>The start page: it shows live numbers, and its "Download from a link" box saves a stream someone pastes.</summary>
    private async Task RunHomeStepsAsync()
    {
        var folder = Path.Combine(outDir, "home-downloads");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        w.Settings.DownloadFolder = folder;
        w.Settings.Commit();
        foreach (var old in w.Downloads.Items.ToList()) w.Downloads.Remove(old);

        var tab = await w.OpenTabAsync(null);
        for (var i = 0; i < 40 && await w.EvalAsync(tab, "!!document.getElementById('paste')") != "true"; i++) await Task.Delay(200);
        await Task.Delay(1500);
        var placeholder = await w.EvalAsync(tab, "document.getElementById('q').placeholder + ' | ' + document.getElementById('footer').textContent");
        Check("the start page gets its live numbers", placeholder.Contains("Search with Google") && placeholder.Contains("blocked this session"), placeholder);

        await w.EvalAsync(tab, $"document.getElementById('link').value = '{baseUrl}/hls/720/index.m3u8'; document.getElementById('paste').requestSubmit()");
        string result = "";
        for (var i = 0; i < 60 && !result.Contains("Downloading") && !result.Contains("not"); i++) { await Task.Delay(250); result = await w.EvalAsync(tab, "document.getElementById('result').textContent"); }
        Check("a pasted stream link starts downloading", result.Contains("Downloading"), result);
        var d = await Finish(i => i.Stream is not null, 60000);
        Check("and is saved as one video file", d is { State: DownloadState.Completed } && d.FileName.EndsWith(".mp4"), $"{d?.State} {d?.FileName} {d?.Error}");
        await Task.Delay(1500);
        Check("the start page lists it", (await w.EvalAsync(tab, "document.getElementById('items').textContent")).Contains(".mp4"));
        await Shot("41-start-page-live");

        await w.EvalAsync(tab, "document.getElementById('link').value = 'not a link'; document.getElementById('paste').requestSubmit()");
        await Task.Delay(800);
        Check("something that is not a link gets a clear answer", (await w.EvalAsync(tab, "document.getElementById('result').textContent")).Contains("does not look like"));

        // The downloads list: the newest at the top, also when the panel is opened later (not only while it is open).
        foreach (var name in new[] { "older.mp4", "newer.mp4" })
        {
            w.Downloads.Add(new Downloads.DownloadRequest(baseUrl + "/clip.mp4", name));
            await Task.Delay(1100); // a second apart, so "newest" is unambiguous
        }
        await WaitFor(() => w.Downloads.Items.Count(i => i.FileName.StartsWith("newer") || i.FileName.StartsWith("older")) == 2 && w.Downloads.Items.All(i => i.State is not (Downloads.DownloadState.Downloading or Downloads.DownloadState.Queued)), 30000);
        var panel = new Controls.DownloadsPanel(w.Downloads);
        var shown = panel.Shown;
        var newestFirst = w.Downloads.Items.Where(i => i.Parent is null).OrderByDescending(i => i.CreatedAt).ToList();
        Check("the downloads list shows the newest at the top", shown.Count >= 2 && shown.SequenceEqual(newestFirst),
            string.Join(" | ", shown.Take(3).Select(i => $"{i.FileName} {i.CreatedAt:HH:mm:ss}")));
    }
}
