using System.IO;
using System.Windows;
using System.Windows.Threading;
using Vireo.Controls;
using Vireo.Downloads;
using Vireo.Services;

namespace Vireo;

/// <summary>
/// The downloader on its own. When the last window closes while downloads run, the browser can go away entirely (its
/// windows and the page engine with all its processes) while the download engine carries on in this same process, with an
/// icon in the notification area that shows how they are going. Opening Vireo again brings the browser back in this process,
/// so the downloads never stop; when they are all done, Vireo says so and exits.
///
/// Someone who chose Vireo as their download manager keeps that icon all the time (like other download managers): the
/// downloader starts with Windows if they want ("Vireo.exe --downloads"), stays when the browser closes, offers links they
/// copy, and says when a download is finished, through Windows' own notifications, never over their work.
/// </summary>
public partial class MainWindow
{
    private static TrayIcon? s_tray;
    private static TrayDownloadsWindow? s_trayWindow;
    private static DispatcherTimer? s_trayTimer;
    private static ClipboardWatch? s_clipboard;
    private static bool s_keepDownloading;
    private static bool s_background;
    /// <summary>"Quit" from the notification area while the browser is open: everything closes, the downloader too.</summary>
    private static bool s_quitting;
    private static DateTime? s_idleSince;
    private static readonly HashSet<string> s_offered = new(StringComparer.Ordinal);
    private static readonly Dictionary<DownloadItem, DownloadState> s_trayStates = [];

    /// <summary>Only the downloader is running (no browser windows).</summary>
    internal static bool InBackground => s_background;
    /// <summary>Windows is shutting down or signing out: close without asking, and without staying in the background.</summary>
    internal static bool EndingSession { get; set; }
    /// <summary>For tests: answers the closing question instead of a person.</summary>
    internal static Func<CloseChoice>? AnswerCloseQuestion;
    /// <summary>For tests: how long after the last download finishes Vireo exits by itself.</summary>
    internal static TimeSpan ExitWhenDoneAfter = TimeSpan.FromSeconds(60);

    /// <summary>The person uses Vireo as their download manager: the downloader stays in the notification area.</summary>
    private static bool Resident => s_settings is { DownloadManager: true };

    /// <summary>
    /// The last window is closing while downloads run: keep them going in the background, stop them, or stay open.
    /// False means "do not close".
    /// </summary>
    private bool DecideClosingWithDownloads()
    {
        if (_private || EndingSession || s_quitting || s_windows.Count > 1 || s_keepDownloading) return true;
        // A download manager stays, downloads or not: no question.
        if (Resident) { KeepDownloaderAfterClosing(); return true; }
        var running = Downloads.Items.Count(i => i.State is DownloadState.Downloading or DownloadState.Queued);
        if (running == 0) return true;
        var choice = _settings.CloseWithDownloads switch
        {
            "background" => CloseChoice.Background,
            "stop" => CloseChoice.Stop,
            _ => Ask(running),
        };
        if (choice == CloseChoice.Cancel) return false;
        if (choice == CloseChoice.Background) KeepDownloaderAfterClosing();
        return true;

        CloseChoice Ask(int count)
        {
            if (AnswerCloseQuestion is { } answer) return answer();
            var dialog = new CloseDownloadsDialog(this, count);
            dialog.ShowDialog();
            if (dialog.Remember && dialog.Choice != CloseChoice.Cancel)
            {
                _settings.CloseWithDownloads = dialog.Choice == CloseChoice.Background ? "background" : "stop";
                _settings.Commit();
            }
            return dialog.Choice;
        }
    }

    private static void KeepDownloaderAfterClosing()
    {
        s_keepDownloading = true;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    /// <summary>
    /// "Vireo.exe --downloads": only the downloader, no browser and no page engine at all. From the Start menu it shows its
    /// window; at Windows start (--quiet) it just takes its place in the notification area.
    /// </summary>
    internal static void StartDownloaderOnly(bool quiet)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var folder = App.ProfileFolder();
        Directory.CreateDirectory(folder);
        var settings = s_settings ??= Settings.Load(folder);
        if (Environment.ProcessPath is { } exe) AutoStart.Refresh(exe, App.ProfileId);
        EnterBackground(SharedDownloads(folder, settings), announce: false);
        if (!quiet) ShowTrayWindow();
    }

    /// <summary>The download engine every normal window and the downloader share (one per profile, made once).</summary>
    private static DownloadEngine SharedDownloads(string dataFolder, Settings settings)
    {
        if (s_downloads is { } running) return running;
        var engine = new DownloadEngine(Path.Combine(dataFolder, "downloads"), OptionsFrom(settings));
        // Settings changed in any window apply to downloads at once (and stop applying once this engine is gone).
        settings.Changed += () => { if (s_downloads == engine) ApplyOptions(engine.Options, settings); };
        s_downloads = engine;
        Schedule.Use(engine, settings); // a "start at" time chosen earlier is kept
        return engine;
    }

    private static DownloadOptions OptionsFrom(Settings s)
    {
        var options = new DownloadOptions();
        ApplyOptions(options, s);
        return options;
    }

    private static void ApplyOptions(DownloadOptions o, Settings s)
    {
        o.AutoConnections = s.DownloadConnections == 0;
        o.Connections = s.DownloadConnections == 0 ? Settings.AutoConnectionsUpTo : s.DownloadConnections;
        o.MaxActive = s.MaxActiveDownloads;
        o.SpeedLimitKBps = s.SpeedLimitKBps;
        o.Folder = s.DownloadFolder;
        o.SortByType = s.SortByType;
    }

    /// <summary>The last window has closed and the downloads go on: only the downloader stays, in the notification area.</summary>
    private static void EnterBackground(DownloadEngine engine, bool announce = true)
    {
        s_keepDownloading = false;
        s_background = true;
        // The page engine goes with the windows; a browser opened again starts a new one.
        s_env = null;
        s_idleSince = null;
        EnsureTray(engine);
        UpdateTray(engine);
        var running = engine.Items.Any(i => i.IsActive);
        if (announce && running) s_tray!.Balloon("Vireo keeps downloading", "The browser is closed. Click the Vireo icon here to see the downloads.");
        else if (announce && Resident && s_settings is { TrayHintShown: false } s)
        {
            // Said once: where the downloader went.
            s.TrayHintShown = true;
            s.Commit();
            s_tray!.Balloon("Vireo Downloads is here", "It stays in the notification area for your downloads. Click the icon to see them.");
        }
        // Give the memory back: the windows, the pages and their engine are gone.
        _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Native.TrimWorkingSet();
        }, TaskScheduler.Default);
    }

    /// <summary>The icon in the notification area, with its timer and (when chosen) the clipboard watch.</summary>
    private static void EnsureTray(DownloadEngine engine)
    {
        if (s_tray is null)
        {
            s_tray = new TrayIcon("Vireo Downloads");
            s_tray.Clicked += ShowTrayWindow;
            s_trayStates.Clear();
            foreach (var i in engine.Items) s_trayStates[i] = i.State;
            s_trayTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
            s_trayTimer.Tick += (_, _) => { if (s_downloads is { } e) UpdateTray(e); };
            s_trayTimer.Start();
        }
        UpdateClipboardWatch();
    }

    /// <summary>Called when the download-manager choices change: the icon and the clipboard watch follow them.</summary>
    internal static void ApplyDownloadManagerChoices()
    {
        if (s_downloads is not { } engine) return;
        if (Resident || s_background || s_settings is { WatchClipboard: true }) EnsureTray(engine);
        else if (s_tray is not null) LeaveTray();
        UpdateClipboardWatch();
    }

    private static void UpdateClipboardWatch()
    {
        var want = s_tray is not null && s_settings is { WatchClipboard: true };
        if (want && s_clipboard is null)
        {
            s_clipboard = new ClipboardWatch();
            s_clipboard.LinkCopied += OfferCopiedLink;
        }
        else if (!want && s_clipboard is not null)
        {
            s_clipboard.Dispose();
            s_clipboard = null;
        }
    }

    /// <summary>
    /// A link to a file was copied in another program: "Download this?" as a Windows notification (it is the person's own
    /// action, so a notification is welcome; nothing ever pops up over their window). Each link is offered once.
    /// </summary>
    private static void OfferCopiedLink(Uri link)
    {
        if (s_tray is null || s_downloads is not { } engine) return;
        var url = FileHosts.DirectAddress(link) ?? link.ToString();
        if (!s_offered.Add(url)) return;
        if (engine.Items.Any(i => i.Url == url && i.State == DownloadState.Downloading)) return;
        var name = FileNaming.FromUrl(link.ToString());
        s_tray.Balloon("Download this?", $"{name} from {link.Host}. Click to download it with Vireo.", () =>
        {
            var item = engine.Add(new DownloadRequest(url), out var continued);
            s_tray?.Balloon(continued ? "Carrying on" : "Downloading", continued ? $"{item.FileName} carries on from where it stopped." : $"{name} is downloading. Click the Vireo icon to see it.");
        });
    }

    /// <summary>The tooltip, the "done" messages, and leaving once there is nothing left to do.</summary>
    private static void UpdateTray(DownloadEngine engine)
    {
        if (s_tray is null) return;
        NotifyFinished(engine);
        var items = engine.Items.Where(i => i.Parent is null).ToList();
        var active = items.Where(i => i.State is DownloadState.Downloading or DownloadState.Queued).ToList();
        var paused = items.Count(i => i.State == DownloadState.Paused);
        if (active.Count > 0)
        {
            s_idleSince = null;
            var total = active.Sum(i => Math.Max(0, i.Total));
            var done = active.Sum(i => i.Downloaded);
            var pct = total > 0 ? $" · {done * 100 / total}%" : "";
            var speed = active.Sum(i => i.Speed);
            s_tray.Tip = $"Vireo: {active.Count} downloading{pct}{(speed > 0 ? $" · {Format.Speed(speed)}" : "")}";
            return;
        }
        if (paused > 0) { s_tray.Tip = $"Vireo: {paused} paused"; s_idleSince = null; return; }
        if (Resident || !s_background)
        {
            // A download manager waits for the next download; with the browser open its windows say what finished.
            s_tray.Tip = "Vireo Downloads";
            s_idleSince = null;
            return;
        }
        if (s_idleSince is null)
        {
            s_idleSince = DateTime.Now;
            var failed = items.Count(i => i.State == DownloadState.Failed);
            s_tray.Tip = "Vireo: downloads finished";
            s_tray.Balloon(failed > 0 ? "Downloads finished, some with problems" : "Downloads finished",
                failed > 0 ? $"{failed} could not be finished. Open Vireo to retry." : "Everything is saved. Vireo closes in a minute.");
            return;
        }
        // Nothing left to do for a while, and nobody looking: Vireo ends, giving everything back.
        if (DateTime.Now - s_idleSince > ExitWhenDoneAfter && s_trayWindow is not { IsVisible: true }) QuitFromBackground();
    }

    /// <summary>
    /// A download manager says when each download is finished, with a click to show it in its folder. While a browser
    /// window is in front, the window's own notice does that instead.
    /// </summary>
    private static void NotifyFinished(DownloadEngine engine)
    {
        foreach (var item in engine.Items.Where(i => i.Parent is null))
        {
            var before = s_trayStates.TryGetValue(item, out var b) ? b : DownloadState.Queued;
            s_trayStates[item] = item.State;
            if (before == item.State || !Resident || s_tray is null) continue;
            if (!s_background && LastActive is { IsForeground: true }) continue;
            if (item.State == DownloadState.Completed)
                s_tray.Balloon("Download complete", $"{item.FileName}. Click to show it in its folder.", () => Format.ShowInFolder(item.FinalPath));
            else if (item.State == DownloadState.Failed)
                s_tray.Balloon("Download failed", $"{(item.FileName.Length > 0 ? item.FileName : item.Url)}: {item.Error}", ShowTrayWindow);
        }
    }

    private static void ShowTrayWindow()
    {
        if (s_downloads is not { } engine) return;
        if (s_trayWindow is null)
        {
            s_trayWindow = new TrayDownloadsWindow(engine);
            s_trayWindow.OpenBrowserRequested += () =>
            {
                s_trayWindow?.Hide();
                if (s_background) OpenBrowserFromBackground(null);
                else if (LastActive is { } w) w.BringForward();
            };
            s_trayWindow.QuitRequested += () =>
            {
                if (s_background) QuitFromBackground();
                else { s_quitting = true; s_trayWindow?.Hide(); foreach (var w in s_windows.ToList()) w.Close(); }
            };
        }
        s_trayWindow.Describe(s_background);
        if (s_trayWindow.IsVisible) s_trayWindow.Hide();
        else s_trayWindow.ShowNearTray();
    }

    /// <summary>Brings this window in front (from the notification area, or a second start of Vireo).</summary>
    internal void BringForward() => OpenFromOutside("");

    private static void LeaveTray()
    {
        s_trayTimer?.Stop();
        s_trayTimer = null;
        s_clipboard?.Dispose();
        s_clipboard = null;
        s_tray?.Dispose();
        s_tray = null;
        if (s_trayWindow is { } w) { s_trayWindow = null; w.Close(); }
    }

    /// <summary>Back to the browser, in this process: the downloads keep running and show in its panel as before.</summary>
    internal static void OpenBrowserFromBackground(string? link)
    {
        if (!InBackground) return;
        s_background = false;
        s_trayWindow?.Hide();
        // A download manager keeps its icon while the browser is open; otherwise it goes.
        if (!Resident && s_settings is not { WatchClipboard: true }) LeaveTray();
        Application.Current.ShutdownMode = ShutdownMode.OnLastWindowClose;
        var window = new MainWindow();
        Application.Current.MainWindow = window;
        window.Show();
        if (!string.IsNullOrWhiteSpace(link)) window.OpenFromOutside(link);
    }

    /// <summary>Ends the downloader: unfinished downloads are kept, and carry on the next time Vireo opens.</summary>
    internal static void QuitFromBackground()
    {
        s_background = false;
        LeaveTray();
        s_downloads?.Dispose();
        s_downloads = null;
        Application.Current.Shutdown();
    }

    /// <summary>"Vireo.exe --downloads" while Vireo already runs: its downloads come forward.</summary>
    internal static void ShowDownloadsFromOutside()
    {
        if (s_background) { if (s_trayWindow is not { IsVisible: true }) ShowTrayWindow(); return; }
        if (LastActive is { } w) { w.BringForward(); w.ShowDownloads(); }
    }

    /// <summary>For tests: the page engine's processes for this profile.</summary>
    internal async Task<List<int>> EngineProcessIdsAsync()
    {
        if (s_env is null) return [];
        var env = await s_env;
        return [.. env.GetProcessInfos().Select(p => p.ProcessId)];
    }

    /// <summary>For tests: the tray's downloads window, opened and closed without a click.</summary>
    internal static void ShowTrayWindowForTest()
    {
        if (s_downloads is null) return;
        if (s_trayWindow is null) { ShowTrayWindow(); s_trayWindow?.Hide(); }
        s_trayWindow?.ShowOffScreenForTest();
    }
    internal static void HideTrayWindowForTest() => s_trayWindow?.Hide();
    internal static Window? TrayWindowForTest => s_trayWindow;

    /// <summary>For tests: what the tray icon says now, and the last notification it showed.</summary>
    internal static string? TrayTip => s_tray?.Tip;
    internal static string? TrayLastBalloon => s_tray?.LastBalloon;
    internal static bool TrayShown => s_tray is not null;
    internal static bool ClipboardWatched => s_clipboard is not null;
    internal static void OfferCopiedLinkForTest(Uri link) => OfferCopiedLink(link);
    internal static void ClickBalloonForTest() => s_tray?.ClickBalloonForTest();
}
