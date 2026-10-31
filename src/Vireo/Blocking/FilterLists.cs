using System.IO;
using System.Net.Http;

namespace Vireo.Blocking;

/// <summary>The community filter lists: where they live, keeping a copy on disk, and refreshing it now and then.</summary>
public sealed class FilterLists(string folder)
{
    public sealed record Source(string Name, string Url, int Category, string File);

    public static readonly Source[] Community =
    [
        new("EasyList", "https://easylist.to/easylist/easylist.txt", 0, "easylist.txt"),
        new("EasyPrivacy", "https://easylist.to/easylist/easyprivacy.txt", 1, "easyprivacy.txt"),
        // Ad, tracker and device-telemetry servers as whole hosts (what uBlock Origin and AdGuard add on top of EasyList).
        new("Peter Lowe's ad servers", "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus&showintro=1&mimetype=plaintext", 0, "peter-lowe.txt"),
        new("AdGuard DNS filter", "https://adguardteam.github.io/AdGuardSDNSFilter/Filters/filter.txt", 1, "adguard-dns.txt"),
    ];

    private string PathOf(Source s) => Path.Combine(folder, s.File);

    public DateTime? LastUpdated => Community.Select(s => File.Exists(PathOf(s)) ? File.GetLastWriteTime(PathOf(s)) : (DateTime?)null).Min();

    /// <summary>True when a list is missing or older than <paramref name="age"/>.</summary>
    public bool NeedsUpdate(TimeSpan age) => Community.Any(s => !File.Exists(PathOf(s)) || DateTime.Now - File.GetLastWriteTime(PathOf(s)) > age);

    public IEnumerable<(Source Source, string Text)> LoadCached()
    {
        foreach (var s in Community)
        {
            string text;
            try { text = File.Exists(PathOf(s)) ? File.ReadAllText(PathOf(s)) : ""; }
            catch { text = ""; }
            if (text.Length > 0) yield return (s, text);
        }
    }

    /// <summary>Downloads each list. Returns true if at least one was saved.</summary>
    public async Task<bool> UpdateAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vireo/0.2 (filter list update)");
        var any = false;
        foreach (var s in Community)
        {
            try
            {
                var text = await http.GetStringAsync(s.Url, ct);
                // A truncated or error page must never replace a good list.
                if (text.Length < 20_000 || !(text.Contains("[Adblock", StringComparison.Ordinal) || text.Contains("! Title:", StringComparison.Ordinal))) continue;
                var tmp = PathOf(s) + ".tmp";
                await File.WriteAllTextAsync(tmp, text, ct);
                File.Move(tmp, PathOf(s), overwrite: true);
                any = true;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* offline: keep what we have */ }
        }
        return any;
    }

    /// <summary>Our own list, plus the community lists when asked.</summary>
    public static FilterEngine Build(string builtIn, IEnumerable<(Source Source, string Text)> community)
    {
        var engine = new FilterEngine();
        engine.AddList(builtIn, 0);
        foreach (var (source, text) in community) engine.AddList(text, source.Category);
        return engine;
    }
}
