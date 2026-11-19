using System.IO;
using Vireo.Downloads;
using Xunit;

namespace Vireo.Tests;

/// <summary>Automatic connections: a download adds connections only while that makes it faster, and never against the server.</summary>
public class AutoConnectionTests
{
    private static DownloadEngine Engine(Sandbox box, int cap, bool auto, int limitKBps = 0) => new(box.State, new DownloadOptions
    {
        Connections = cap, AutoConnections = auto, AutoStartConnections = 2, AutoGrowEvery = TimeSpan.FromMilliseconds(400),
        Folder = box.Files, SpeedLimitKBps = limitKBps, StallTimeout = TimeSpan.FromSeconds(5), MaxRetryDelay = TimeSpan.FromSeconds(1),
    });

    [Fact]
    public async Task A_server_that_limits_each_connection_gets_more_connections_and_the_file_arrives_whole()
    {
        // Every connection is slow on its own (as on servers that cap each connection): more connections, more speed.
        using var box = new Sandbox();
        using var server = new TestServer(6_000_000) { ChunkDelayMs = 15 };
        using var engine = Engine(box, cap: 10, auto: true);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(90));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath));
        Assert.True(server.MaxConcurrent > 3, $"grew to {server.MaxConcurrent} at once"); // started with 2
    }

    [Fact]
    public async Task A_server_that_allows_few_connections_is_not_pushed()
    {
        using var box = new Sandbox();
        using var limited = new TestServer(4_000_000) { ChunkDelayMs = 10, AllowedAtOnce = 3 };
        using (var engine = Engine(box, cap: 12, auto: true))
        {
            var item = engine.Add(new DownloadRequest(limited.BaseUrl + "/file.bin"));
            await engine.WhenIdleAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(DownloadState.Completed, item.State);
            Assert.Equal(limited.Data, File.ReadAllBytes(item.FinalPath));
        }
        var autoRefusals = limited.TooManyAnswers;

        // The same server with a fixed twelve: far more "too many" answers.
        using var box2 = new Sandbox();
        using var limited2 = new TestServer(4_000_000) { ChunkDelayMs = 10, AllowedAtOnce = 3 };
        using (var engine = Engine(box2, cap: 12, auto: false))
        {
            var item = engine.Add(new DownloadRequest(limited2.BaseUrl + "/file.bin"));
            await engine.WhenIdleAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(DownloadState.Completed, item.State);
        }
        Assert.True(autoRefusals < limited2.TooManyAnswers, $"automatic {autoRefusals} refusals, fixed {limited2.TooManyAnswers}");
    }

    [Fact]
    public async Task When_the_line_is_already_full_no_more_connections_are_added()
    {
        // The speed limit stands in for a full internet line: more connections cannot make it faster.
        using var box = new Sandbox();
        using var server = new TestServer(3_000_000);
        using var engine = Engine(box, cap: 12, auto: true, limitKBps: 600);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.True(server.MaxConcurrent <= 6, $"{server.MaxConcurrent} at once"); // 2, maybe one step of 2 more, plus the first look
    }

    [Fact]
    public async Task Fixed_connections_still_open_them_all_at_once()
    {
        using var box = new Sandbox();
        using var server = new TestServer(3_000_000) { ChunkDelayMs = 10 };
        using var engine = Engine(box, cap: 6, auto: false);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(DownloadState.Completed, item.State);
        Assert.True(server.MaxConcurrent >= 6, $"{server.MaxConcurrent} at once");
    }

    [Theory]
    [InlineData(8, 0)]  // the old default becomes automatic
    [InlineData(4, 4)]  // a number someone chose stays
    [InlineData(16, 16)]
    public void Updating_moves_only_the_old_default_to_automatic(int saved, int now)
    {
        using var box = new Sandbox();
        File.WriteAllText(Path.Combine(box.Root, "settings.json"), $"{{\"Version\": 2, \"DownloadConnections\": {saved}}}");
        var settings = Vireo.Services.Settings.Load(box.Root);
        Assert.Equal(now, settings.DownloadConnections);
        Assert.Equal(0, Vireo.Services.Settings.Load(Path.Combine(box.Root, "new")).DownloadConnections); // a first start: automatic
    }
}
