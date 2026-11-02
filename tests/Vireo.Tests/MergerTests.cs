using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Vireo.Media;
using Xunit;

namespace Vireo.Tests;

/// <summary>Merges real fragmented MP4 made by ffmpeg (from the Media Lab test media) and checks the result with ffprobe.</summary>
public class MergerTests
{
    private static string? Dir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests", "server", "media", "dash-real"))) d = d.Parent;
        return d is null ? null : Path.Combine(d.FullName, "tests", "server", "media", "dash-real");
    }

    private static string Join(string dir, int stream, string target)
    {
        using var o = File.Create(target);
        using (var init = File.OpenRead(Path.Combine(dir, $"init-stream{stream}.m4s"))) init.CopyTo(o);
        foreach (var f in Directory.GetFiles(dir, $"chunk-stream{stream}-*.m4s").OrderBy(x => x))
            using (var c = File.OpenRead(f)) c.CopyTo(o);
        return target;
    }

    private static JsonElement Probe(string path)
    {
        var psi = new ProcessStartInfo("ffprobe", $"-v error -show_format -show_streams -of json \"{path}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var json = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return JsonDocument.Parse(json).RootElement;
    }

    private static bool HasFfprobe()
    {
        try { using var p = Process.Start(new ProcessStartInfo("ffprobe", "-version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }); p!.WaitForExit(); return p.ExitCode == 0; }
        catch { return false; }
    }

    [Fact]
    public void Video_and_audio_files_become_one_file_with_both_tracks()
    {
        var dir = Dir();
        if (dir is null || !HasFfprobe()) return; // needs the test media and ffprobe
        var work = Path.Combine(AppContext.BaseDirectory, "tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        // Which stream is video and which is audio: ask ffprobe about the parts first.
        string? video = null, audio = null;
        foreach (var n in new[] { 0, 1, 2 })
        {
            var part = Join(dir, n, Path.Combine(work, $"s{n}.mp4"));
            var kinds = Probe(part).GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()).ToList();
            if (kinds.Contains("video") && video is null) video = part;
            if (kinds.Contains("audio") && !kinds.Contains("video")) audio = part;
        }
        Assert.NotNull(video);
        Assert.NotNull(audio);
        Assert.True(Fmp4Merger.CanMerge(video!, audio!));

        var outPath = Path.Combine(work, "merged.mp4");
        Fmp4Merger.Merge(video!, audio!, outPath);

        var probe = Probe(outPath);
        var streams = probe.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()).ToList();
        Assert.Equal(["video", "audio"], streams);
        var duration = double.Parse(probe.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var videoDuration = double.Parse(Probe(video!).GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(duration, videoDuration - 0.6, videoDuration + 0.6);

        // And every frame and sound sample is really there: decode the whole thing.
        var decode = Process.Start(new ProcessStartInfo("ffmpeg", $"-v error -i \"{outPath}\" -f null -") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        var errors = decode.StandardError.ReadToEnd();
        decode.WaitForExit();
        Assert.Equal("", errors.Trim());
        try { Directory.Delete(work, true); } catch { /* still closing */ }
    }

    [Fact]
    public void A_file_that_is_not_fragmented_is_reported_as_not_mergeable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tmp", Guid.NewGuid().ToString("N") + ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 8, (byte)'f', (byte)'r', (byte)'e', (byte)'e' });
        Assert.False(Fmp4Merger.CanMerge(path, path));
        File.Delete(path);
    }
}
