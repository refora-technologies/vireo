using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vireo.Downloads;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>
    /// Pictures for the README and the release page (only on request: VIREO_STEPS=showcase). Vireo's own screens, with clean
    /// demo content: made-up file names downloading from the local test server, a few start-page sites, no web pages and
    /// no addresses. Written to the out folder as showcase-*.png.
    /// </summary>
    private async Task RunShowcaseStepsAsync()
    {
        w.Topmost = true;
        w.WindowState = WindowState.Normal;
        w.Left = 60; w.Top = 40; w.Width = 1280; w.Height = 800;
        w.CloseDrawer();

        // One clean start page; every test tab goes.
        var home = await w.OpenTabAsync(null);
        foreach (var t in w.AllTabs.Where(t => t != home).ToList()) await w.CloseTabAsync(t);
        foreach (var d in w.Downloads.Items.ToList()) w.Downloads.Remove(d);

        w.Settings.PinnedSites = [new("Mail", "https://mail.example.com/"), new("Calendar", "https://calendar.example.com/"), new("Notes", "https://notes.example.com/"),
            new("Music", "https://music.example.com/"), new("News", "https://news.example.com/"), new("Learn", "https://learn.example.com/")];
        w.Settings.PinnedSitesChangedAt = DateTime.UtcNow;
        w.Settings.Commit();
        BrowserTab.TotalAds = 1284;
        BrowserTab.TotalTrackers = 312;

        // Finished earlier, then two big ones still coming down.
        var demo = new List<DownloadItem>();
        foreach (var (name, size) in new[] { ("pixel-fonts-collection.7z", 1_480_000), ("q3-financial-summary.pdf", 240_000), ("cosmos-podcast-ep118.mp3", 860_000) })
        {
            demo.Add(w.Downloads.Add(new DownloadRequest($"{baseUrl}/generated/{size}.bin?n={name}", name)));
            await WaitFor(() => demo[^1].State == DownloadState.Completed, 20000);
            await Task.Delay(1100); // a second apart, so the list is in this order
        }
        w.Settings.DownloadConnections = 8; // fixed, so the test server's per-connection pace adds up to a believable speed
        w.Settings.Commit();
        demo.Add(w.Downloads.Add(new DownloadRequest($"{baseUrl}/generated/687194767.bin?throttle=330&n=lectures", "winter-semester-lectures.zip")));
        demo.Add(w.Downloads.Add(new DownloadRequest($"{baseUrl}/generated/1288490188.bin?throttle=720&n=aurora", "aurora-borealis-8k-timelapse.mp4")));
        await Task.Delay(9000); // a few seconds of speed for the graphs

        await w.ActivateAsync(home);
        await w.ReloadAsync(home);
        await Task.Delay(3500);
        await Shot("showcase-1-start-page");

        w.ShowDownloads();
        await Task.Delay(3000);
        await Shot("showcase-2-downloads");
        w.CloseDrawer();

        // The small window by the clock.
        MainWindow.ShowTrayWindowForTest();
        await Task.Delay(2500);
        SavePicture(MainWindow.TrayWindowForTest, "showcase-5-tray.png");
        MainWindow.HideTrayWindowForTest();

        // Settings, at the download manager.
        w.Settings.DownloadManager = true;
        w.Settings.WatchClipboard = true;
        w.Settings.Commit();
        w.ShowSettings();
        await Task.Delay(800);
        var body = w.DrawerContent as DependencyObject;
        var scroll = body as ScrollViewer ?? FindAll<ScrollViewer>(body).FirstOrDefault();
        var heading = FindAll<TextBlock>(scroll).FirstOrDefault(t => t.Text == "Download manager");
        if (scroll is not null && heading is not null)
        {
            var y = heading.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + y - 8);
        }
        await Task.Delay(600);
        await Shot("showcase-4-settings");
        w.CloseDrawer();
        w.Settings.DownloadManager = false;
        w.Settings.WatchClipboard = false;
        w.Settings.Commit();
        MainWindow.ApplyDownloadManagerChoices();

        // The welcome page.
        await w.OpenWelcomeForTestAsync();
        await Task.Delay(3000);
        await Shot("showcase-3-welcome");

        foreach (var d in demo) { w.Downloads.Cancel(d); w.Downloads.Remove(d, deleteFile: true); }
        w.Settings.DownloadConnections = 0;
        w.Settings.Commit();
        Check("the showcase pictures are taken", File.Exists(Path.Combine(outDir, "showcase-1-start-page.png")));
    }
}
