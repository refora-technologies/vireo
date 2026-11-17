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
    }
}
