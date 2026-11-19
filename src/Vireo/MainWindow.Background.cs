using System.Windows;
using System.Windows.Threading;
using Vireo.Controls;
using Vireo.Downloads;
using Vireo.Services;

namespace Vireo;

/// <summary>
/// Downloading in the background: when the last window closes while downloads run, the browser can go away entirely (its
/// windows and the page engine with all its processes) while the download engine carries on in this same process, with an
/// icon in the notification area that shows how they are going. Opening Vireo again brings the browser back in this process,
/// so the downloads never stop; when they are all done, Vireo says so and exits.
/// </summary>
public partial class MainWindow
{
    private static TrayIcon? s_tray;
    private static TrayDownloadsWindow? s_trayWindow;
    private static DispatcherTimer? s_trayTimer;
    private static bool s_keepDownloading;
    private static DateTime? s_idleSince;

    /// <summary>Only the downloader is running (no browser windows).</summary>
    internal static bool InBackground => s_tray is not null;
    /// <summary>Windows is shutting down or signing out: close without asking, and without staying in the background.</summary>
    internal static bool EndingSession { get; set; }
    /// <summary>For tests: answers the closing question instead of a person.</summary>
    internal static Func<CloseChoice>? AnswerCloseQuestion;
    /// <summary>For tests: how long after the last download finishes Vireo exits by itself.</summary>
    internal static TimeSpan ExitWhenDoneAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The last window is closing while downloads run: keep them going in the background, stop them, or stay open.
    /// False means "do not close".
    /// </summary>
    private bool DecideClosingWithDownloads()
    {
        if (_private || EndingSession || s_windows.Count > 1 || s_keepDownloading) return true;
        var running = Downloads.Items.Count(i => i.State is DownloadState.Downloading or DownloadState.Queued);
        if (running == 0) return true;
        var choice = _settings.CloseWithDownloads switch
        {
            "background" => CloseChoice.Background,
            "stop" => CloseChoice.Stop,
            _ => Ask(running),
        };
        if (choice == CloseChoice.Cancel) return false;
        s_keepDownloading = choice == CloseChoice.Background;
        if (s_keepDownloading) Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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

    /// <summary>The last window has closed and the downloads go on: only the downloader stays, in the notification area.</summary>
    private static void EnterBackground(DownloadEngine engine)
    {
        s_keepDownloading = false;
        // The page engine goes with the windows; a browser opened again starts a new one.
        s_env = null;
        s_idleSince = null;
        s_tray = new TrayIcon("Vireo: downloading");
        s_tray.Clicked += () => ToggleTrayWindow(engine);
        s_trayTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        s_trayTimer.Tick += (_, _) => UpdateTray(engine);
        s_trayTimer.Start();
        UpdateTray(engine);
        s_tray.Balloon("Vireo keeps downloading", "The browser is closed. Click the Vireo icon here to see the downloads.");
        // Give the memory back: the windows, the pages and their engine are gone.
        _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Native.TrimWorkingSet();
        }, TaskScheduler.Default);
    }

    /// <summary>The tooltip, the "all done" message, and leaving once there is nothing left to do.</summary>
    private static void UpdateTray(DownloadEngine engine)
    {
        if (s_tray is null) return;
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

    private static void ToggleTrayWindow(DownloadEngine engine)
    {
        if (s_trayWindow is null)
        {
            s_trayWindow = new TrayDownloadsWindow(engine);
            s_trayWindow.OpenBrowserRequested += () => OpenBrowserFromBackground(null);
            s_trayWindow.QuitRequested += QuitFromBackground;
        }
        if (s_trayWindow.IsVisible) s_trayWindow.Hide();
        else s_trayWindow.ShowNearTray();
    }

    private static void LeaveBackground()
    {
        s_trayTimer?.Stop();
        s_trayTimer = null;
        s_tray?.Dispose();
        s_tray = null;
        if (s_trayWindow is { } w) { s_trayWindow = null; w.Close(); }
    }

    /// <summary>Back to the browser, in this process: the downloads keep running and show in its panel as before.</summary>
    internal static void OpenBrowserFromBackground(string? link)
    {
        if (!InBackground) return;
        LeaveBackground();
        Application.Current.ShutdownMode = ShutdownMode.OnLastWindowClose;
        var window = new MainWindow();
        Application.Current.MainWindow = window;
        window.Show();
        if (!string.IsNullOrWhiteSpace(link)) window.OpenFromOutside(link);
    }

    /// <summary>Ends the downloader: unfinished downloads are kept, and carry on the next time Vireo opens.</summary>
    internal static void QuitFromBackground()
    {
        LeaveBackground();
        s_downloads?.Dispose();
        s_downloads = null;
        Application.Current.Shutdown();
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
        if (s_downloads is not { } engine) return;
        if (s_trayWindow is null) { ToggleTrayWindow(engine); s_trayWindow?.Hide(); }
        s_trayWindow?.ShowOffScreenForTest();
    }
    internal static void HideTrayWindowForTest() => s_trayWindow?.Hide();
    internal static Window? TrayWindowForTest => s_trayWindow;

    /// <summary>For tests: what the tray icon says now.</summary>
    internal static string? TrayTip => s_tray?.Tip;
}
