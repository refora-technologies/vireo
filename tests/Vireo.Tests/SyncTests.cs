using System.IO;
using Vireo.Services;
using Xunit;

namespace Vireo.Tests;

public class SyncTests
{
    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "vireo-sync-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void Bookmarks_and_history_from_another_computer_arrive()
    {
        using var home = new Library(null);
        using var work = new Library(null);
        work.ToggleBookmark("https://example.com/a", "A");
        work.Visit("https://news.example.org/", "News");
        var changed = home.MergeFromSync([work.ForSync()]);
        Assert.True(changed >= 2);
        Assert.True(home.IsBookmarked("https://example.com/a"));
        Assert.Contains(home.History, h => h.Url == "https://news.example.org/");
        Assert.Equal(0, home.MergeFromSync([work.ForSync()])); // the second time there is nothing new
    }

    [Fact]
    public void A_bookmark_deleted_on_one_computer_goes_from_the_others_and_is_not_brought_back()
    {
        using var home = new Library(null);
        using var work = new Library(null);
        home.ToggleBookmark("https://example.com/a", "A");
        work.MergeFromSync([home.ForSync()]);
        Assert.True(work.IsBookmarked("https://example.com/a"));
        Thread.Sleep(20);
        work.RemoveBookmark("https://example.com/a");           // deleted at work
        home.MergeFromSync([work.ForSync()]);                    // home hears of it
        Assert.False(home.IsBookmarked("https://example.com/a"));
        work.MergeFromSync([home.ForSync()]);                    // and does not hand it back
        Assert.False(work.IsBookmarked("https://example.com/a"));
    }

    [Fact]
    public void Adding_it_again_after_the_deletion_wins()
    {
        using var home = new Library(null);
        using var work = new Library(null);
        home.ToggleBookmark("https://example.com/a", "A");
        home.RemoveBookmark("https://example.com/a");
        Thread.Sleep(20);
        work.ToggleBookmark("https://example.com/a", "A again");
        home.MergeFromSync([work.ForSync()]);
        Assert.True(home.IsBookmarked("https://example.com/a"));
    }

    [Fact]
    public void Clearing_history_on_one_computer_clears_what_was_visited_before_it_everywhere()
    {
        using var home = new Library(null);
        using var work = new Library(null);
        home.Visit("https://old.example.com/", "Old", DateTime.Now.AddMinutes(-5));
        work.MergeFromSync([home.ForSync()]);
        work.ClearHistory();
        home.MergeFromSync([work.ForSync()]);
        Assert.DoesNotContain(home.History, h => h.Url == "https://old.example.com/");
        home.Visit("https://new.example.com/", "New");
        work.MergeFromSync([home.ForSync()]);
        Assert.Contains(work.History, h => h.Url == "https://new.example.com/");
    }

    [Fact]
    public void Only_web_addresses_are_taken_in()
    {
        using var home = new Library(null);
        var evil = new SyncLibrary([new SyncBookmark("javascript:alert(1)", "x", DateTime.UtcNow)], [new SyncHistory("file:///c:/secret", "x", DateTime.UtcNow, 1)], [], [], null);
        Assert.Equal(0, home.MergeFromSync([evil]));
        Assert.Empty(home.Bookmarks);
    }

    [Fact]
    public void The_folder_opens_only_with_its_passphrase_and_files_cannot_be_read_or_changed_without_it()
    {
        var dir = TempDir();
        try
        {
            var key = SyncVault.Open(dir, "correct horse battery")!;
            Assert.NotNull(key);
            Assert.Equal(key, SyncVault.Open(dir, "correct horse battery"));
            Assert.Null(SyncVault.Open(dir, "wrong passphrase"));

            using var lib = new Library(null);
            lib.ToggleBookmark("https://secret.example.com/", "Secret");
            var payload = new SyncPayload(1, "dev1", "Laptop", DateTime.UtcNow, lib.ForSync(), [new PinnedSite("Mail", "https://mail.example.com/")], DateTime.UtcNow, [new SyncTab("https://a.example/", "A")]);
            var sealedBytes = SyncService.Pack(key, payload);
            Assert.DoesNotContain("secret.example.com", System.Text.Encoding.UTF8.GetString(sealedBytes));
            var back = SyncService.Unpack(key, sealedBytes)!;
            Assert.Equal("Laptop", back.DeviceName);
            Assert.Equal("https://secret.example.com/", back.Library.Bookmarks.Single().Url);

            var other = SyncVault.DeriveKey("wrong passphrase", new byte[16]);
            Assert.Null(SyncService.Unpack(other, sealedBytes));
            sealedBytes[^5] ^= 0x40; // one bit changed in transit
            Assert.Null(SyncService.Unpack(key, sealedBytes));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Vireo_writes_into_a_folder_of_its_own()
    {
        Assert.Equal(Path.Combine(@"C:\Users\me\OneDrive", "Vireo Sync"), SyncVault.FolderIn(@"C:\Users\me\OneDrive"));
        Assert.Equal(@"C:\Users\me\OneDrive\Vireo Sync", SyncVault.FolderIn(@"C:\Users\me\OneDrive\Vireo Sync"));
    }
}

public class ProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vireo-profiles-" + Guid.NewGuid().ToString("N"));
    public ProfileTests() => Environment.SetEnvironmentVariable("VIREO_PROFILES_ROOT", _root);
    public void Dispose() { Environment.SetEnvironmentVariable("VIREO_PROFILES_ROOT", null); try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Profiles_are_listed_with_the_default_first_and_get_safe_folder_names()
    {
        Assert.Single(Profiles.All());
        var work = Profiles.Add("Work / Office ✨");
        var work2 = Profiles.Add("Work / Office ✨");
        Assert.True(Profiles.ValidId(work.Id));
        Assert.NotEqual(work.Id, work2.Id);
        Assert.Equal(["", work.Id, work2.Id], Profiles.All().Select(p => p.Id));
        Assert.Equal(Path.Combine(_root, "profiles", work.Id), Profiles.FolderOf(work.Id));
        Assert.Equal(Path.Combine(_root, "profile"), Profiles.FolderOf(""));
        Profiles.Update(work with { Name = "Job", Color = "#7AA2FF" });
        Assert.Equal("Job", Profiles.Find(work.Id).Name);
        Directory.CreateDirectory(Profiles.FolderOf(work2.Id));
        Assert.True(Profiles.Remove(work2.Id));
        Assert.False(Directory.Exists(Profiles.FolderOf(work2.Id)));
        Assert.False(Profiles.Remove(""));      // the default one stays
        Assert.False(Profiles.Remove("../x"));  // nothing outside the profiles folder
    }

    [Fact]
    public void The_command_line_picks_the_profile_and_ignores_bad_names()
    {
        Assert.Equal("work", App.ProfileFrom(["Vireo.exe", "--profile", "work", "https://a.example"]));
        Assert.Equal("", App.ProfileFrom(["Vireo.exe", "--profile", "..\\..\\Windows"]));
        Assert.Equal("", App.ProfileFrom(["Vireo.exe", "https://a.example"]));
        Assert.Equal(["--profile", "work", "https://a.example"], Profiles.Arguments("work", "https://a.example"));
        Assert.Empty(Profiles.Arguments(""));
    }
}
