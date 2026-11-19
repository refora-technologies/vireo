using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Vireo.Downloads;

namespace Vireo.Services;

/// <summary>What the computer does once every download has finished.</summary>
public enum AfterDownloads { Nothing, Sleep, ShutDown }

/// <summary>
/// "When downloads finish: sleep / shut down", as download managers offer for overnight downloads. The choice lasts for this
/// run only and is never saved (a computer must not shut down because of something chosen weeks ago), it is used once, and
/// it always gives a minute's warning that can be cancelled. Windows is asked to sleep or shut down the normal way, so
/// programs (Vireo too) close properly and save what they have.
/// </summary>
public static class WhenDone
{
    public static readonly TimeSpan Warning = TimeSpan.FromSeconds(60);

    private static AfterDownloads s_choice;
    private static DateTime s_chosenAt;
    private static DispatcherTimer? s_timer;
    private static DownloadEngine? s_engine;

    public static AfterDownloads Choice => s_choice;
    public static event Action? Changed;

    /// <summary>Shows the warning and calls back with true to go ahead, false when cancelled. Set by the app (a window).</summary>
    public static Func<AfterDownloads, Task<bool>>? Warn;
    /// <summary>For tests: what would have been done, instead of doing it.</summary>
    internal static Action<AfterDownloads>? ActForTest;
    /// <summary>For tests: how often the downloads are looked at.</summary>
    internal static TimeSpan CheckEvery = TimeSpan.FromSeconds(2);

    public static void Choose(AfterDownloads choice, DownloadEngine engine)
    {
        s_choice = choice;
        s_chosenAt = DateTime.Now;
        s_engine = engine;
        s_runningThen = engine.Items.Where(i => i.IsActive).Select(i => i.Id).ToHashSet();
        s_timer?.Stop();
        s_timer = null;
        if (choice != AfterDownloads.Nothing)
        {
            s_timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CheckEvery };
            s_timer.Tick += async (_, _) => await CheckAsync();
            s_timer.Start();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Everything is finished when nothing is downloading or waiting, and at least one download ended since the choice was
    /// made: one that was running then, or one finished since (choosing "shut down" with nothing running must not shut down
    /// at once). Paused ones were stopped by the person and are not waited for.
    /// </summary>
    public static bool AllFinished(IEnumerable<DownloadItem> items, DateTime since, IReadOnlySet<string> runningThen)
    {
        var list = items.Where(i => i.Parent is null).ToList();
        if (list.Any(i => i.IsActive)) return false;
        return list.Any(i => i.State is DownloadState.Completed or DownloadState.Failed && runningThen.Contains(i.Id)
            || i.State == DownloadState.Completed && i.CompletedAt >= since);
    }

    private static HashSet<string> s_runningThen = [];

    /// <summary>A download started again (during the warning, say): the warning gives way.</summary>
    public static bool SomethingRunning => s_engine?.Items.Any(i => i.IsActive) == true;

    private static bool s_warning;

    private static async Task CheckAsync()
    {
        if (s_warning || s_engine is not { } engine || s_choice == AfterDownloads.Nothing) return;
        var items = engine.Items;
        if (!AllFinished(items, s_chosenAt, s_runningThen)) return;
        var what = s_choice;
        s_warning = true;
        try
        {
            var go = Warn is null || await Warn(what);
            // Used once: whatever happens next, it does not happen again by itself.
            Choose(AfterDownloads.Nothing, engine);
            if (go) Act(what);
        }
        finally { s_warning = false; }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

    private static void Act(AfterDownloads what)
    {
        if (ActForTest is { } test) { test(what); return; }
        try
        {
            if (what == AfterDownloads.Sleep) SetSuspendState(false, false, false);
            else if (what == AfterDownloads.ShutDown)
                Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { /* not allowed on this computer: nothing happens */ }
    }
}
