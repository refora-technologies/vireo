using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Vireo.Services.Import;

public enum BrowserKind { Chrome, Edge, Brave, Vivaldi, Opera, Firefox }

/// <summary>One profile of another browser that can be imported from.</summary>
public sealed record ImportSource(BrowserKind Kind, string Name, string ProfileFolder, string UserDataFolder)
{
    public bool IsFirefox => Kind == BrowserKind.Firefox;
    public string Title => $"{Kind} · {Name}";
}

public sealed class ImportReport
{
    public int Bookmarks, History, Passwords, PasswordsSkipped;
    public List<string> Notes { get; } = [];
    public string Summary => string.Join(", ", new[]
    {
        Bookmarks > 0 ? $"{Bookmarks:N0} bookmarks" : null, History > 0 ? $"{History:N0} history entries" : null, Passwords > 0 ? $"{Passwords:N0} passwords" : null,
    }.Where(x => x is not null)) is { Length: > 0 } s ? "Imported " + s + "." : "Nothing new to import.";
}

public static class OtherBrowsers
{
    // ---- Which browsers are here ----

    public static List<ImportSource> Detect(string? localAppData = null, string? appData = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var found = new List<ImportSource>();

        void Chromium(BrowserKind kind, string userData)
        {
            if (!Directory.Exists(userData)) return;
            foreach (var dir in Directory.GetDirectories(userData).Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ", StringComparison.Ordinal)))
            {
                if (!File.Exists(Path.Combine(dir, "Bookmarks")) && !File.Exists(Path.Combine(dir, "History"))) continue;
                var id = Path.GetFileName(dir);
                found.Add(new ImportSource(kind, id == "Default" ? "Main profile" : id, dir, userData));
            }
        }

        Chromium(BrowserKind.Chrome, Path.Combine(localAppData, "Google", "Chrome", "User Data"));
        Chromium(BrowserKind.Edge, Path.Combine(localAppData, "Microsoft", "Edge", "User Data"));
        Chromium(BrowserKind.Brave, Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"));
        Chromium(BrowserKind.Vivaldi, Path.Combine(localAppData, "Vivaldi", "User Data"));
        var opera = Path.Combine(appData, "Opera Software", "Opera Stable");
        if (File.Exists(Path.Combine(opera, "Bookmarks")) || File.Exists(Path.Combine(opera, "History"))) found.Add(new ImportSource(BrowserKind.Opera, "Default", opera, opera));

        var firefox = Path.Combine(appData, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefox))
            foreach (var dir in Directory.GetDirectories(firefox).Where(d => File.Exists(Path.Combine(d, "places.sqlite"))))
                found.Add(new ImportSource(BrowserKind.Firefox, Path.GetFileName(dir).Contains('.') ? Path.GetFileName(dir)[(Path.GetFileName(dir).IndexOf('.') + 1)..] : Path.GetFileName(dir), dir, firefox));
        return found;
    }

    // ---- Chrome, Edge, Brave, Vivaldi, Opera ----

    private static readonly DateTime WebKitEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static DateTime FromWebKit(long microseconds) =>
        microseconds <= 0 || microseconds > 20_000_000_000_000_000 ? DateTime.Now : WebKitEpoch.AddTicks(microseconds * 10).ToLocalTime();

    public static List<Bookmark> ChromiumBookmarks(string profileFolder)
    {
        var list = new List<Bookmark>();
        var file = Path.Combine(profileFolder, "Bookmarks");
        if (!File.Exists(file)) return list;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(stream);
        void Walk(JsonElement node)
        {
            if (node.TryGetProperty("type", out var type) && type.GetString() == "url")
            {
                var url = node.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (url is not null && IsWeb(url))
                {
                    var title = node.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var when = node.TryGetProperty("date_added", out var d) && long.TryParse(d.GetString(), out var us) ? FromWebKit(us) : DateTime.Now;
                    list.Add(new Bookmark(url, string.IsNullOrWhiteSpace(title) ? url : title!, when));
                }
            }
            if (node.TryGetProperty("children", out var kids)) foreach (var k in kids.EnumerateArray()) Walk(k);
        }
        if (doc.RootElement.TryGetProperty("roots", out var roots))
            foreach (var root in roots.EnumerateObject()) if (root.Value.ValueKind == JsonValueKind.Object) Walk(root.Value);
        return list;
    }

    public static List<HistoryEntry> ChromiumHistory(string profileFolder, int max = 20000)
    {
        var file = Path.Combine(profileFolder, "History");
        var list = new List<HistoryEntry>();
        if (!File.Exists(file)) return list;
        using var db = new SqliteCopy(file);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT url, title, visit_count, last_visit_time FROM urls WHERE hidden = 0 OR hidden IS NULL ORDER BY last_visit_time DESC LIMIT $max";
        cmd.Parameters.AddWithValue("$max", max);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var url = r.GetString(0);
            if (!IsWeb(url)) continue;
            var title = r.IsDBNull(1) ? "" : r.GetString(1);
            list.Add(new HistoryEntry(url, string.IsNullOrWhiteSpace(title) ? url : title, FromWebKit(r.GetInt64(3)), Math.Max(1, r.GetInt32(2))));
        }
        return list;
    }

    // ---- Firefox ----

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime FromUnixMicro(long us) => us <= 0 ? DateTime.Now : UnixEpoch.AddTicks(us * 10).ToLocalTime();

    public static List<Bookmark> FirefoxBookmarks(string profileFolder)
    {
        var list = new List<Bookmark>();
        var file = Path.Combine(profileFolder, "places.sqlite");
        if (!File.Exists(file)) return list;
        using var db = new SqliteCopy(file);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT p.url, COALESCE(b.title, p.title), b.dateAdded FROM moz_bookmarks b JOIN moz_places p ON p.id = b.fk WHERE b.type = 1";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var url = r.GetString(0);
            if (!IsWeb(url)) continue;
            var title = r.IsDBNull(1) ? "" : r.GetString(1);
            list.Add(new Bookmark(url, string.IsNullOrWhiteSpace(title) ? url : title, r.IsDBNull(2) ? DateTime.Now : FromUnixMicro(r.GetInt64(2))));
        }
        return list;
    }

    public static List<HistoryEntry> FirefoxHistory(string profileFolder, int max = 20000)
    {
        var list = new List<HistoryEntry>();
        var file = Path.Combine(profileFolder, "places.sqlite");
        if (!File.Exists(file)) return list;
        using var db = new SqliteCopy(file);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT url, title, visit_count, last_visit_date FROM moz_places WHERE visit_count > 0 AND last_visit_date IS NOT NULL ORDER BY last_visit_date DESC LIMIT $max";
        cmd.Parameters.AddWithValue("$max", max);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var url = r.GetString(0);
            if (!IsWeb(url)) continue;
            var title = r.IsDBNull(1) ? "" : r.GetString(1);
            list.Add(new HistoryEntry(url, string.IsNullOrWhiteSpace(title) ? url : title, FromUnixMicro(r.GetInt64(3)), Math.Max(1, r.GetInt32(2))));
        }
        return list;
    }

    // ---- Files any browser can export ----

    private static readonly Regex Anchor = new("""<A\s+[^>]*?HREF\s*=\s*"([^"]+)"[^>]*>(.*?)</A>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AddDate = new("""ADD_DATE\s*=\s*"(\d+)"?""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The "bookmarks.html" file every browser can export.</summary>
    public static List<Bookmark> BookmarksFromHtml(string html)
    {
        var list = new List<Bookmark>();
        foreach (Match m in Anchor.Matches(html))
        {
            var url = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            if (!IsWeb(url)) continue;
            var title = System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[2].Value, "<[^>]+>", "")).Trim();
            var when = AddDate.Match(m.Value) is { Success: true } d && long.TryParse(d.Groups[1].Value, out var s) && s > 0 ? UnixEpoch.AddSeconds(s).ToLocalTime() : DateTime.Now;
            list.Add(new Bookmark(url, title.Length == 0 ? url : title, when));
        }
        return list;
    }

    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    private static bool IsWeb(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // ---- Doing it ----

    /// <summary>Brings the chosen kinds of data from <paramref name="source"/> into Vireo.</summary>
    public static ImportReport Import(ImportSource source, bool bookmarks, bool history, bool passwords, Library library)
    {
        var report = new ImportReport();
        void Attempt(string what, Action work)
        {
            try { work(); }
            catch (ImportException e) { report.Notes.Add(e.Message); }
            catch (Exception e) when (e is SqliteException or IOException or JsonException or UnauthorizedAccessException)
            {
                report.Notes.Add($"The {what} could not be read: {e.Message}");
            }
        }

        if (bookmarks) Attempt("bookmarks", () =>
        {
            var list = source.IsFirefox ? FirefoxBookmarks(source.ProfileFolder) : ChromiumBookmarks(source.ProfileFolder);
            report.Bookmarks = library.Import(list, []).Bookmarks;
        });
        if (history) Attempt("history", () =>
        {
            var list = source.IsFirefox ? FirefoxHistory(source.ProfileFolder) : ChromiumHistory(source.ProfileFolder);
            report.History = library.Import([], list).History;
        });
        if (passwords)
            report.Notes.Add($"Saved passwords stay in {source.Kind}. Vireo's engine offers to save each one the first time you sign in to a site here.");
        return report;
    }
}
