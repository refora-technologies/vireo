using System.Diagnostics;
using System.Windows.Threading;

namespace Vireo.Services;

/// <summary>
/// Notes what the window's thread is busy with, cheaply: a fixed ring of "started X" / "finished X" marks. When the window
/// stops answering, the marks that were started and not finished say what it was doing.
/// </summary>
public static class Breadcrumbs
{
    private const int Size = 64;
    private static readonly (long At, string What, bool Start)[] Ring = new (long, string, bool)[Size];
    private static int _next;

    public readonly struct Mark(string what) : IDisposable
    {
        public void Dispose() => Add(what, false);
    }

    /// <summary><c>using var _ = Breadcrumbs.Scope("loading a page")</c> marks the start now and the end when the block is left.</summary>
    public static Mark Scope(string what)
    {
        Add(what, true);
        return new Mark(what);
    }

    private static void Add(string what, bool start)
    {
        var i = Interlocked.Increment(ref _next) % Size;
        Ring[i] = (Stopwatch.GetTimestamp(), what, start);
    }

    /// <summary>What was started and has not finished, oldest first, with how long ago it started.</summary>
    public static List<string> Open()
    {
        var snapshot = new List<(long At, string What, bool Start)>();
        var end = Volatile.Read(ref _next);
        for (var k = end - Size + 1; k <= end; k++)
        {
            var e = Ring[((k % Size) + Size) % Size];
            if (e.What is not null) snapshot.Add(e);
        }
        snapshot.Sort((a, b) => a.At.CompareTo(b.At));
        var open = new List<(long At, string What)>();
        foreach (var e in snapshot)
        {
            if (e.Start) open.Add((e.At, e.What));
            else { var i = open.FindLastIndex(o => o.What == e.What); if (i >= 0) open.RemoveAt(i); }
        }
        var now = Stopwatch.GetTimestamp();
        return [.. open.Select(o => $"{o.What} ({(now - o.At) * 1000 / Stopwatch.Frequency} ms ago)")];
    }
}

/// <summary>
/// Watches the window's thread from another thread. A window that does not answer for a moment feels stuck; one that does not
/// answer for seconds is "Not responding". Every stall is measured, and long ones are written to the log with what the thread was doing.
/// </summary>
public sealed class UiWatchdog : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly object _lock = new();
    private readonly List<(DateTime At, double Ms, string Doing)> _stalls = [];

    /// <summary>A delay this long is reported (the eye notices about a tenth of a second; Windows says "Not responding" at five seconds).</summary>
    public static readonly TimeSpan StallLimit = TimeSpan.FromMilliseconds(700);
    public double WorstMs { get; private set; }

    public UiWatchdog(Dispatcher ui)
    {
        _ui = ui;
        _thread = new Thread(Run) { IsBackground = true, Name = "Vireo UI watchdog", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public IReadOnlyList<(DateTime At, double Ms, string Doing)> Stalls { get { lock (_lock) return [.. _stalls]; } }
    public void Reset() { lock (_lock) { _stalls.Clear(); WorstMs = 0; } }

    private void Run()
    {
        while (!_stop)
        {
            Thread.Sleep(200);
            var answered = new ManualResetEventSlim();
            var asked = Stopwatch.StartNew();
            // Input priority: the same level as a click. If this waits, so would the person.
            _ui.BeginInvoke(DispatcherPriority.Input, () => answered.Set());
            string? doing = null;
            if (!answered.Wait(StallLimit))
            {
                doing = string.Join(" > ", Breadcrumbs.Open().TakeLast(4));
                while (!_stop && !answered.Wait(250)) { }
            }
            var ms = asked.Elapsed.TotalMilliseconds;
            lock (_lock)
            {
                if (ms > WorstMs) WorstMs = ms;
                if (doing is not null)
                {
                    _stalls.Add((DateTime.Now, ms, doing.Length > 0 ? doing : "(nothing marked)"));
                    if (_stalls.Count > 200) _stalls.RemoveAt(0);
                }
            }
            if (doing is not null && ms > 2000) App.Log("Not responding", new TimeoutException($"The window did not answer for {ms / 1000:0.0} s while: {doing}"));
        }
    }

    public void Dispose() => _stop = true;
}
