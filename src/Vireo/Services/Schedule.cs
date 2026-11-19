using System.Windows.Threading;
using Vireo.Downloads;

namespace Vireo.Services;

/// <summary>
/// "Start at": paused downloads start by themselves at a chosen time (at night, or tomorrow when a file host's daily limit
/// has passed). Kept in the settings, so it survives a restart; a time that passed while Vireo was closed still starts them
/// if it was within the last 12 hours, otherwise it is dropped. Used once, then cleared.
/// </summary>
public static class Schedule
{
    public static readonly TimeSpan StillWorthIt = TimeSpan.FromHours(12);

    private static DispatcherTimer? s_timer;
    private static DownloadEngine? s_engine;
    private static Settings? s_settings;

    public static DateTime? At => s_settings?.ScheduledStart;
    public static event Action? Changed;
    /// <summary>For tests: what "now" is.</summary>
    internal static Func<DateTime> Now = () => DateTime.Now;

    /// <summary>The engine and settings it works with (again whenever the engine is made anew).</summary>
    public static void Use(DownloadEngine engine, Settings settings)
    {
        s_engine = engine;
        s_settings = settings;
        Arm();
    }

    public static void Set(DateTime? at)
    {
        if (s_settings is null) return;
        s_settings.ScheduledStart = at;
        s_settings.Commit();
        Arm();
        Changed?.Invoke();
    }

    /// <summary>What a scheduled start would start: everything paused (and what failed because a host's limit was reached).</summary>
    public static List<DownloadItem> Waiting(DownloadEngine engine) =>
        engine.Items.Where(i => i.Parent is null && (i.State == DownloadState.Paused || i.State == DownloadState.Failed && i.Error?.Contains("limit", StringComparison.OrdinalIgnoreCase) == true)).ToList();

    private static void Arm()
    {
        s_timer?.Stop();
        s_timer = null;
        if (At is null) return;
        s_timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        s_timer.Tick += (_, _) => CheckNow();
        s_timer.Start();
        CheckNow();
    }

    /// <summary>Starts the waiting downloads if the time has come.</summary>
    internal static void CheckNow()
    {
        if (At is not { } at || s_engine is not { } engine) return;
        var late = Now() - at;
        if (late < TimeSpan.Zero) return;
        Set(null);
        if (late > StillWorthIt) return; // long past: starting downloads out of the blue now would surprise
        foreach (var item in Waiting(engine)) engine.Resume(item);
    }

    /// <summary>The next time it is <paramref name="hour"/>:00 (today if still ahead, else tomorrow).</summary>
    public static DateTime Next(int hour, DateTime? now = null)
    {
        var n = now ?? Now();
        var today = n.Date.AddHours(hour);
        return today > n ? today : today.AddDays(1);
    }
}
