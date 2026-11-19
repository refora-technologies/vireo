using System.IO;
using System.Text.Json;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>
    /// Real, busy pages (many ads, scripts and requests) open in several tabs while a big download runs over many connections:
    /// the window must keep answering. Only on request (needs the internet): VIREO_STEPS=freezes. With VIREO_STALL_STACKS set to
    /// dotnet-stack.exe, every stall also writes where the window's thread was stuck to stalls.log.
    /// </summary>
    private static readonly string[] BusySites =
    [
        "https://edition.cnn.com/",
        "https://www.dailymail.co.uk/home/index.html",
        "https://www.theguardian.com/international",
        "https://www.foxnews.com/",
        "https://www.msn.com/en-us",
        "https://www.speedtest.net/",
    ];

    private async Task RunFreezeStepsAsync()
    {
        if (w.Watchdog is not { } dog) { Console.WriteLine("SKIP  freezes (no watchdog)"); return; }
        var minutes = double.TryParse(Environment.GetEnvironmentVariable("VIREO_FREEZE_MINUTES"), out var m) ? m : 4;

        // A big download in the background, over every connection, the way a large file from a host is fetched.
        var big = Path.Combine(outDir, "freeze-download");
        Directory.CreateDirectory(big);
        w.Downloads.Add(new Downloads.DownloadRequest(baseUrl + "/generated/314572800.bin?throttle=250", "freeze-big.bin", Folder: big));
        dog.Reset();

        // Which addresses are blocked, and how often: a few addresses blocked thousands of times means scripts retrying.
        var blockedTimes = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        MainWindow.BlockedLog = line =>
        {
            var url = line.Split(' ').Last();
            var q = url.IndexOf('?');
            blockedTimes.AddOrUpdate(q > 0 ? url[..q] : url, 1, (_, n) => n + 1);
        };
        var tabs = new List<Models.BrowserTab>();
        foreach (var url in BusySites) tabs.Add(await w.OpenTabAsync(url));
        var until = DateTime.Now.AddMinutes(minutes);
        var round = 0;
        while (DateTime.Now < until)
        {
            // Like a person: switch tabs, scroll, open the panels that list downloads and media.
            // Mostly on the first site (CNN, where blocked ads are retried in a loop), visiting the others in between.
            var tab = round++ % 2 == 0 ? tabs[0] : tabs[1 + round / 2 % (tabs.Count - 1)];
            await w.ActivateAsync(tab);
            await Task.Delay(1500);
            try { await w.EvalAsync(tab, "window.scrollBy(0, 1400)"); } catch { }
            await Task.Delay(1500);
            if (round % 3 == 0) { w.ShowDownloads(); await Task.Delay(800); w.CloseDrawer(); }
            if (round % 4 == 0 && tab.View?.CoreWebView2 is { } core) core.Reload();
        }

        MainWindow.BlockedLog = null;
        Console.WriteLine("BLOCKED  " + string.Join(" | ", tabs.Select(t => $"{new Uri(t.Url).Host}: {t.Blocked}")));
        Console.WriteLine($"BLOCKS   {blockedTimes.Values.Sum()} requests, {blockedTimes.Count} distinct addresses; most repeated: " +
            string.Join(" | ", blockedTimes.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Value}x {kv.Key}")));
        var stalls = dog.Stalls;
        var worst = stalls.Count == 0 ? 0 : stalls.Max(s => s.Ms);
        var summary = string.Join(" | ", stalls.OrderByDescending(s => s.Ms).Take(6).Select(s => $"{s.Ms:0} ms: {s.Doing}"));
        Console.WriteLine($"FREEZES  {stalls.Count} stalls over {minutes} min, worst {worst:0} ms :: {summary}");
        File.WriteAllText(Path.Combine(outDir, "freezes.json"), JsonSerializer.Serialize(stalls.Select(s => new { s.At, s.Ms, s.Doing })));
        Check("busy pages and a download never freeze the window for a second", worst < 1000, $"{stalls.Count} stalls, worst {worst:0} ms");
        foreach (var t in tabs) await w.CloseTabAsync(t);
    }
}
