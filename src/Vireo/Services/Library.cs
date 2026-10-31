using System.IO;
using System.Text.Json;

namespace Vireo.Services;

public sealed record Bookmark(string Url, string Title, DateTime AddedAt);

public sealed record HistoryEntry(string Url, string Title, DateTime LastVisit, int Visits);

public sealed record SyncBookmark(string Url, string Title, DateTime AddedUtc);
public sealed record SyncHistory(string Url, string Title, DateTime VisitedUtc, int Visits);

/// <summary>A computer's bookmarks, history and deletions, as shared through sync (all times in UTC).</summary>
public sealed record SyncLibrary(List<SyncBookmark> Bookmarks, List<SyncHistory> History,
    Dictionary<string, DateTime> BookmarkRemovals, Dictionary<string, DateTime> HistoryRemovals, DateTime? HistoryClearedAt);

/// <summary>A suggestion for the address bar.</summary>
public sealed record Suggestion(string Url, string Title, bool IsBookmark, bool IsSearch = false);

/// <summary>Bookmarks and history, kept in two small files in the profile folder.</summary>
public sealed class Library : IDisposable
{
    internal const int HistoryLimit = 20000;
    private readonly object _lock = new();
    private readonly string? _folder;
    private readonly Timer _saver;
    private bool _dirty;
    private List<Bookmark> _bookmarks = [];
    private List<HistoryEntry> _history = [];
    // What was deleted, and when (UTC), so a deletion on one computer reaches the others through sync instead of being undone.
    private Removals _removed = new();

    public sealed class Removals
    {
        public Dictionary<string, DateTime> Bookmarks { get; set; } = [];
        public Dictionary<string, DateTime> History { get; set; } = [];
        public DateTime? HistoryClearedAt { get; set; }
    }

    public event Action? Changed;

    /// <summary>With no folder nothing is written to disk (a private window).</summary>
    public Library(string? folder)
    {
        _folder = folder;
        _saver = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        if (folder is null) return;
        _bookmarks = Read<Bookmark>("bookmarks.json");
        _history = Read<HistoryEntry>("history.json");
        try
        {
            var path = Path.Combine(folder, "removals.json");
            if (File.Exists(path)) _removed = JsonSerializer.Deserialize<Removals>(File.ReadAllText(path)) ?? new();
        }
        catch { _removed = new(); }
    }

    public IReadOnlyList<Bookmark> Bookmarks { get { lock (_lock) return [.. _bookmarks.OrderByDescending(b => b.AddedAt)]; } }
    public IReadOnlyList<HistoryEntry> History { get { lock (_lock) return [.. _history.OrderByDescending(h => h.LastVisit)]; } }

    /// <summary>Two addresses are the same page if they differ only by the part after "#".</summary>
    public static string Key(string url)
    {
        var hash = url.IndexOf('#');
        return (hash < 0 ? url : url[..hash]).TrimEnd('/');
    }

    // ---- Bookmarks ----

    public bool IsBookmarked(string url)
    {
        var key = Key(url);
        lock (_lock) return _bookmarks.Any(b => Key(b.Url) == key);
    }

    /// <summary>Adds the bookmark, or removes it when it is already there. Returns true if it is now bookmarked.</summary>
    public bool ToggleBookmark(string url, string title)
    {
        var key = Key(url);
        bool now;
        lock (_lock)
        {
            var existing = _bookmarks.FindIndex(b => Key(b.Url) == key);
            if (existing >= 0) { _bookmarks.RemoveAt(existing); _removed.Bookmarks[key] = DateTime.UtcNow; now = false; }
            else { _bookmarks.Add(new Bookmark(url, string.IsNullOrWhiteSpace(title) ? url : title, DateTime.Now)); now = true; }
        }
        Touched();
        return now;
    }

    public void RemoveBookmark(string url)
    {
        var key = Key(url);
        lock (_lock) { _bookmarks.RemoveAll(b => Key(b.Url) == key); _removed.Bookmarks[key] = DateTime.UtcNow; }
        Touched();
    }

    // ---- Bringing in what another browser had ----

    /// <summary>Adds bookmarks and history from elsewhere. What is already here is kept (history takes the larger count and the later date).</summary>
    public (int Bookmarks, int History) Import(IEnumerable<Bookmark> bookmarks, IEnumerable<HistoryEntry> history)
    {
        int addedBookmarks = 0, addedHistory = 0;
        lock (_lock)
        {
            var known = _bookmarks.Select(b => Key(b.Url)).ToHashSet();
            foreach (var b in bookmarks)
                if (known.Add(Key(b.Url))) { _bookmarks.Add(b); addedBookmarks++; }

            var index = new Dictionary<string, int>();
            for (var i = 0; i < _history.Count; i++) index[Key(_history[i].Url)] = i;
            foreach (var h in history)
            {
                var key = Key(h.Url);
                if (index.TryGetValue(key, out var at))
                    _history[at] = _history[at] with { Visits = Math.Max(_history[at].Visits, h.Visits), LastVisit = h.LastVisit > _history[at].LastVisit ? h.LastVisit : _history[at].LastVisit };
                else { index[key] = _history.Count; _history.Add(h); addedHistory++; }
            }
            if (_history.Count > HistoryLimit) _history = [.. _history.OrderByDescending(h => h.LastVisit).Take(HistoryLimit)];
        }
        if (addedBookmarks + addedHistory > 0) Touched();
        return (addedBookmarks, addedHistory);
    }

    // ---- History ----

    public void Visit(string url, string title, DateTime? at = null)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        var key = Key(url);
        lock (_lock)
        {
            var i = _history.FindIndex(h => Key(h.Url) == key);
            if (i >= 0) _history[i] = _history[i] with { Title = string.IsNullOrWhiteSpace(title) ? _history[i].Title : title, LastVisit = at ?? DateTime.Now, Visits = _history[i].Visits + 1 };
            else _history.Add(new HistoryEntry(url, string.IsNullOrWhiteSpace(title) ? url : title, at ?? DateTime.Now, 1));
            if (_history.Count > HistoryLimit) { var oldest = 0; for (var j = 1; j < _history.Count; j++) if (_history[j].LastVisit < _history[oldest].LastVisit) oldest = j; _history.RemoveAt(oldest); } // one visit adds at most one entry: drop the oldest, no sorting on every page
        }
        Touched();
    }

    /// <summary>A page's title often arrives after the page itself.</summary>
    public void UpdateTitle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var key = Key(url);
        lock (_lock)
        {
            var i = _history.FindIndex(h => Key(h.Url) == key);
            if (i < 0) return;
            _history[i] = _history[i] with { Title = title };
        }
        Touched();
    }

    public void RemoveHistory(string url)
    {
        var key = Key(url);
        lock (_lock) { _history.RemoveAll(h => Key(h.Url) == key); _removed.History[key] = DateTime.UtcNow; }
        Touched();
    }

    public void ClearHistory()
    {
        lock (_lock) { _history.Clear(); _removed.History.Clear(); _removed.HistoryClearedAt = DateTime.UtcNow; }
        Touched();
    }

    // ---- Sync: what this computer has, and taking in what the others have ----

    /// <summary>Everything to share with other computers (times in UTC). History is the most recent pages only.</summary>
    public SyncLibrary ForSync(int historyLimit = 5000)
    {
        lock (_lock)
        {
            return new SyncLibrary(
                [.. _bookmarks.Select(b => new SyncBookmark(b.Url, b.Title, b.AddedAt.ToUniversalTime()))],
                [.. _history.OrderByDescending(h => h.LastVisit).Take(historyLimit).Select(h => new SyncHistory(h.Url, h.Title, h.LastVisit.ToUniversalTime(), h.Visits))],
                new Dictionary<string, DateTime>(_removed.Bookmarks), new Dictionary<string, DateTime>(_removed.History), _removed.HistoryClearedAt);
        }
    }

    /// <summary>
    /// Takes in what other computers have. The newest word wins: a bookmark added after it was deleted elsewhere stays, one
    /// deleted after it was added goes, and the same for history (and "clear all history" clears everything visited before it).
    /// Returns how many things changed here.
    /// </summary>
    public int MergeFromSync(IEnumerable<SyncLibrary> others)
    {
        var changed = 0;
        lock (_lock)
        {
            // Everyone's deletions, newest of each
            foreach (var o in others)
            {
                foreach (var (k, t) in o.BookmarkRemovals) if (!_removed.Bookmarks.TryGetValue(k, out var mine) || t > mine) _removed.Bookmarks[k] = t;
                foreach (var (k, t) in o.HistoryRemovals) if (!_removed.History.TryGetValue(k, out var mine) || t > mine) _removed.History[k] = t;
                if (o.HistoryClearedAt is { } c && (_removed.HistoryClearedAt is null || c > _removed.HistoryClearedAt)) _removed.HistoryClearedAt = c;
            }
            bool BookmarkGone(string key, DateTime addedUtc) => _removed.Bookmarks.TryGetValue(key, out var t) && t >= addedUtc;
            bool HistoryGone(string key, DateTime visitedUtc) =>
                (_removed.History.TryGetValue(key, out var t) && t >= visitedUtc) || (_removed.HistoryClearedAt is { } c && c >= visitedUtc);

            changed += _bookmarks.RemoveAll(b => BookmarkGone(Key(b.Url), b.AddedAt.ToUniversalTime()));
            changed += _history.RemoveAll(h => HistoryGone(Key(h.Url), h.LastVisit.ToUniversalTime()));

            var bookmarks = _bookmarks.Select(b => Key(b.Url)).ToHashSet();
            var history = new Dictionary<string, int>();
            for (var i = 0; i < _history.Count; i++) history[Key(_history[i].Url)] = i;
            foreach (var o in others)
            {
                foreach (var b in o.Bookmarks)
                {
                    var key = Key(b.Url);
                    if (!b.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || BookmarkGone(key, b.AddedUtc) || !bookmarks.Add(key)) continue;
                    _bookmarks.Add(new Bookmark(b.Url, b.Title, b.AddedUtc.ToLocalTime()));
                    changed++;
                }
                foreach (var h in o.History)
                {
                    var key = Key(h.Url);
                    if (!h.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || HistoryGone(key, h.VisitedUtc)) continue;
                    var when = h.VisitedUtc.ToLocalTime();
                    if (history.TryGetValue(key, out var at))
                    {
                        var mine = _history[at];
                        if (when <= mine.LastVisit && h.Visits <= mine.Visits) continue;
                        _history[at] = mine with { LastVisit = when > mine.LastVisit ? when : mine.LastVisit, Visits = Math.Max(mine.Visits, h.Visits), Title = when > mine.LastVisit && h.Title.Length > 0 ? h.Title : mine.Title };
                    }
                    else { history[key] = _history.Count; _history.Add(new HistoryEntry(h.Url, h.Title, when, Math.Max(1, h.Visits))); }
                    changed++;
                }
            }
            if (_history.Count > HistoryLimit) _history = [.. _history.OrderByDescending(h => h.LastVisit).Take(HistoryLimit)];
            // Deletions older than a quarter are dropped: every computer has long since heard of them.
            var cutoff = DateTime.UtcNow.AddDays(-90);
            foreach (var k in _removed.Bookmarks.Where(x => x.Value < cutoff).Select(x => x.Key).ToList()) _removed.Bookmarks.Remove(k);
            foreach (var k in _removed.History.Where(x => x.Value < cutoff).Select(x => x.Key).ToList()) _removed.History.Remove(k);
        }
        if (changed > 0) Touched();
        else { _dirty = true; _saver.Change(1000, Timeout.Infinite); } // the deletions learnt are kept even when nothing showed
        return changed;
    }

    // ---- Finding things ----

    /// <summary>Bookmarks and visited pages that contain every word typed, best first.</summary>
    public IReadOnlyList<Suggestion> Search(string query, int limit = 6)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant()).ToArray();
        if (words.Length == 0) return [];
        lock (_lock)
        {
            bool Hits(string url, string title) => words.All(w => url.Contains(w, StringComparison.OrdinalIgnoreCase) || title.Contains(w, StringComparison.OrdinalIgnoreCase));
            double Rank(string url, string title, int visits, DateTime when, bool bookmark)
            {
                var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
                var score = (bookmark ? 50 : 0) + Math.Min(visits, 30) * 2 - (DateTime.Now - when).TotalDays * 0.3;
                if (host.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) || host.StartsWith("www." + words[0], StringComparison.OrdinalIgnoreCase)) score += 40; // typing the start of a site's name
                if (title.StartsWith(words[0], StringComparison.OrdinalIgnoreCase)) score += 15;
                return score;
            }
            var found = new Dictionary<string, (Suggestion S, double Score)>();
            foreach (var b in _bookmarks.Where(b => Hits(b.Url, b.Title)))
                found[Key(b.Url)] = (new Suggestion(b.Url, b.Title, true), Rank(b.Url, b.Title, 0, b.AddedAt, true));
            foreach (var h in _history.Where(h => Hits(h.Url, h.Title)))
            {
                var k = Key(h.Url);
                var score = Rank(h.Url, h.Title, h.Visits, h.LastVisit, found.ContainsKey(k));
                if (!found.TryGetValue(k, out var old) || score > old.Score)
                    found[k] = (new Suggestion(h.Url, h.Title, found.ContainsKey(k) || old.S?.IsBookmark == true), score);
            }
            return [.. found.Values.OrderByDescending(v => v.Score).Take(limit).Select(v => v.S)];
        }
    }

    // ---- Saving ----

    private void Touched()
    {
        _dirty = true;
        _saver.Change(1000, Timeout.Infinite); // save a second after the last change
        Changed?.Invoke();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private List<T> Read<T>(string name)
    {
        try
        {
            var path = Path.Combine(_folder!, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch { return []; } // a damaged file starts empty rather than stopping the browser
    }

    public void Flush()
    {
        if (_folder is null || !_dirty) return;
        string b, h, r;
        lock (_lock)
        {
            _dirty = false;
            b = JsonSerializer.Serialize(_bookmarks, Json);
            h = JsonSerializer.Serialize(_history, Json);
            r = JsonSerializer.Serialize(_removed, Json);
        }
        try
        {
            Directory.CreateDirectory(_folder);
            Write("bookmarks.json", b);
            Write("history.json", h);
            Write("removals.json", r);
        }
        catch { /* try again on the next change */ _dirty = true; }
    }

    private void Write(string name, string text)
    {
        var path = Path.Combine(_folder!, name);
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public void Dispose()
    {
        _saver.Dispose();
        Flush();
    }
}
