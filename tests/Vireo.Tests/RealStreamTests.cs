using System.IO;
using System.Net.Http;
using Vireo.Downloads;
using Vireo.Media;
using Xunit;
using Xunit.Abstractions;

namespace Vireo.Tests;

/// <summary>
/// Real streams on the open internet (public test streams from Apple, Mux, NASA and Unified Streaming, none protected).
/// They only run when VIREO_LIVE=1 is set, because they need a network and take a minute or two.
/// </summary>
public class RealStreamTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("VIREO_LIVE") == "1" && FfmpegTools.Available;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<DetectedMedia> Probe(string url, MediaKind kind = MediaKind.Hls)
    {
        var m = new DetectedMedia { Url = url, Kind = kind };
        await MediaProbe.ProbeAsync(m, Http, (u, r) =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, u);
            RequestHeaders.Apply(req, u, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130.0 Safari/537.36", r, null, script: true);
            return req;
        }, CancellationToken.None);
        return m;
    }

    private async Task<DownloadItem> Save(DetectedMedia m, MediaChoice choice, string name, int seconds = 240, Func<DownloadItem, Task>? during = null)
    {
        using var box = new Sandbox();
        var engine = box.Engine(connections: 6);
        var estimate = choice.SizeBytes ?? 0;
        var item = engine.Add(new DownloadRequest(choice.Url, name, null, null, null, Stream: new StreamSpec(choice.Url, choice.AudioUrl, m.Live, estimate)));
        if (during is not null) { await during(item); engine.StopLive(item); }
        await engine.WhenIdleAsync(TimeSpan.FromSeconds(seconds));
        Keep(item);
        return item;
    }

    private static readonly List<(string Path, DownloadItem Item)> Kept = [];

    private void Keep(DownloadItem item)
    {
        // the sandbox deletes its folder: copy the result out for the checks that follow
        var copy = Path.Combine(Path.GetTempPath(), "vireo-real-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(item.FileName));
        if (item.State == DownloadState.Completed && File.Exists(item.FinalPath)) File.Copy(item.FinalPath, copy);
        Kept.Add((copy, item));
        output.WriteLine($"{item.FileName}: {item.State} {item.Error} {item.Note} {(File.Exists(copy) ? new FileInfo(copy).Length / 1024 + " KB" : "")}");
    }

    private static string CopyOf(DownloadItem item) => Kept.Last(k => k.Item == item).Path;

    [Fact]
    public async Task A_public_ten_minute_transport_stream_becomes_a_playable_mp4_of_the_right_length()
    {
        if (!Enabled) return;
        var m = await Probe("https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8");
        Assert.Equal(MediaStatus.Ready, m.Status);
        output.WriteLine("qualities: " + string.Join(", ", m.Choices.Select(c => $"{c.Label} {c.SizeBytes / 1024} KB")));
        var lowest = m.Choices.Where(c => !c.AudioOnly).OrderBy(c => c.SizeBytes ?? long.MaxValue).First();
        var item = await Save(m, lowest, "mux.mp4", seconds: 300);
        Assert.Equal(DownloadState.Completed, item.State);
        var path = CopyOf(item);
        try
        {
            Assert.EndsWith(".mp4", item.FileName);
            Assert.Contains("video", FfmpegTools.StreamKinds(path));
            Assert.InRange(FfmpegTools.Duration(path), 600, 660); // 10 min 34 s
            Assert.Equal("", FfmpegTools.DecodeErrors(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_ts/master.m3u8", "Apple, transport stream with separate sound")]
    [InlineData("https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_fmp4/master.m3u8", "Apple, fragmented MP4 with separate sound")]
    public async Task A_public_test_stream_with_its_sound_in_a_separate_list_ends_as_one_file_with_both(string url, string what)
    {
        if (!Enabled) return;
        var m = await Probe(url);
        output.WriteLine($"{what}: {m.Status} {m.Error}; " + string.Join(", ", m.Choices.Select(c => $"{c.Label}{(c.AudioUrl is not null ? "+audio" : "")}")));
        Assert.Equal(MediaStatus.Ready, m.Status);
        var choice = m.Choices.Where(c => !c.AudioOnly).OrderBy(c => c.SizeBytes ?? long.MaxValue).First();
        Assert.NotNull(choice.AudioUrl);
        var item = await Save(m, choice, "apple.mp4", seconds: 300);
        Assert.Equal(DownloadState.Completed, item.State);
        var path = CopyOf(item);
        try
        {
            Assert.Null(item.Note); // joined, not left as two files
            Assert.Contains("video", FfmpegTools.StreamKinds(path));
            Assert.Contains("audio", FfmpegTools.StreamKinds(path));
            Assert.True(FfmpegTools.Duration(path) > 60);
            Assert.Equal("", FfmpegTools.DecodeErrors(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("https://demo.unified-streaming.com/k8s/live/stable/scte35.isml/.m3u8", "Unified Streaming live demo")]
    public async Task A_public_live_stream_is_recorded_for_a_while_and_stops_when_asked(string url, string what)
    {
        if (!Enabled) return;
        var m = await Probe(url);
        output.WriteLine($"{what}: {m.Status} {m.Error} live={m.Live}; " + string.Join(", ", m.Choices.Select(c => c.Label)));
        Assert.Equal(MediaStatus.Ready, m.Status);
        Assert.True(m.Live);
        var choice = m.Choices.Where(c => !c.AudioOnly).OrderBy(c => c.SizeBytes ?? long.MaxValue).ThenBy(c => c.Label).First();
        var item = await Save(m, choice, "live.mp4", seconds: 120, during: async i =>
        {
            for (var n = 0; n < 60 && i.Done0 < 5; n++) await Task.Delay(1000); // about ten seconds of video
            await Task.Delay(1500);
        });
        Assert.Equal(DownloadState.Completed, item.State);
        var path = CopyOf(item);
        try
        {
            Assert.Contains("video", FfmpegTools.StreamKinds(path));
            Assert.True(FfmpegTools.Duration(path) > 8, $"{FfmpegTools.Duration(path)} s");
            Assert.Equal("", FfmpegTools.DecodeErrors(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_public_live_dash_stream_is_recorded_for_a_while_and_stops_when_asked()
    {
        if (!Enabled) return;
        var m = await Probe("https://livesim2.dashif.org/livesim2/testpic_2s/Manifest.mpd", MediaKind.Dash);
        output.WriteLine($"DASH-IF live simulator: {m.Status} {m.Error} live={m.Live}; " + string.Join(", ", m.Choices.Select(c => c.Label)));
        Assert.Equal(MediaStatus.Ready, m.Status);
        Assert.True(m.Live);
        var choice = m.Choices.Where(c => !c.AudioOnly).OrderBy(c => c.SizeBytes ?? long.MaxValue).First();
        var item = await Save(m, choice, "dashlive.mp4", seconds: 120, during: async i =>
        {
            for (var n = 0; n < 60 && i.Done0 < 5; n++) await Task.Delay(1000);
            await Task.Delay(1500);
        });
        Assert.Equal(DownloadState.Completed, item.State);
        var path = CopyOf(item);
        try
        {
            Assert.Contains("video", FfmpegTools.StreamKinds(path));
            Assert.True(FfmpegTools.Duration(path) > 6, $"{FfmpegTools.Duration(path)} s");
            Assert.Equal("", FfmpegTools.DecodeErrors(path));
        }
        finally { File.Delete(path); }
    }
}
