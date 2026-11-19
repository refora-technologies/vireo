using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Vireo.Services;

namespace Vireo;

/// <summary>
/// Drives the real browser: opens pages, uses the keyboard, freezes and frees tabs, measures memory
/// and takes screenshots. Run with: Vireo.exe --selftest OUTDIR BASEURL
/// </summary>
internal sealed partial class SelfTest(MainWindow w, string outDir, string baseUrl)
{
    private readonly List<object> _results = [];
    private bool _failed;

    public static bool TryParse(string[] args, out string outDir, out string baseUrl)
    {
        var i = Array.IndexOf(args, "--selftest");
        outDir = i >= 0 && args.Length > i + 1 ? args[i + 1] : "";
        baseUrl = i >= 0 && args.Length > i + 2 ? args[i + 2] : "";
        return i >= 0 && outDir.Length > 0 && baseUrl.Length > 0;
    }

    private void Check(string name, bool ok, string detail = "")
    {
        _results.Add(new { name, ok, detail });
        if (!ok) _failed = true;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private async Task Shot(string name)
    {
        await Task.Delay(500); // let animations settle
        var (x, y, width, height) = Native.WindowBounds(w);
        try
        {
            using var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, bmp.Size);
            Directory.CreateDirectory(outDir);
            bmp.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
        }
        catch (Exception e) { Console.WriteLine($"SKIP  screenshot {name} (the screen could not be read: {e.Message})"); } // a locked screen or a security prompt on top
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0} MB";

    public async Task RunAsync()
    {
        // Whatever steps run, and in whatever order, nothing the test downloads ever lands in the person's own Downloads folder.
        var testDownloads = Path.GetFullPath(Path.Combine(outDir, "test-downloads"));
        Directory.CreateDirectory(testDownloads);
        w.Settings.DownloadFolder = testDownloads;
        w.Settings.Commit();
        // Nor does "start with Windows" ever touch Windows' real Run key, and the notification area's messages stay silent.
        MainWindow.AutoStartKey = TestRunKey;
        TrayIcon.SilentForTest = true;
        try { await RunStepsAsync(); }
        catch (Exception e) { Check("no crash", false, e.ToString()); }
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "selftest.json"), JsonSerializer.Serialize(_results, new JsonSerializerOptions { WriteIndented = true }));
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\VireoSelfTest", throwOnMissingSubKey: false); } catch { /* nothing to clean */ }
        Application.Current.Shutdown(_failed ? 1 : 0);
    }

    private async Task RunStepsAsync()
    {
        w.Topmost = true; // so screenshots show the browser, not whatever is in front of it
        w.Left = 60; w.Top = 40; w.Width = 1280; w.Height = 820;
        await w.Ready;

        // 1. Start page
        var start = w.Active!;
        await WaitFor(() => start.IsStartPage && start.Title == "New tab");
        Check("start page opens", start.IsStartPage && start.Title == "New tab", $"title='{start.Title}'");
        await Shot("01-start-page");

        // 2. Real pages in tabs
        var urls = new[] { "/pages/file.html", "/pages/mixed.html", "/pages/spa.html?v=1", "/pages/hls.html" };
        var tabs = new List<Models.BrowserTab>();
        foreach (var u in urls)
        {
            var t = await w.OpenTabAsync(baseUrl + u);
            await WaitFor(() => !t.IsLoading && t.Title.Length > 0);
            tabs.Add(t);
        }
        Check("pages load with their titles", tabs.All(t => t.Title.Contains("test", StringComparison.OrdinalIgnoreCase)), string.Join(" | ", tabs.Select(t => t.Title)));
        Check("the newest tab is showing", w.Active == tabs[^1]);
        await Shot("02-pages-open");

        // 3. Address bar: typed text becomes an address
        w.NavigateActive(new Uri(baseUrl).Authority + "/pages/slow.html");
        await WaitFor(() => w.Active!.Url.EndsWith("/pages/slow.html") && !w.Active.IsLoading);
        Check("typed address opens", w.Active!.Url == baseUrl + "/pages/slow.html", w.Active.Url);

        // 4. Keyboard
        var before = w.AllTabs.Count;
        w.HandleShortcut(Key.T, ModifierKeys.Control);
        await WaitFor(() => w.AllTabs.Count == before + 1 && w.Active!.IsStartPage);
        Check("Ctrl+T opens a tab", w.AllTabs.Count == before + 1 && w.Active!.IsStartPage);
        w.HandleShortcut(Key.W, ModifierKeys.Control);
        await WaitFor(() => w.AllTabs.Count == before);
        Check("Ctrl+W closes it", w.AllTabs.Count == before);
        var closing = w.Active!;
        var closingUrl = closing.Url;
        w.HandleShortcut(Key.W, ModifierKeys.Control);
        await WaitFor(() => w.AllTabs.Count == before - 1);
        w.HandleShortcut(Key.T, ModifierKeys.Control | ModifierKeys.Shift);
        await WaitFor(() => w.AllTabs.Count == before && w.Active!.Url == closingUrl);
        Check("Ctrl+Shift+T brings back the closed page", w.AllTabs.Count == before && w.Active!.Url == closingUrl, closingUrl);
        w.HandleShortcut(Key.D1, ModifierKeys.Control);
        await WaitFor(() => w.Active == w.AllTabs[0]);
        Check("Ctrl+1 goes to the first tab", w.Active == w.AllTabs[0]);

        // 5. Memory: everything awake, then freeze, then free
        await Task.Delay(1500);
        var awake = w.MeasureMemory();
        var target = w.AllTabs[0];
        await w.ActivateAsync(target);
        w.Suspension = new SuspensionSettings(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        foreach (var t in w.AllTabs.Where(t => t != target)) t.HiddenSince = DateTime.UtcNow - TimeSpan.FromSeconds(5);
        await w.SweepNowAsync();
        var hidden = w.AllTabs.Where(t => t != target).ToList();
        Check("hidden tabs are frozen", hidden.All(t => t.Life == TabLife.Suspended), string.Join(",", hidden.Select(t => t.Life)));
        await Task.Delay(1500);
        var frozen = w.MeasureMemory();

        // Music must not be interrupted.
        var music = hidden[0];
        music.IsPlayingAudio = true;
        foreach (var t in hidden) t.HiddenSince = DateTime.UtcNow - TimeSpan.FromMinutes(30);
        await w.SweepNowAsync();
        Check("a tab playing sound is left alone", music.Life == TabLife.Suspended && music.View is not null);
        Check("the others are freed", hidden.Skip(1).All(t => t.Life == TabLife.Discarded && t.View is null), string.Join(",", hidden.Skip(1).Select(t => t.Life)));
        music.IsPlayingAudio = false;
        await Task.Delay(2500);
        var freed = w.MeasureMemory();
        Console.WriteLine($"MEMORY  all awake: {Mb(awake.Bytes)} ({awake.Processes} processes)  frozen: {Mb(frozen.Bytes)}  freed: {Mb(freed.Bytes)} ({freed.Processes} processes)");
        _results.Add(new { name = "memory", awake = Mb(awake.Bytes), awakeProcesses = awake.Processes, frozen = Mb(frozen.Bytes), freed = Mb(freed.Bytes), freedProcesses = freed.Processes });
        Check("freeing tabs lowers memory", freed.Bytes < awake.Bytes, $"{Mb(awake.Bytes)} -> {Mb(freed.Bytes)}");
        await Shot("03-tabs-asleep");

        // 6. Waking a freed tab brings its page back
        var back = hidden[^1];
        var title = back.Title;
        await w.ActivateAsync(back);
        await WaitFor(() => back.View is not null && !back.IsLoading && back.Title.Length > 0 && back.Life == TabLife.Active);
        Check("a freed tab wakes with its page", back.Life == TabLife.Active && back.Title == title, $"'{title}' -> '{back.Title}'");
        // And a frozen one resumes.
        await w.ActivateAsync(music);
        Check("a frozen tab resumes", music.Life == TabLife.Active && music.View is not null);

        // 7. Menu and memory panel
        w.ShowMenu();
        await Task.Delay(400);
        Check("menu shows memory", w.MemoryText.Contains("MB"), w.MemoryText);
        await Shot("04-menu");
        w.HideMenu();

        await Step("layout", RunLayoutStepsAsync);
        await Step("downloads", RunDownloadStepsAsync);
        await Step("blocking", RunBlockingStepsAsync);
        await Step("privacy", RunPrivacyStepsAsync);
        await Step("library", RunLibraryStepsAsync);
        await Step("browsing", RunBrowsingStepsAsync);
        await Step("search", RunSearchStepsAsync);
        await Step("hostile", RunHostileStepsAsync);
        await Step("extensions", RunExtensionStepsAsync);
        await Step("launch", RunLaunchStepsAsync);
        await Step("streams", RunStreamStepsAsync);
        await Step("hidden", RunHiddenStreamStepsAsync);
        await Step("gestures", RunGestureStepsAsync);
        await Step("windows", RunWindowStepsAsync);
        await Step("profiles", RunProfileAndSyncStepsAsync);
        await Step("media", RunMediaStepsAsync);
        await Step("youtube", RunYouTubeStepsAsync);
        await Step("home", RunHomeStepsAsync);
        await Step("stress", RunStressStepsAsync);
        if (Only?.Contains("adtest") == true) await Step("adtest", RunAdTestStepsAsync);
        if (Only?.Contains("sites") == true) await Step("sites", RunSiteSurveyAsync);
        if (Only?.Contains("freezes") == true) await Step("freezes", RunFreezeStepsAsync);
        if (Only?.Contains("animations") == true) await Step("animations", RunAnimationStepsAsync);
        await Step("welcome", RunWelcomeStepsAsync);
        await Step("manager", RunDownloadManagerStepsAsync);
        await Step("settingslook", RunSettingsLookStepsAsync);

        // 8. Many tabs share the width
        for (var i = 0; i < 9; i++) await w.OpenTabAsync(null, activate: false);
        await Shot("05-many-tabs");
        Check("many tabs still fit", w.TabStripWidth <= w.ActualWidth, $"{w.AllTabs.Count} tabs, strip {w.TabStripWidth:0}px of {w.ActualWidth:0}px");

        // 9. Window buttons
        w.WindowState = WindowState.Maximized;
        await Task.Delay(500);
        await Shot("06-maximized");
        w.WindowState = WindowState.Normal;
        Check("window maximizes and restores", w.WindowState == WindowState.Normal);

        // 10. It kills the browser engine, which every other test would notice
        await RunRecoveryStepsAsync();

        // 11. Last of all: it closes the test's window (the browser goes, only the downloader stays, then the browser comes back).
        await Step("background", RunBackgroundStepsAsync);
    }
}
