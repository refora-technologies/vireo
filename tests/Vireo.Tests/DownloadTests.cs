using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using Vireo.Downloads;
using Xunit;

namespace Vireo.Tests;

/// <summary>Each test gets its own folders, on this drive (the system drive is nearly full).</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "tmp", Guid.NewGuid().ToString("N"));
    public string State => Path.Combine(Root, "state");
    public string Files => Path.Combine(Root, "files");

    public Sandbox() => Directory.CreateDirectory(Files);

    public DownloadEngine Engine(int connections = 6, int maxActive = 3, int limitKBps = 0) =>
        new(State, new DownloadOptions { Connections = connections, MaxActive = maxActive, SpeedLimitKBps = limitKBps, Folder = Files });

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { /* a file may still be closing */ }
    }
}

public class DownloadTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(40);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task A_file_split_over_several_connections_arrives_byte_for_byte()
    {
        using var box = new Sandbox();
        using var server = new TestServer();
        server.ChunkDelayMs = 4; // slow enough for connections to overlap
        using var engine = box.Engine(connections: 6);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file", "data.bin"));
        await engine.WhenIdleAsync(Limit);

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(Hash(server.Data), HashFile(Path.Combine(box.Files, "data.bin")));
        Assert.True(server.MaxConcurrent >= 4, $"only {server.MaxConcurrent} connections at once");
        Assert.False(File.Exists(Path.Combine(box.Files, "data.bin.vireopart")), "the working file should be gone");
    }

    [Fact]
    public async Task A_server_that_cannot_send_parts_gets_one_connection()
    {
        using var box = new Sandbox();
        using var server = new TestServer(2 * 1024 * 1024) { AcceptRanges = false };
        using var engine = box.Engine(connections: 8);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file", "plain.bin"));
        await engine.WhenIdleAsync(Limit);

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.False(item.CanResume);
        Assert.Equal(Hash(server.Data), HashFile(Path.Combine(box.Files, "plain.bin")));
        Assert.Equal(1, server.MaxConcurrent);
    }

    [Fact]
    public async Task Fast_connections_take_over_the_tail_of_a_slow_one()
    {
        using var box = new Sandbox();
        using var server = new TestServer(8 * 1024 * 1024) { ChunkDelayMs = 25, SlowWhenRangeStarts = start => start == 0 };
        using var engine = box.Engine(connections: 4);
        var sw = Stopwatch.StartNew();
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file", "split.bin"));
        await engine.WhenIdleAsync(Limit);

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(Hash(server.Data), HashFile(Path.Combine(box.Files, "split.bin")));
        // 4 stretches at the start; more means the idle connections split the slow one.
        Assert.True(item.Segments.Count > 4, $"{item.Segments.Count} stretches");
        // Alone, the slow stretch (2 MB at 16 KB per 25 ms) would take over 3 s.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public async Task Pausing_keeps_the_progress_and_resuming_finishes_the_file()
    {
        using var box = new Sandbox();
        using var server = new TestServer(6 * 1024 * 1024) { ChunkDelayMs = 12 };
        using var engine = box.Engine(connections: 4);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file", "pausable.bin"));
        while (item.Downloaded < 1_000_000) await Task.Delay(20);

        engine.Pause(item);
        while (item.State != DownloadState.Paused) await Task.Delay(20);
        var kept = item.Downloaded;
        Assert.InRange(kept, 1_000_000, server.Data.Length - 1);
        Assert.True(File.Exists(Path.Combine(box.Files, "pausable.bin.vireopart")));

        server.ChunkDelayMs = 0;
        engine.Resume(item);
        await engine.WhenIdleAsync(Limit);
        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(Hash(server.Data), HashFile(Path.Combine(box.Files, "pausable.bin")));
    }

    [Fact]
    public async Task Dropped_connections_carry_on_from_where_they_stopped()
    {
        using var box = new Sandbox();
        using var server = new TestServer(4 * 1024 * 1024) { DropFirst = 5, DropAfterBytes = 120_000 };
        using var engine = box.Engine(connections: 4);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file", "flaky.bin"));
        await engine.WhenIdleAsync(Limit);

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(Hash(server.Data), HashFile(Path.Combine(box.Files, "flaky.bin")));
    }

    [Fact]
    public async Task The_speed_limit_is_kept_across_all_connections()
    {
        using var box = new Sandbox();
        using var server = new TestServer(1024 * 1024);
        using var engine = box.Engine(connections: 4, limitKBps: 512);
        var sw = Stopwatch.StartNew();
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file", "limited.bin"));
        await engine.WhenIdleAsync(Limit);

        // 1 MB at 512 KB/s is about 2 seconds (the bucket starts empty). Unlimited it would be instant.
        Assert.Equal(DownloadState.Completed, item.State);
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.4, 6);
    }

    [Fact]
    public async Task Only_the_allowed_number_of_downloads_run_at_once()
    {
        using var box = new Sandbox();
        using var server = new TestServer(2 * 1024 * 1024) { ChunkDelayMs = 6 };
        using var engine = box.Engine(connections: 2, maxActive: 2);
        var most = 0;
        engine.ItemChanged += _ => most = Math.Max(most, engine.Items.Count(i => i.State == DownloadState.Downloading));
        var items = Enumerable.Range(0, 5).Select(i => engine.Add(new DownloadRequest(server.BaseUrl + "/file", $"q{i}.bin"))).ToList();
        await engine.WhenIdleAsync(Limit);

        Assert.All(items, i => Assert.Equal(DownloadState.Completed, i.State));
        Assert.InRange(most, 1, 2);
    }

    [Fact]
    public async Task The_file_is_named_as_the_server_asks_and_never_overwrites()
    {
        using var box = new Sandbox();
        using var server = new TestServer(300_000);
        using var engine = box.Engine();
        var first = engine.Add(new DownloadRequest(server.BaseUrl + "/named"));
        await engine.WhenIdleAsync(Limit);
        var second = engine.Add(new DownloadRequest(server.BaseUrl + "/named"));
        await engine.WhenIdleAsync(Limit);

        Assert.Equal("report final.pdf", first.FileName);
        Assert.Equal("report final (1).pdf", second.FileName);
        Assert.True(File.Exists(Path.Combine(box.Files, "report final.pdf")));
        Assert.True(File.Exists(Path.Combine(box.Files, "report final (1).pdf")));
    }

    [Theory]
    [InlineData("/missing", "no longer has this file")]
    [InlineData("/denied", "refused the download")]
    public async Task A_refusal_is_explained_in_plain_words(string path, string words)
    {
        using var box = new Sandbox();
        using var server = new TestServer(1000);
        using var engine = box.Engine();
        var item = engine.Add(new DownloadRequest(server.BaseUrl + path, "x.bin"));
        await engine.WhenIdleAsync(Limit);

        Assert.Equal(DownloadState.Failed, item.State);
        Assert.Contains(words, item.Error);
    }

    [Fact]
    public async Task A_download_stopped_by_closing_the_browser_resumes_after_it_opens_again()
    {
        using var box = new Sandbox();
        using var server = new TestServer(6 * 1024 * 1024) { ChunkDelayMs = 12 };
        string id;
        long kept;
        using (var first = box.Engine(connections: 4))
        {
            var item = first.Add(new DownloadRequest(server.BaseUrl + "/file", "survivor.bin"));
            id = item.Id;
            while (item.Downloaded < 1_200_000) await Task.Delay(20);
        } // disposing the engine is what closing the browser does
        server.ChunkDelayMs = 0;
        int before;
        lock (server.Requests) before = server.Requests.Count;

        using var second = box.Engine(connections: 4);
        var back = Assert.Single(second.Items);
        kept = back.Downloaded;
        Assert.Equal(id, back.Id);
        Assert.Equal(DownloadState.Paused, back.State);
        Assert.True(kept >= 1_000_000, $"kept {kept}");

        second.Resume(back);
        await second.WhenIdleAsync(Limit);
        Assert.Equal(DownloadState.Completed, back.State);
        Assert.Equal(Hash(server.Data), HashFile(Path.Combine(box.Files, "survivor.bin")));
        // After the restart it asked only for what was missing: apart from the 1-byte check, nothing began at byte zero.
        List<string> asked;
        lock (server.Requests) asked = server.Requests.Skip(before).ToList();
        Assert.NotEmpty(asked);
        Assert.DoesNotContain(asked, r => r.Contains("bytes=0-") && !r.Contains("bytes=0-0"));
    }

    [Fact]
    public async Task A_cancelled_download_leaves_nothing_behind()
    {
        using var box = new Sandbox();
        using var server = new TestServer(6 * 1024 * 1024) { ChunkDelayMs = 20 };
        using var engine = box.Engine(connections: 4);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file", "gone.bin"));
        while (item.Downloaded < 300_000) await Task.Delay(20);
        engine.Cancel(item);
        while (item.State != DownloadState.Cancelled) await Task.Delay(20);

        Assert.Empty(Directory.GetFiles(box.Files));
    }
}

public class FileNamingTests
{
    [Theory]
    [InlineData("a:b?.txt", "a_b_.txt")]
    [InlineData("  spaced.mp4.  ", "spaced.mp4")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("", "download")]
    public void Names_are_made_safe(string raw, string expected) => Assert.Equal(expected, FileNaming.Sanitize(raw));

    [Fact]
    public void A_missing_extension_comes_from_the_content_type() =>
        Assert.Equal("clip.mp4", FileNaming.Sanitize("clip", "video/mp4; charset=x"));

    [Fact]
    public void Long_names_are_shortened_but_keep_their_type() =>
        Assert.EndsWith(".mkv", FileNaming.Sanitize(new string('a', 300) + ".mkv"));

    [Theory]
    [InlineData("https://x.test/files/My%20Movie.mp4?token=1", "My Movie.mp4")]
    [InlineData("https://x.test/", "download")]
    public void Names_come_from_the_address(string url, string expected) => Assert.Equal(expected, FileNaming.FromUrl(url));
}
