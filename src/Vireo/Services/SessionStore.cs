using System.IO;
using System.Text.Json;

namespace Vireo.Services;

public sealed record SessionTab(string Url, string Title, bool Pinned);

/// <summary>The tabs that were open, so the next start can carry on where this one stopped.</summary>
public sealed record SessionState(List<SessionTab> Tabs, int Active, List<SessionWindow>? More = null);

/// <summary>Another window of the same session (the first window's tabs are the session's own).</summary>
public sealed record SessionWindow(List<SessionTab> Tabs, int Active);

public static class SessionStore
{
    public const int MaxTabs = 100;

    /// <summary>Only real pages are brought back; the start page, blank tabs and browser-internal addresses are not.</summary>
    public static bool Restorable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "file";

    /// <summary>What to save for these tabs. <paramref name="active"/> is the position of the tab being looked at (or -1).</summary>
    public static SessionState Snapshot(IEnumerable<(string Url, string Title, bool Pinned)> tabs, int active)
    {
        var kept = new List<SessionTab>();
        var keptActive = 0;
        var i = 0;
        foreach (var (url, title, pinned) in tabs)
        {
            if (Restorable(url) && kept.Count < MaxTabs)
            {
                if (i == active) keptActive = kept.Count;
                kept.Add(new SessionTab(url, title ?? "", pinned));
            }
            i++;
        }
        return new SessionState(kept, keptActive);
    }

    public static string ToJson(SessionState state) => JsonSerializer.Serialize(state);

    public static void Save(string path, SessionState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson(state));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* the session is a convenience; never stop the browser over it */ }
    }

    /// <summary>The saved session, or null when there is none or it is damaged.</summary>
    public static SessionState? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path));
            if (state?.Tabs is null) return null;
            var tabs = state.Tabs.Where(t => t is not null && Restorable(t.Url)).Take(MaxTabs).ToList();
            var more = state.More?.Select(w => (w, t: (w.Tabs ?? []).Where(t => t is not null && Restorable(t.Url)).Take(MaxTabs).ToList()))
                .Where(x => x.t.Count > 0).Select(x => new SessionWindow(x.t, Math.Clamp(x.w.Active, 0, x.t.Count - 1))).Take(10).ToList();
            return new SessionState(tabs, Math.Clamp(state.Active, 0, Math.Max(0, tabs.Count - 1)), more is { Count: > 0 } ? more : null);
        }
        catch { return null; }
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); } catch { /* not there */ }
    }
}
