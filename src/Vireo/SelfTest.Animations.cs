using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Vireo.Services;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>
    /// Every animation, measured: how many frames were drawn while it ran, and the longest wait between two of them. A smooth
    /// animation draws a frame every 16.7 ms (60 a second); a long gap is a visible stutter. Results go to animations.json.
    /// </summary>
    private async Task RunAnimationStepsAsync()
    {
        // Every job the window's thread runs that takes longer than about a frame, with the code it ran: what makes frames late.
        var slow = new System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, double WorstMs, double TotalMs)>();
        var startedAt = new Dictionary<System.Windows.Threading.DispatcherOperation, long>();
        var hooks = w.Dispatcher.Hooks;
        void Started(object? s, System.Windows.Threading.DispatcherHookEventArgs e) => startedAt[e.Operation] = Stopwatch.GetTimestamp();
        void Completed(object? s, System.Windows.Threading.DispatcherHookEventArgs e)
        {
            if (!startedAt.Remove(e.Operation, out var t0)) return;
            var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms < 12) return;
            var field = typeof(System.Windows.Threading.DispatcherOperation).GetField("_method", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var d = field?.GetValue(e.Operation) as Delegate;
            var name = d is null ? $"({e.Operation.Priority})" : $"{d.Method.DeclaringType?.FullName}.{d.Method.Name} ({e.Operation.Priority})";
            // A window message handled through WPF: which message, for which kind of window.
            var args = typeof(System.Windows.Threading.DispatcherOperation).GetField("_args", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(e.Operation);
            if (args is not null)
            {
                var t = args.GetType();
                var msg = t.GetField("msg", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(args);
                if (msg is not null) name += $" msg 0x{Convert.ToInt32(msg):X4}";
                else if (args is Action act)
                {
                    var box = act.Target?.GetType();
                    var machine = box?.GetGenericArguments().LastOrDefault() ?? box;
                    name += $" resuming {machine?.DeclaringType?.Name}.{machine?.Name}";
                }
                else name += $" args {t.Name}";
            }
            slow.AddOrUpdate(name, (1, ms, ms), (_, v) => (v.Count + 1, Math.Max(v.WorstMs, ms), v.TotalMs + ms));
        }
        hooks.OperationStarted += Started;
        hooks.OperationCompleted += Completed;

        var page = await w.OpenTabAsync(baseUrl + "/pages/file.html");
        await WaitFor(() => !page.IsLoading);
        await Task.Delay(800);
        var results = new List<object>();

        async Task Measure(string name, Action start, int ms, Action? after = null)
        {
            // Warm: the first time anything is drawn costs more than it ever will again.
            await Task.Delay(400);
            var stamps = new List<long>();
            void OnFrame(object? s, EventArgs e) => stamps.Add(Stopwatch.GetTimestamp());
            CompositionTarget.Rendering += OnFrame;
            var began = Stopwatch.GetTimestamp();
            start();
            await Task.Delay(ms);
            CompositionTarget.Rendering -= OnFrame;
            after?.Invoke();
            double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
            var gaps = new List<double>();
            var last = began;
            foreach (var t in stamps) { gaps.Add(Ms(last, t)); last = t; }
            // The first gap is how soon it starts (the work before the first frame); the others are the motion itself.
            var first = gaps.Count == 0 ? ms : gaps[0];
            var motion = gaps.Skip(1).ToList();
            var worstMotion = motion.Count == 0 ? 0 : motion.Max();
            var janky = motion.Count(g => g > 25); // a frame shown late enough to be seen as a stutter
            Console.WriteLine($"ANIM  {name,-22} {stamps.Count,3} frames, starts after {first,5:0.0} ms, worst gap while moving {worstMotion,5:0.0} ms, {janky} late frames");
            results.Add(new { name, frames = stamps.Count, firstMs = Math.Round(first, 1), worstMotionMs = Math.Round(worstMotion, 1), janky });
            await Task.Delay(300);
        }

        var rounds = int.TryParse(Environment.GetEnvironmentVariable("VIREO_ANIM_ROUNDS"), out var r) ? r : 5;
        for (var round = 0; round < rounds; round++)
        {
            await Measure("side panel opens", () => w.ShowDownloads(), 420);
            await Measure("side panel closes", () => w.CloseDrawer(), 380);
            await Measure("menu opens", () => w.ShowMenu(), 320, () => w.HideMenu());
            await Measure("notice appears", () => { w.ForegroundForTest = true; w.ShowToast("Download finished", "report.pdf"); }, 400, () => w.ForegroundForTest = null);
            Models.BrowserTab? added = null;
            await Measure("tab opens", () => { _ = w.OpenTabAsync(null).ContinueWith(t => added = t.Result, TaskScheduler.FromCurrentSynchronizationContext()); }, 400);
            if (added is not null) await Measure("tab closes", () => _ = w.CloseTabAsync(added), 380);
        }
        w.SetTabSide(TabSide.Left);
        await Task.Delay(800);
        for (var round = 0; round < rounds; round++)
        {
            await Measure("side tabs fold", () => w.ToggleCollapse(), 400);
            await Measure("side tabs unfold", () => w.ToggleCollapse(), 400);
        }
        w.SetTabSide(TabSide.Top);
        await Task.Delay(500);
        await w.CloseTabAsync(page);
        hooks.OperationStarted -= Started;
        hooks.OperationCompleted -= Completed;
        foreach (var (name, v) in slow.OrderByDescending(kv => kv.Value.TotalMs).Take(15))
            Console.WriteLine($"SLOW  {v.Count,4}x  worst {v.WorstMs,6:0.0} ms  total {v.TotalMs,7:0} ms  {name}");
        File.WriteAllText(Path.Combine(outDir, "animations.json"), System.Text.Json.JsonSerializer.Serialize(results));
    }
}
