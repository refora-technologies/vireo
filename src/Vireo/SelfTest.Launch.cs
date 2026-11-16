using System.Diagnostics;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>A link clicked in another program starts Vireo again: the running window must take it and the new process must leave.</summary>
    private async Task RunLaunchStepsAsync()
    {
        using var site = new PageSite().Html("/launched.html", "<!doctype html><title>Launched</title><body>from another program</body>");
        var tabs = w.AllTabs.Count;
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"\"{site.Base}/launched.html\"") { UseShellExecute = false };
        psi.Environment["VIREO_DATA"] = App.ProfileFolder();
        var watch = Stopwatch.StartNew();
        using var second = Process.Start(psi)!;
        var left = second.WaitForExit(15000);
        Check("starting Vireo again does not start a second browser", left && second.ExitCode == 0, left ? $"exit {second.ExitCode} after {watch.Elapsed.TotalSeconds:0.0}s" : "it kept running");
        if (!left) { try { second.Kill(); } catch { /* gone */ } }
        await WaitFor(() => w.AllTabs.Any(t => t.Url.EndsWith("/launched.html")), 10000);
        var tab = w.AllTabs.FirstOrDefault(t => t.Url.EndsWith("/launched.html"));
        Check("the running window opens the link", tab is not null && w.AllTabs.Count == tabs + 1, $"{tabs} -> {w.AllTabs.Count}");
        await WaitFor(() => tab?.Title == "Launched", 8000);
        Check("in front", tab is not null && w.Active == tab && tab.Title == "Launched", tab?.Title);

        // pressing the shortcut again with nothing to open just brings the window forward
        using var third = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, Environment = { ["VIREO_DATA"] = App.ProfileFolder() } })!;
        Check("starting it with no link just returns to the window", third.WaitForExit(15000) && third.ExitCode == 0 && w.AllTabs.Count == tabs + 1);
    }
}
