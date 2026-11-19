using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Vireo.Controls;
using Vireo.Downloads;
using Vireo.Services;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>Where the self-test's "start with Windows" goes: a key of its own, never Windows' real Run key.</summary>
    internal const string TestRunKey = @"Software\VireoSelfTest\Run";

    /// <summary>
    /// The welcome page: it opens, says what Vireo does, and each of its buttons does the real thing (the download-manager
    /// choice turns on the downloader's icon, the clipboard offer and "start with Windows"; "Just the browser" undoes them).
    /// A page that is not the welcome cannot press its buttons.
    /// </summary>
    private async Task RunWelcomeStepsAsync()
    {
        w.Settings.WelcomeSeen = 0;
        w.Settings.Commit();
        Check("a profile that has not seen the welcome is due for it", w.WelcomeIsDue);
        await w.OpenWelcomeForTestAsync();
        var tab = w.Active!;
        for (var i = 0; i < 60 && await w.EvalAsync(tab, "!!document.getElementById('done')") != "true"; i++) await Task.Delay(200);
        await Task.Delay(1200);
        var title = await w.EvalAsync(tab, "document.getElementById('title').textContent");
        Check("the welcome page opens", title.Contains("Welcome to Vireo") || title.Contains("download manager too"), title);
        Check("and is then marked as seen (shown once)", !w.WelcomeIsDue, $"seen {w.Settings.WelcomeSeen}");
        await Shot("70-welcome");

        async Task Click(string id) { await w.EvalAsync(tab, $"document.getElementById('{id}').click()"); await Task.Delay(700); }

        await Click("dmYes");
        await WaitFor(() => w.Settings.DownloadManager, 5000);
        Check("\"Yes, use Vireo\" makes it the download manager", w.Settings.DownloadManager && w.Settings.WatchClipboard);
        Check("the downloader starts with Windows (the test's own key)", AutoStart.IsOn(App.ProfileId, TestRunKey));
        Check("its icon is in the notification area, watching for copied links", MainWindow.TrayShown && MainWindow.ClipboardWatched);
        Check("the page shows what that brings", await w.EvalAsync(tab, "document.getElementById('extras').classList.contains('on')") == "true");
        await Click("sort");
        Check("sorting into folders can be chosen there", w.Settings.SortByType);
        await Shot("71-welcome-download-manager");

        // Another page cannot press the welcome's buttons.
        var other = await w.OpenTabAsync(baseUrl + "/pages/file.html");
        await WaitFor(() => !other.IsLoading && other.Title.Length > 0);
        await w.EvalAsync(other, "chrome.webview.postMessage({ t: 'welcome-dm', on: false })");
        await Task.Delay(800);
        Check("a web page's message to the welcome is ignored", w.Settings.DownloadManager && AutoStart.IsOn(App.ProfileId, TestRunKey));
        await w.CloseTabAsync(other);
        await w.ActivateAsync(tab);

        await Click("dmNo");
        await WaitFor(() => !w.Settings.DownloadManager, 5000);
        Check("\"Just the browser\" undoes it all", !w.Settings.DownloadManager && !w.Settings.WatchClipboard && !AutoStart.IsOn(App.ProfileId, TestRunKey) && !MainWindow.TrayShown,
            $"dm {w.Settings.DownloadManager}, clipboard {w.Settings.WatchClipboard}, autostart {AutoStart.IsOn(App.ProfileId, TestRunKey)}, tray {MainWindow.TrayShown}");
        await Click("sort");
        await Click("done");
        await WaitFor(() => tab.IsStartPage && tab.View?.CoreWebView2?.Source.EndsWith("/newtab.html") == true, 5000);
        Check("\"Start browsing\" goes to the start page", tab.View?.CoreWebView2?.Source.EndsWith("/newtab.html") == true, tab.View?.CoreWebView2?.Source);
    }

    /// <summary>
    /// The download manager at work: a copied link offered from the notification area and downloaded with one click, the
    /// same link carrying on its unfinished download, files sorted by type, a checksum checked, "when done" with its
    /// cancelable warning, and Vireo started as the downloader alone (no page engine at all).
    /// </summary>
    private async Task RunDownloadManagerStepsAsync()
    {
        var folder = Path.Combine(outDir, "manager-downloads");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        w.Settings.DownloadFolder = folder;
        w.Settings.DownloadManager = true;
        w.Settings.Commit();
        MainWindow.ApplyDownloadManagerChoices();
        Check("as a download manager, the downloader's icon stays while the browser is open", MainWindow.TrayShown);

        // A copied link: "Download this?" from the notification area; a click downloads it.
        MainWindow.OfferCopiedLinkForTest(new Uri(baseUrl + "/generated/400000.bin"));
        Check("a copied link is offered", MainWindow.TrayLastBalloon?.StartsWith("Download this?") == true, MainWindow.TrayLastBalloon);
        MainWindow.ClickBalloonForTest();
        var offered = await Finish(i => i.Url == baseUrl + "/generated/400000.bin", 30000);
        Check("and one click downloads it", offered is { State: DownloadState.Completed }, $"{offered?.State} {offered?.Error}");
        MainWindow.OfferCopiedLinkForTest(new Uri(baseUrl + "/generated/400000.bin"));
        Check("the same link is not offered twice", MainWindow.TrayLastBalloon?.StartsWith("Download this?") != true, MainWindow.TrayLastBalloon);

        // Same link, same download.
        var slow = baseUrl + "/generated/20000000.bin?throttle=400";
        var first = w.Downloads.Add(new DownloadRequest(slow));
        await WaitFor(() => first.Downloaded > 100_000, 15000);
        w.Downloads.Pause(first);
        await WaitFor(() => first.State == DownloadState.Paused, 5000);
        var count = w.Downloads.Items.Count;
        var (ok, said) = await w.QuickDownloadAsync(slow);
        Check("pasting the link of an unfinished download carries it on", ok && said.Contains("carries on") && w.Downloads.Items.Count == count && first.IsActive, said);
        w.Downloads.Cancel(first);

        // Sorted by type.
        w.Settings.SortByType = true;
        w.Settings.Commit();
        var video = w.Downloads.Add(new DownloadRequest(baseUrl + "/generated/300000.bin", "clip.mp4"));
        var other = w.Downloads.Add(new DownloadRequest(baseUrl + "/generated/300001.bin", "data.bin"));
        await WaitFor(() => video.State == DownloadState.Completed && other.State == DownloadState.Completed, 30000);
        Check("with sorting on, a video goes in the Video folder", video.FinalPath == Path.Combine(folder, "Video", "clip.mp4") && File.Exists(video.FinalPath), video.FinalPath);
        Check("and other files stay in the download folder", other.FinalPath == Path.Combine(folder, "data.bin"), other.FinalPath);
        w.Settings.SortByType = false;
        w.Settings.Commit();

        // The checksum box under a finished download.
        var panel = new DownloadsPanel(w.Downloads);
        var host = new Window { Content = panel, Background = UiKit.Brush("Surface1"), Width = 420, Height = 600, Left = -4000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show();
        await Task.Delay(500);
        var row = FindRows(panel).First(r => r.Item == other);
        row.ToggleChecksum();
        await Task.Delay(300);
        var box = row.ChecksumForTest!;
        box.PasteForTest(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(other.FinalPath))));
        await box.CheckForTestAsync();
        Check("a checksum from the site that matches says so", box.Said.StartsWith("It matches"), box.Said);
        await Task.Delay(400); // drawn with its answer
        host.UpdateLayout();
        SavePicture(host, "72-checksum.png");
        box.PasteForTest(new string('a', 64));
        await box.CheckForTestAsync();
        Check("one that does not match warns", box.Said.StartsWith("It does not match"), box.Said);
        box.PasteForTest("not a checksum");
        await box.CheckForTestAsync();
        Check("and something else is explained", box.Said.StartsWith("That is not a checksum"), box.Said);
        host.Close();

        // When done: a minute's warning (here three seconds), Cancel in front; it happens once.
        AfterDownloads? acted = null;
        WhenDoneCountdown? countdown = null;
        var warn = WhenDone.Warn;
        WhenDone.ActForTest = what => acted = what;
        WhenDone.Warn = what => { countdown = new WhenDoneCountdown(what, TimeSpan.FromSeconds(3)) { Left = -4000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false }; return countdown.Run(); };
        try
        {
            var a = w.Downloads.Add(new DownloadRequest(baseUrl + "/generated/2000000.bin?throttle=1000"));
            WhenDone.Choose(AfterDownloads.ShutDown, w.Downloads);
            await WaitFor(() => countdown is not null, 30000);
            Check("when everything has finished, the shut-down warning appears", countdown is not null && a.State == DownloadState.Completed, a.State.ToString());
            await Task.Delay(600);
            SavePicture(countdown, "73-when-done.png");
            countdown?.Press(false);
            await Task.Delay(500);
            Check("Cancel stops it, and the choice is used up", acted is null && WhenDone.Choice == AfterDownloads.Nothing, $"{acted} {WhenDone.Choice}");

            countdown = null;
            var b = w.Downloads.Add(new DownloadRequest(baseUrl + "/generated/1000000.bin?throttle=1000"));
            WhenDone.Choose(AfterDownloads.Sleep, w.Downloads);
            await WaitFor(() => acted is not null, 40000);
            Check("left alone, the warning runs out and the computer would sleep", acted == AfterDownloads.Sleep && b.State == DownloadState.Completed, $"{acted} {b.State}");

            acted = null;
            WhenDone.Choose(AfterDownloads.ShutDown, w.Downloads);
            await Task.Delay(5000);
            Check("chosen while nothing runs, nothing happens", acted is null && countdown?.IsVisible != true);
            WhenDone.Choose(AfterDownloads.Nothing, w.Downloads);
        }
        finally
        {
            WhenDone.ActForTest = null;
            WhenDone.Warn = warn;
        }

        w.Settings.DownloadManager = false;
        w.Settings.Commit();
        MainWindow.ApplyDownloadManagerChoices();

        // "Vireo.exe --downloads --quiet" (what Windows starts at sign-in), in a profile of its own: only the downloader,
        // light, and without the page engine; a second start hands over to it instead of starting another.
        var profile = Path.GetFullPath(Path.Combine(outDir, "downloader-only"));
        if (Directory.Exists(profile)) Directory.Delete(profile, true);
        Directory.CreateDirectory(profile);
        var info = new ProcessStartInfo(Environment.ProcessPath!, "--downloads --quiet") { UseShellExecute = false };
        info.Environment["VIREO_DATA"] = profile;
        using var alone = Process.Start(info)!;
        await Task.Delay(7000);
        alone.Refresh();
        var set = alone.HasExited ? 0 : alone.WorkingSet64;
        Check("the downloader starts on its own and keeps running", !alone.HasExited && Directory.Exists(Path.Combine(profile, "downloads")) && !File.Exists(Path.Combine(profile, "crash.log")),
            File.Exists(Path.Combine(profile, "crash.log")) ? File.ReadAllText(Path.Combine(profile, "crash.log"))[..Math.Min(300, (int)new FileInfo(Path.Combine(profile, "crash.log")).Length)] : "");
        Check("with no browser window", alone.MainWindowHandle == IntPtr.Zero);
        Check("without the page engine", !Directory.Exists(Path.Combine(profile, "EBWebView")));
        Console.WriteLine($"MEMORY  downloader started alone: {set / 1048576} MB in use");
        Check("light: under 100 MB in use", set is > 0 and < 100L * 1024 * 1024, $"{set / 1048576} MB");
        var second = new ProcessStartInfo(Environment.ProcessPath!, "--downloads --quiet") { UseShellExecute = false };
        second.Environment["VIREO_DATA"] = profile;
        using (var again = Process.Start(second)!)
        {
            var handed = again.WaitForExit(10000);
            Check("starting it again hands over to the running one", handed && again.ExitCode == 0 && !alone.HasExited);
        }
        try { alone.Kill(); alone.WaitForExit(5000); } catch { /* already gone */ }
    }

    private static IEnumerable<DownloadRow> FindRows(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is DownloadRow r) yield return r;
            foreach (var deeper in FindRows(child)) yield return deeper;
        }
    }
}
