using System.Net.Http;
using Vireo.Models;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>
    /// Heavy use, measured: many big pages at once, fast tab switching, scrolling, panels, downloads and stream downloads all
    /// together, sleeping and waking tabs. The watchdog times how long the window takes to answer throughout.
    /// </summary>
    private async Task RunStressStepsAsync()
    {
        if (w.Watchdog is not { } dog) { Console.WriteLine("SKIP  stress (no watchdog)"); return; }
        var online = false;
        try { using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; online = (await probe.GetAsync("https://www.google.com/generate_204")).IsSuccessStatusCode; } catch { /* offline */ }

        var pages = new List<string>
        {
            baseUrl + "/pages/file.html", baseUrl + "/pages/hls.html", baseUrl + "/pages/mixed.html", baseUrl + "/pages/long.html", baseUrl + "/pages/dash-real.html",
        };
        if (online) pages.AddRange([
            "https://en.wikipedia.org/wiki/Web_browser", "https://www.bbc.com/news", "https://www.theverge.com/", "https://www.youtube.com/",
            "https://www.reddit.com/r/videos/", "https://edition.cnn.com/", "https://www.amazon.com/", "https://github.com/trending",
        ]);

        async Task Phase(string name, Func<Task> work, double limitMs = 2000)
        {
            dog.Reset();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await work();
            await Task.Delay(400); // let the last of it settle
            var stalls = dog.Stalls;
            var worst = dog.WorstMs;
            var detail = $"worst {worst:0} ms, {stalls.Count} stall(s), took {watch.Elapsed.TotalSeconds:0.0} s" + (stalls.Count > 0 ? " — " + string.Join(" | ", stalls.OrderByDescending(s => s.Ms).Take(3).Select(s => $"{s.Ms:0} ms: {s.Doing}")) : "");
            Console.WriteLine($"STRESS  {name}: {detail}");
            Check($"the window keeps answering while {name}", worst < limitMs, detail);
        }

        var opened = new List<BrowserTab>();
        await Phase($"opening {pages.Count} pages at once{(online ? " (busy real sites included)" : "")}", async () =>
        {
            foreach (var p in pages) opened.Add(await w.OpenTabAsync(p, activate: false));
            await w.ActivateAsync(opened[^1]);
            await WaitFor(() => opened.All(t => !t.IsLoading), 45000);
        });

        await Phase("switching tabs fast", async () =>
        {
            var r = new Random(4);
            for (var i = 0; i < 50; i++) { await w.ActivateAsync(opened[r.Next(opened.Count)]); await Task.Delay(120); }
        });

        await Phase("scrolling busy pages", async () =>
        {
            foreach (var t in opened.Take(8))
            {
                await w.ActivateAsync(t);
                for (var k = 0; k < 6; k++) { await w.EvalAsync(t, "window.scrollBy(0, 1500)"); await Task.Delay(80); }
            }
        });

        await Phase("opening every panel", async () =>
        {
            for (var round = 0; round < 3; round++)
            {
                w.ShowDownloads(); await Task.Delay(250);
                w.ShowMedia(); await Task.Delay(250);
                w.ShowSettings(); await Task.Delay(250);
                w.ShowHistory(); await Task.Delay(250);
                await w.ShowBatchGrabberAsync(); await Task.Delay(250);
                w.ShowExtensions(); await Task.Delay(250);
            }
            w.CloseDrawer();
        });

        await Phase("downloading files and a stream while browsing", async () =>
        {
            var hls = opened.FirstOrDefault(t => t.Url.Contains("/pages/hls.html"));
            if (hls is not null && hls.Media.FirstOrDefault(m => m.Status == Media.MediaStatus.Ready) is { } stream)
                await w.StartChoiceAsync(hls, stream, stream.Choices[0]);
            var file = opened.FirstOrDefault(t => t.Url.Contains("/pages/file.html"));
            if (file is not null && file.Media.FirstOrDefault(m => m.Status == Media.MediaStatus.Ready) is { } clip)
                for (var i = 0; i < 3; i++) await w.StartChoiceAsync(file, clip, clip.Choices[0]);
            w.ShowDownloads();
            for (var i = 0; i < 30; i++) { await w.ActivateAsync(opened[i % opened.Count]); await Task.Delay(100); }
            await WaitFor(() => w.Downloads.Items.All(d => !d.IsActive), 30000);
            w.CloseDrawer();
        });

        await Phase("putting tabs to sleep and waking them", async () =>
        {
            var keep = opened[0];
            await w.ActivateAsync(keep);
            foreach (var t in opened.Where(t => t != keep)) t.HiddenSince = DateTime.UtcNow - TimeSpan.FromHours(1);
            await w.SweepNowAsync();
            foreach (var t in opened.Skip(1).Take(5)) { await w.ActivateAsync(t); await Task.Delay(300); }
        });

        foreach (var t in opened) await w.CloseTabAsync(t);
        var memory = w.MeasureMemory();
        Console.WriteLine($"STRESS  memory after closing them: {memory.Bytes / 1048576} MB in {memory.Processes} processes");
    }
}
