using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vireo.Services;
using Vireo.Services.Import;
using Xunit;

namespace Vireo.Tests;

/// <summary>Makes a pretend Chrome / Firefox profile on disk, exactly as those browsers lay theirs out.</summary>
internal sealed class FakeProfiles : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vireo-fake-" + Guid.NewGuid().ToString("N")[..8]);
    public string LocalAppData => Path.Combine(Root, "Local");
    public string AppData => Path.Combine(Root, "Roaming");

    public FakeProfiles() { Directory.CreateDirectory(Root); SQLitePCL.Batteries_V2.Init(); }

    private static long WebKit(DateTime utc) => (utc - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / 10;

    private static void Run(string db, params string[] sql)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        foreach (var s in sql) { using var cmd = c.CreateCommand(); cmd.CommandText = s; cmd.ExecuteNonQuery(); }
    }

    /// <summary>Chrome-style: Bookmarks and History, laid out as Chrome lays them out.</summary>
    public (string UserData, string Profile) Chromium(string vendorPath)
    {
        var userData = Path.Combine(LocalAppData, vendorPath, "User Data");
        var profile = Path.Combine(userData, "Default");
        Directory.CreateDirectory(profile);
        var when = WebKit(new DateTime(2025, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(profile, "Bookmarks"), """
            {"roots":{"bookmark_bar":{"type":"folder","children":[
              {"type":"url","name":"Example","url":"https://example.com/","date_added":"@WHEN@"},
              {"type":"folder","name":"Deep","children":[{"type":"url","name":"Nested","url":"https://nested.test/a","date_added":"@WHEN@"}]},
              {"type":"url","name":"Script","url":"javascript:alert(1)","date_added":"@WHEN@"}]},
              "other":{"type":"folder","children":[{"type":"url","name":"Other","url":"http://other.test/","date_added":"@WHEN@"}]},"synced":{"type":"folder","children":[]}}}
            """.Replace("@WHEN@", when.ToString()));
        var history = Path.Combine(profile, "History");
        Run(history,
            "CREATE TABLE urls (id INTEGER PRIMARY KEY, url TEXT, title TEXT, visit_count INTEGER, typed_count INTEGER, last_visit_time INTEGER, hidden INTEGER DEFAULT 0)",
            $"INSERT INTO urls (url, title, visit_count, last_visit_time) VALUES ('https://news.test/story', 'A story', 7, {when})",
            $"INSERT INTO urls (url, title, visit_count, last_visit_time) VALUES ('https://hidden.test/', 'Hidden', 1, {when})",
            "UPDATE urls SET hidden = 1 WHERE url = 'https://hidden.test/'",
            $"INSERT INTO urls (url, title, visit_count, last_visit_time) VALUES ('chrome://settings/', 'Settings', 3, {when})");
        return (userData, profile);
    }

    public string Firefox()
    {
        var profile = Path.Combine(AppData, "Mozilla", "Firefox", "Profiles", "ab12cd34.default-release");
        Directory.CreateDirectory(profile);
        var us = (long)(new DateTime(2025, 4, 1, 8, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalMilliseconds * 1000;
        Run(Path.Combine(profile, "places.sqlite"),
            "CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT, title TEXT, visit_count INTEGER, last_visit_date INTEGER)",
            "CREATE TABLE moz_bookmarks (id INTEGER PRIMARY KEY, type INTEGER, fk INTEGER, parent INTEGER, title TEXT, dateAdded INTEGER)",
            $"INSERT INTO moz_places VALUES (1, 'https://ff.test/page', 'FF page', 4, {us})",
            $"INSERT INTO moz_places VALUES (2, 'place:type=6&sort=14', 'Recent', 0, NULL)",
            $"INSERT INTO moz_places VALUES (3, 'https://never-visited.test/', 'Bookmarked only', 0, NULL)",
            $"INSERT INTO moz_bookmarks VALUES (1, 1, 1, 2, 'My FF bookmark', {us})",
            $"INSERT INTO moz_bookmarks VALUES (2, 2, NULL, 0, 'A folder', {us})",
            $"INSERT INTO moz_bookmarks VALUES (3, 1, 3, 2, NULL, {us})",
            $"INSERT INTO moz_bookmarks VALUES (4, 1, 2, 2, 'A smart folder', {us})");
        return profile;
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* a file still closing */ } }
}

public class ImportTests
{

}

public class DefaultBrowserTests
{
    [Fact]
    public void Registering_lists_vireo_for_web_links_and_pages_and_can_be_undone()
    {
        var hive = @"Software\VireoTest-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Assert.False(DefaultBrowser.IsRegistered(hive));
            DefaultBrowser.Register(@"C:\Apps\Vireo\Vireo.exe", hive);
            Assert.True(DefaultBrowser.IsRegistered(hive));
            using var user = Microsoft.Win32.Registry.CurrentUser;
            Assert.Equal("\"C:\\Apps\\Vireo\\Vireo.exe\" \"%1\"", user.OpenSubKey($@"{hive}\Classes\VireoURL\shell\open\command")!.GetValue(null));
            Assert.Equal("VireoURL", user.OpenSubKey($@"{hive}\Clients\StartMenuInternet\Vireo\Capabilities\URLAssociations")!.GetValue("https"));
            Assert.Equal("VireoHTML", user.OpenSubKey($@"{hive}\Clients\StartMenuInternet\Vireo\Capabilities\FileAssociations")!.GetValue(".html"));
            Assert.Equal(@$"{hive}\Clients\StartMenuInternet\Vireo\Capabilities", user.OpenSubKey($@"{hive}\RegisteredApplications")!.GetValue("Vireo"));

            DefaultBrowser.Register(@"C:\Apps\Vireo\Vireo.exe", hive); // twice is fine
            DefaultBrowser.Unregister(hive);
            Assert.False(DefaultBrowser.IsRegistered(hive));
            Assert.Null(user.OpenSubKey($@"{hive}\Classes\VireoURL"));
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(hive, false); }
    }
}

public class SingleInstanceTests
{
    [Fact]
    public async Task A_second_start_hands_its_link_to_the_first()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vireo-single-" + Guid.NewGuid().ToString("N")[..8]);
        using var first = new SingleInstance(folder);
        Assert.True(first.IsFirst);
        var got = new TaskCompletionSource<string>();
        first.Received += link => got.TrySetResult(link);

        using var second = new SingleInstance(folder);
        Assert.False(second.IsFirst);
        Assert.True(second.Send("https://link.test/page"));
        Assert.Equal("https://link.test/page", await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Different_profiles_do_not_know_about_each_other()
    {
        using var a = new SingleInstance(Path.Combine(Path.GetTempPath(), "vireo-single-a-" + Guid.NewGuid().ToString("N")[..6]));
        using var b = new SingleInstance(Path.Combine(Path.GetTempPath(), "vireo-single-b-" + Guid.NewGuid().ToString("N")[..6]));
        Assert.True(a.IsFirst);
        Assert.True(b.IsFirst);
    }

    [Fact]
    public async Task Nothing_but_an_address_or_an_empty_request_is_accepted()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vireo-single-" + Guid.NewGuid().ToString("N")[..8]);
        using var first = new SingleInstance(folder);
        var seen = new List<string>();
        first.Received += link => { lock (seen) seen.Add(link); };
        using var second = new SingleInstance(folder);
        second.Send(new string('x', 20000));
        second.Send("");
        await Task.Delay(800);
        lock (seen) Assert.Equal([""], seen); // the oversized request was dropped; the empty one just brings the window forward
    }
}
