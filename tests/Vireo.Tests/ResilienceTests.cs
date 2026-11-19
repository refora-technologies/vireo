using System.IO;
using Vireo.Downloads;
using Xunit;

namespace Vireo.Tests;

/// <summary>What happens when the network or the server misbehaves.</summary>
public class ResilienceTests
{
    [Fact]
    public async Task A_server_having_a_moment_is_asked_again_and_the_download_completes()
    {
        using var box = new Sandbox();
        using var server = new TestServer(800_000) { Unavailable503First = 2 };
        using var engine = box.Engine(connections: 3);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath));
        Assert.Null(item.Note); // the "reconnecting" message is gone
    }

    [Fact]
    public async Task A_connection_that_goes_silent_mid_file_is_cut_and_the_file_still_completes()
    {
        // The server stops sending on two connections but leaves them open: before the watchdog these hung for ever.
        using var box = new Sandbox();
        using var server = new TestServer(3_000_000) { HangFirst = 2, HangAfterBytes = 200_000 };
        using var engine = box.Engine(connections: 4);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath));
    }

    [Fact]
    public async Task A_server_that_never_answers_a_request_is_asked_again()
    {
        // No answer at all (not even headers) to the first requests, including the first look at the file.
        using var box = new Sandbox();
        using var server = new TestServer(1_500_000) { HangFirst = 3, HangAfterBytes = 0 };
        using var engine = box.Engine(connections: 3);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath));
    }

    [Fact]
    public async Task Connections_that_keep_dropping_do_not_end_the_download()
    {
        // Far more drops than one connection tolerates: before, the first connection to give up ended the whole download.
        using var box = new Sandbox();
        using var server = new TestServer(4_000_000) { DropFirst = 40, DropAfterBytes = 50_000 };
        using var engine = box.Engine(connections: 4);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(90));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath));
    }

    [Fact]
    public async Task An_outage_in_the_middle_is_waited_out_and_the_file_completes()
    {
        using var box = new Sandbox();
        using var server = new TestServer(3_000_000) { ChunkDelayMs = 10 };
        using var engine = box.Engine(connections: 3);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        for (var i = 0; i < 200 && item.Downloaded < 500_000; i++) await Task.Delay(25);
        Assert.True(item.Downloaded > 0, "it started");
        server.OutageUntil = DateTime.UtcNow.AddSeconds(6); // longer than all the old retries together
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(90));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath));
    }

    [Fact]
    public async Task While_the_computer_is_offline_it_waits_without_using_up_its_tries()
    {
        using var box = new Sandbox();
        using var server = new TestServer(1_000_000);
        using var engine = box.Engine(connections: 2);
        engine.Options.MaxReconnects = 1; // with tries counted, one failure would end it
        var online = false;
        engine.OnlineForTest = () => online;
        server.OutageUntil = DateTime.UtcNow.AddSeconds(5);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        for (var i = 0; i < 300 && item.Note?.Contains("waiting for the internet") != true; i++) await Task.Delay(20);
        Assert.Contains("waiting for the internet", item.Note);
        await Task.Delay(5500);
        online = true;
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath));
    }

    [Fact]
    public async Task A_host_that_allows_few_connections_still_gets_the_whole_file()
    {
        // A free file host answering "too many requests" beyond two connections at once.
        using var box = new Sandbox();
        using var server = new TestServer(3_000_000) { AllowedAtOnce = 2, ChunkDelayMs = 2 };
        using var engine = box.Engine(connections: 8);
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(90));

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath));
        Assert.True(server.TooManyAnswers > 0, "the host did refuse some connections");
    }

    [Fact]
    public async Task A_download_that_fails_says_why_in_plain_words()
    {
        using var box = new Sandbox();
        using var server = new TestServer(1_000_000) { OutageUntil = DateTime.MaxValue };
        using var engine = box.Engine(connections: 2);
        engine.Options.MaxReconnects = 2;
        engine.OnlineForTest = () => true;
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(DownloadState.Failed, item.State);
        Assert.DoesNotContain("operation was canceled", item.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(item.Error));
    }

    [Fact]
    public async Task A_missing_file_fails_at_once_without_pointless_retries()
    {
        using var box = new Sandbox();
        using var server = new TestServer(1000);
        using var engine = box.Engine();
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/missing"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DownloadState.Failed, item.State);
        Assert.NotNull(item.Error);
        Assert.True(server.Requests.Count <= 2, $"{server.Requests.Count} requests");
    }

    [Fact]
    public async Task A_server_that_never_recovers_ends_as_failed_with_a_message()
    {
        using var box = new Sandbox();
        using var server = new TestServer(1000);
        using var engine = box.Engine();
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/unavailable"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(40));

        Assert.Equal(DownloadState.Failed, item.State);
        Assert.False(string.IsNullOrWhiteSpace(item.Error));
        Assert.Null(item.Note);
    }

    [Fact]
    public async Task Pausing_while_it_waits_to_reconnect_pauses_it_instead_of_leaving_it_stuck()
    {
        using var box = new Sandbox();
        using var server = new TestServer(1000);
        using var engine = box.Engine();
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/unavailable"));
        for (var i = 0; i < 100 && item.Note?.StartsWith("Reconnecting") != true; i++) await Task.Delay(30);
        Assert.StartsWith("Reconnecting", item.Note);

        engine.Pause(item);
        for (var i = 0; i < 100 && item.State == DownloadState.Downloading; i++) await Task.Delay(30);
        Assert.Equal(DownloadState.Paused, item.State);
        Assert.Null(item.Note);

        // and it can be started again
        engine.Cancel(item);
        Assert.Equal(DownloadState.Cancelled, item.State);
    }

    [Fact]
    public async Task Cancelling_while_it_waits_to_reconnect_cancels_it()
    {
        using var box = new Sandbox();
        using var server = new TestServer(1000);
        using var engine = box.Engine();
        var item = engine.Add(new DownloadRequest(server.BaseUrl + "/unavailable"));
        for (var i = 0; i < 100 && item.Note?.StartsWith("Reconnecting") != true; i++) await Task.Delay(30);

        engine.Cancel(item);
        for (var i = 0; i < 100 && item.State == DownloadState.Downloading; i++) await Task.Delay(30);
        Assert.Equal(DownloadState.Cancelled, item.State);
    }

    [Fact]
    public async Task A_plain_download_looks_like_a_navigation_and_names_where_it_came_from()
    {
        using var box = new Sandbox();
        using var server = new TestServer(5000);
        using var engine = box.Engine();
        engine.Add(new DownloadRequest(server.BaseUrl + "/file.bin", Referer: "https://page.example/some/page"));
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(10));

        var h = server.Headers.First();
        Assert.Equal("https://page.example/some/page", h["Referer"]);
        Assert.Null(h["Origin"]);         // a navigation does not send one
        Assert.Null(h["Sec-Fetch-Mode"]);
        Assert.Equal("identity", h["Accept-Encoding"]);
    }

    [Theory]
    [InlineData("https://a.example/x", "https://a.example/page", "same-origin")]
    [InlineData("https://cdn.a.example/x", "https://www.a.example/page", "same-site")]
    [InlineData("https://other.test/x", "https://a.example/page", "cross-site")]
    [InlineData("https://a.example/x", null, "none")]
    public void The_relation_between_page_and_address_is_described_like_a_browser_would(string url, string? referer, string expected) =>
        Assert.Equal(expected, RequestHeaders.SiteRelation(url, referer));

    [Fact]
    public async Task A_stream_piece_refused_for_its_referer_is_fetched_with_another_and_the_choice_is_remembered()
    {
        using var server = new TestServer(60_000);
        using var http = new System.Net.Http.HttpClient();
        var downloader = new StreamDownloader(http, url =>
        {
            var r = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            RequestHeaders.Apply(r, url, "test", "https://elsewhere.example/page", null, script: true);
            return r;
        }, new RateLimiter(), 2);

        // a piece that only answers when it is asked without a referer
        var first = await GetAsync(downloader, server.BaseUrl + "/refuses-referer");
        var second = await GetAsync(downloader, server.BaseUrl + "/refuses-referer");
        Assert.Equal(60_000, first.Length);
        Assert.Equal(60_000, second.Length);
        // the second piece went straight to the version that works: one request, not three
        var all = server.Requests.Count(r => r.StartsWith("/refuses-referer"));
        Assert.Equal(4, all); // first: refused, retried with the site's own address (refused), with none (ok); second: ok at once
    }

    private static Task<byte[]> GetAsync(StreamDownloader d, string url)
    {
        var m = typeof(StreamDownloader).GetMethod("GetBytesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task<byte[]>)m.Invoke(d, [url, null, null, CancellationToken.None])!;
    }
}
