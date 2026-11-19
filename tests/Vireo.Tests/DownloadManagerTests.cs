using System.IO;
using System.Net;
using Vireo.Downloads;
using Xunit;

namespace Vireo.Tests;

/// <summary>The download-manager features: same link continues, sorting by type, file hosts' refusals in words.</summary>
public class DownloadManagerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(40);

    // ---- Same link, same download ----

    [Fact]
    public async Task Adding_a_link_again_carries_on_the_unfinished_download_instead_of_a_second_copy()
    {
        using var box = new Sandbox();
        using var server = new TestServer(3_000_000) { ChunkDelayMs = 20 };
        using var engine = box.Engine(connections: 2);
        var first = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        for (var i = 0; i < 200 && first.Downloaded < 200_000; i++) await Task.Delay(25);
        engine.Pause(first);
        for (var i = 0; i < 200 && first.State != DownloadState.Paused; i++) await Task.Delay(25);
        var before = first.Downloaded;
        server.ChunkDelayMs = 0;

        var again = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin", Cookie: "fresh=1"), out var continued);
        await engine.WhenIdleAsync(Limit);

        Assert.True(continued);
        Assert.Same(first, again);
        Assert.Single(engine.Items);
        Assert.Equal("fresh=1", first.Cookie); // a new visit's sign-in goes along
        Assert.Equal(DownloadState.Completed, first.State);
        Assert.Equal(server.Data, File.ReadAllBytes(first.FinalPath));
        Assert.True(before > 0);
        Assert.Single(Directory.GetFiles(box.Files)); // no "file (1).bin"
    }

    [Fact]
    public async Task A_failed_download_added_again_is_retried_from_where_it_stopped()
    {
        using var box = new Sandbox();
        using var server = new TestServer(1_000_000) { Unavailable503First = 100 };
        using var engine = new DownloadEngine(box.State, new DownloadOptions { Folder = box.Files, Connections = 2, MaxReconnects = 1, MaxRetryDelay = TimeSpan.FromMilliseconds(100), StallTimeout = TimeSpan.FromSeconds(3) });
        var first = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        await engine.WhenIdleAsync(Limit);
        Assert.Equal(DownloadState.Failed, first.State);

        server.Unavailable503First = 0;
        var again = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"), out var continued);
        await engine.WhenIdleAsync(Limit);

        Assert.True(continued);
        Assert.Same(first, again);
        Assert.Equal(DownloadState.Completed, first.State);
    }

    [Fact]
    public async Task A_finished_download_added_again_is_a_new_download()
    {
        using var box = new Sandbox();
        using var server = new TestServer(300_000);
        using var engine = box.Engine();
        var first = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        await engine.WhenIdleAsync(Limit);
        var second = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"), out var continued);
        await engine.WhenIdleAsync(Limit);

        Assert.False(continued);
        Assert.NotSame(first, second);
        Assert.Equal(2, Directory.GetFiles(box.Files).Length);
    }

    // ---- Sorting by type ----

    [Theory]
    [InlineData("film.mkv", FileTypes.Video)]
    [InlineData("song.FLAC", FileTypes.Music)]
    [InlineData("report.pdf", FileTypes.Documents)]
    [InlineData("setup.exe", FileTypes.Programs)]
    [InlineData("backup.7z", FileTypes.Compressed)]
    [InlineData("backup.7z.001", FileTypes.Compressed)]
    [InlineData("photo.jpeg", FileTypes.Pictures)]
    [InlineData("notes", null)]
    [InlineData("data.bin", null)]
    public void Files_are_sorted_by_their_kind(string name, string? kind) => Assert.Equal(kind, FileTypes.KindOf(name));

    [Fact]
    public async Task With_sorting_on_a_download_lands_in_the_folder_for_its_kind()
    {
        using var box = new Sandbox();
        using var server = new TestServer(200_000);
        using var engine = box.Engine();
        engine.Options.SortByType = true;
        var named = engine.Add(new DownloadRequest(server.BaseUrl + "/named")); // the server names it "report final.pdf"
        var given = engine.Add(new DownloadRequest(server.BaseUrl + "/clip", "clip.mp4"));
        var other = engine.Add(new DownloadRequest(server.BaseUrl + "/data.bin"));
        var chosen = engine.Add(new DownloadRequest(server.BaseUrl + "/song.mp3", Folder: Path.Combine(box.Files, "mine")));
        await engine.WhenIdleAsync(Limit);

        Assert.Equal(Path.Combine(box.Files, "Documents", "report final.pdf"), named.FinalPath);
        Assert.Equal(Path.Combine(box.Files, "Video", "clip.mp4"), given.FinalPath);
        Assert.Equal(Path.Combine(box.Files, "data.bin"), other.FinalPath);
        Assert.Equal(Path.Combine(box.Files, "mine", "song.mp3"), chosen.FinalPath); // a folder someone picked is left alone
        Assert.All(new[] { named, given, other, chosen }, i => Assert.True(File.Exists(i.FinalPath)));
    }

    [Fact]
    public async Task With_sorting_off_everything_stays_in_the_download_folder()
    {
        using var box = new Sandbox();
        using var server = new TestServer(200_000);
        using var engine = box.Engine();
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/clip", "clip.mp4"));
        await engine.WhenIdleAsync(Limit);
        Assert.Equal(Path.Combine(box.Files, "clip.mp4"), item.FinalPath);
    }

    // ---- File hosts ----

    [Fact]
    public async Task A_hosts_daily_limit_is_said_in_words()
    {
        using var box = new Sandbox();
        using var server = new TestServer(100_000);
        using var engine = box.Engine();
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/host-refuses/transfer_limit_exceeded"));
        await engine.WhenIdleAsync(Limit);

        Assert.Equal(DownloadState.Failed, item.State);
        Assert.Contains("daily download limit", item.Error);
        Assert.Single(server.Requests); // a used-up limit is not hammered with retries
    }

    [Fact]
    public async Task A_host_asking_for_its_page_says_so()
    {
        using var box = new Sandbox();
        using var server = new TestServer(100_000);
        using var engine = box.Engine();
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/host-refuses/file_rate_limited_captcha_required"));
        await engine.WhenIdleAsync(Limit);
        Assert.Contains("open this file on its page", item.Error);
    }

    [Fact]
    public async Task A_bandwidth_limit_is_said_in_words()
    {
        using var box = new Sandbox();
        using var server = new TestServer(100_000);
        using var engine = box.Engine();
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/host-refuses/x/bandwidth"));
        await engine.WhenIdleAsync(Limit);
        Assert.Contains("bandwidth limit", item.Error);
    }

    [Fact]
    public async Task A_host_that_allows_few_connections_still_gets_the_whole_file()
    {
        using var box = new Sandbox();
        using var server = new TestServer(4_000_000) { HostAllowsAtOnce = 2 };
        using var engine = box.Engine(connections: 8);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath));
        Assert.True(server.TooManyAnswers > 0);
    }

    [Theory]
    [InlineData("https://pixeldrain.com/u/abc123XY", "https://pixeldrain.com/api/file/abc123XY?download")]
    [InlineData("https://pixeldrain.com/l/abc123XY", null)] // a list: its page, not one file
    [InlineData("https://example.com/u/abc123XY", null)]
    public void A_hosts_share_page_has_a_direct_address(string page, string? direct) =>
        Assert.Equal(direct, FileHosts.DirectAddress(new Uri(page)));

    [Theory]
    [InlineData("{\"success\":false,\"value\":\"transfer_limit_exceeded\"}", "transfer_limit_exceeded")]
    [InlineData("<html>no</html>", null)]
    [InlineData("{\"value\":5}", null)]
    [InlineData("{broken", null)]
    [InlineData("", null)]
    public void The_reason_is_read_from_the_hosts_answer(string body, string? reason) => Assert.Equal(reason, FileHosts.ReasonCode(body));

    [Fact]
    public void Hosts_are_named_plainly()
    {
        Assert.Equal("pixeldrain", FileHosts.SiteName("pixeldrain.com"));
        Assert.Equal("pixeldrain", FileHosts.SiteName("sa.pixeldrain.com"));
        Assert.Equal(HostRefusal.LimitReached, FileHosts.Classify("x.com", (HttpStatusCode)509, null));
        Assert.Equal(HostRefusal.None, FileHosts.Classify("x.com", HttpStatusCode.Forbidden, null));
    }

    // ---- Links worth offering from the clipboard ----

    [Theory]
    [InlineData("https://example.com/files/setup.exe", true)]
    [InlineData("https://example.com/a/film%20one.mkv?token=1", true)]
    [InlineData("https://example.com/archive.zip", true)]
    [InlineData("https://example.com/photo.jpg", false)] // pictures are copied all the time
    [InlineData("https://example.com/page.html", false)]
    [InlineData("https://example.com/", false)]
    public void Only_links_to_files_are_offered(string url, bool offered) => Assert.Equal(offered, FileTypes.IsFileAddress(new Uri(url)));

    [Theory]
    [InlineData("https://example.com/files/setup.exe", true)]
    [InlineData("  https://example.com/archive.zip  ", true)]
    [InlineData("https://pixeldrain.com/u/abc123XY", true)] // a file host's page
    [InlineData("https://example.com/archive.zip\nhttps://example.com/b.zip", false)] // several lines: not one link
    [InlineData("see https://example.com/archive.zip", false)] // a sentence
    [InlineData("ftp://example.com/archive.zip", false)]
    [InlineData("https://user:secret@example.com/archive.zip", false)] // a link with a password in it is never offered
    [InlineData("https://example.com/page", false)]
    [InlineData("hunter2", false)]
    public void Only_a_single_link_to_a_file_is_taken_from_the_clipboard(string text, bool taken) =>
        Assert.Equal(taken, Vireo.Services.ClipboardWatch.FileLink(text) is not null);

    // ---- Checksums ----

    [Theory]
    [InlineData("3c65ea93424a9c362fec0e3a69ea36031e8a358441479dd665cc6110eabe7b08", "SHA256")]
    [InlineData("SHA-256: 3C65EA93424A9C362FEC0E3A69EA36031E8A358441479DD665CC6110EABE7B08", "SHA256")]
    [InlineData("3c65ea93424a9c362fec0e3a69ea36031e8a358441479dd665cc6110eabe7b08  ubuntu.iso", "SHA256")]
    [InlineData("sha1:da39a3ee5e6b4b0d3255bfef95601890afd80709", "SHA1")]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e", "MD5")]
    [InlineData("not a checksum", null)]
    [InlineData("3c65ea93", null)]
    public void A_pasted_checksum_is_recognised(string text, string? kind) =>
        Assert.Equal(kind, Vireo.Services.Checksum.KindOf(text)?.Name);

    [Fact]
    public async Task A_files_checksum_is_computed_and_compared()
    {
        using var box = new Sandbox();
        var path = Path.Combine(box.Files, "x.bin");
        File.WriteAllBytes(path, []);
        var sha = await Vireo.Services.Checksum.OfFileAsync(path, System.Security.Cryptography.HashAlgorithmName.SHA256);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", sha); // the SHA-256 of nothing
        Assert.True(Vireo.Services.Checksum.Matches(sha, "SHA256: E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"));
        Assert.False(Vireo.Services.Checksum.Matches(sha, new string('0', 64)));
    }

    // ---- When done ----

    [Fact]
    public void When_done_waits_for_everything_and_needs_something_to_have_finished()
    {
        var since = DateTime.Now;
        DownloadItem Item(DownloadState state, DateTime? completed = null) => new() { State = state, CompletedAt = completed };
        var running = Item(DownloadState.Downloading);
        var runningThen = new HashSet<string> { running.Id };

        Assert.False(Vireo.Services.WhenDone.AllFinished([running], since, runningThen)); // still going
        running.State = DownloadState.Completed;
        Assert.True(Vireo.Services.WhenDone.AllFinished([running], since, runningThen));
        running.State = DownloadState.Failed;
        Assert.True(Vireo.Services.WhenDone.AllFinished([running], since, runningThen)); // a failure is finished too

        // Chosen while nothing was running: an old finished download is not a reason to shut down.
        Assert.False(Vireo.Services.WhenDone.AllFinished([Item(DownloadState.Completed, since.AddHours(-1))], since, new HashSet<string>()));
        Assert.False(Vireo.Services.WhenDone.AllFinished([Item(DownloadState.Paused)], since, new HashSet<string>()));
        Assert.True(Vireo.Services.WhenDone.AllFinished([Item(DownloadState.Completed, since.AddSeconds(5))], since, new HashSet<string>()));
        Assert.False(Vireo.Services.WhenDone.AllFinished([Item(DownloadState.Completed, since.AddSeconds(5)), Item(DownloadState.Queued)], since, new HashSet<string>()));
    }

    // ---- Start at a time ----

    [Fact]
    public void The_next_hour_is_today_if_still_ahead_else_tomorrow()
    {
        var evening = new DateTime(2026, 11, 19, 22, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 20, 1, 0, 0), Vireo.Services.Schedule.Next(1, evening));
        Assert.Equal(new DateTime(2026, 11, 19, 23, 0, 0), Vireo.Services.Schedule.Next(23, evening));
    }

    [Fact]
    public async Task Paused_downloads_start_at_the_chosen_time_and_only_once()
    {
        using var box = new Sandbox();
        using var server = new TestServer(2_000_000) { ChunkDelayMs = 30 };
        using var engine = box.Engine(connections: 2);
        var settings = new Vireo.Services.Settings();
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        for (var i = 0; i < 200 && item.Downloaded == 0; i++) await Task.Delay(25);
        engine.Pause(item);
        for (var i = 0; i < 200 && item.State != DownloadState.Paused; i++) await Task.Delay(25);
        server.ChunkDelayMs = 0;

        var now = new DateTime(2026, 11, 19, 22, 0, 0);
        Vireo.Services.Schedule.Now = () => now;
        try
        {
            Vireo.Services.Schedule.Use(engine, settings);
            Vireo.Services.Schedule.Set(now.AddHours(3));
            Vireo.Services.Schedule.CheckNow();
            Assert.Equal(DownloadState.Paused, item.State); // not yet

            now = now.AddHours(3).AddSeconds(5);
            Vireo.Services.Schedule.CheckNow();
            Assert.True(item.IsActive || item.State == DownloadState.Completed);
            Assert.Null(settings.ScheduledStart); // used once
            await engine.WhenIdleAsync(Limit);
            Assert.Equal(DownloadState.Completed, item.State);
        }
        finally { Vireo.Services.Schedule.Now = () => DateTime.Now; }
    }

    [Fact]
    public void A_start_time_long_past_is_dropped_without_starting_anything()
    {
        using var box = new Sandbox();
        using var engine = box.Engine();
        var settings = new Vireo.Services.Settings { ScheduledStart = new DateTime(2026, 11, 1, 1, 0, 0) };
        Vireo.Services.Schedule.Now = () => new DateTime(2026, 11, 19, 9, 0, 0);
        try
        {
            Vireo.Services.Schedule.Use(engine, settings);
            Assert.Null(settings.ScheduledStart);
        }
        finally { Vireo.Services.Schedule.Now = () => DateTime.Now; }
    }
}
