using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Vireo.Media;

namespace Vireo.Downloads;

/// <summary>What a stream download is made of: one playlist, or a video playlist and a separate audio one.</summary>
public sealed record StreamSpec(string PlaylistUrl, string? AudioUrl = null, bool Live = false, long EstimatedBytes = 0);

/// <summary>Finds ffmpeg, if there is one, for the few merges Vireo cannot do on its own (MPEG-TS to MP4).</summary>
public static class Ffmpeg
{
    private static string? _found;
    private static bool _looked;

    /// <summary>Tests switch it off to prove the built-in paths work without it.</summary>
    public static bool Disabled { get; set; }

    public static string? Find()
    {
        if (Disabled) return null;
        if (_looked) return _found;
        _looked = true;
        var names = new[] { Environment.GetEnvironmentVariable("VIREO_FFMPEG"), Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") };
        _found = names.FirstOrDefault(n => !string.IsNullOrEmpty(n) && File.Exists(n));
        if (_found is not null) return _found;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate)) return _found = candidate;
            }
            catch { /* a bad entry in PATH */ }
        }
        return null;
    }

    /// <summary>Runs ffmpeg to make <paramref name="output"/>; a failed run leaves nothing behind.</summary>
    public static async Task<bool> MakeAsync(string args, string output, CancellationToken ct)
    {
        var ok = await RunAsync(args, ct);
        if (!ok) try { File.Delete(output); } catch { /* not there */ }
        return ok;
    }

    public static async Task<bool> RunAsync(string args, CancellationToken ct)
    {
        var path = Find();
        if (path is null) return false;
        var psi = new ProcessStartInfo(path, "-v error -y " + args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var p = Process.Start(psi);
        if (p is null) return false;
        var errors = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 && (await errors).Trim().Length == 0;
    }
}

public sealed partial class DownloadEngine
{
    private HttpRequestMessage NewRequestFor(DownloadItem item, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        RequestHeaders.Apply(req, url, item.UserAgent ?? DefaultAgent, item.Referer, item.Cookie, script: true);
        return req;
    }

    private string VideoPart(DownloadItem i) => Path.Combine(i.Folder, Path.GetFileNameWithoutExtension(i.FileName) + ".video.vireopart");
    private string AudioPart(DownloadItem i) => Path.Combine(i.Folder, Path.GetFileNameWithoutExtension(i.FileName) + ".audio.vireopart");

    /// <summary>Asks a live recording to stop and keep what it has.</summary>
    public void StopLive(DownloadItem item)
    {
        if (item.Stream is { Live: true } && item.State == DownloadState.Downloading) item.FinishRequested = true;
    }

    private async Task RunStreamAsync(DownloadItem item, CancellationToken ct)
    {
        var spec = item.Stream!;
        var downloader = new StreamDownloader(_http, url => NewRequestFor(item, url), _limiter, item.Connections);
        StreamProgress video = new(), audio = new();
        var lastBytes = 0L;

        void Sync()
        {
            var now = video.Bytes + audio.Bytes;
            var delta = now - Interlocked.Exchange(ref lastBytes, now);
            if (delta > 0) Interlocked.Add(ref item.WindowBytes, delta);
            Interlocked.Exchange(ref item.StreamBytes, now);
            // The pair "pieces done / bytes written" must be saved together or a resume could start in the wrong place.
            lock (item.Lock)
            {
                item.Done0 = video.Done; item.Bytes0 = video.Bytes;
                item.Done1 = audio.Done; item.Bytes1 = audio.Bytes;
            }
            if (spec.Live) item.Note = $"Recording {TimeSpan.FromSeconds(video.RecordedSeconds):mm\\:ss} · {video.Done} pieces";
            else if (item.Total > 0 && now > item.Total * 0.97) item.Total = (long)(now * 1.04); // the estimate was low
        }
        video.Changed += Sync;
        audio.Changed += Sync;
        lastBytes = item.Bytes0 + item.Bytes1;
        if (!spec.Live) item.Total = spec.EstimatedBytes > 0 ? spec.EstimatedBytes : -1;

        string videoFile = VideoPart(item), audioFile = AudioPart(item);
        StreamTrackResult? v = null, a = null;
        Func<bool> stop = () => item.FinishRequested;

        if (spec.Live)
        {
            var tv = downloader.DownloadAsync(spec.PlaylistUrl, videoFile, video, ct, stop);
            var ta = spec.AudioUrl is null ? null : downloader.DownloadAsync(spec.AudioUrl, audioFile, audio, ct, stop);
            v = await tv;
            if (ta is not null) a = await ta;
        }
        else
        {
            if (item.StreamStage == 0)
            {
                v = await downloader.DownloadAsync(spec.PlaylistUrl, videoFile, video, ct, resumePieces: item.Done0, resumeBytes: item.Bytes0);
                item.Fragmented0 = v.Fragmented; item.Ext0 = v.Extension;
                item.StreamStage = 1;
                Save(item);
            }
            if (spec.AudioUrl is not null)
            {
                a = await downloader.DownloadAsync(spec.AudioUrl, audioFile, audio, ct, resumePieces: item.Done1, resumeBytes: item.Bytes1);
                item.Fragmented1 = a.Fragmented; item.Ext1 = a.Extension;
            }
            video.Done = item.Done0;
        }
        if (v is not null) { item.Fragmented0 = v.Fragmented; item.Ext0 = v.Extension; }
        if (a is not null) { item.Fragmented1 = a.Fragmented; item.Ext1 = a.Extension; }

        item.Note = "Finishing…";
        Changed(item, save: false);
        await FinishStreamAsync(item, spec.AudioUrl is not null, videoFile, audioFile, video.Skipped + audio.Skipped, ct);
    }

    /// <summary>Joins the parts into the finished file: natively when both are fragmented MP4, with ffmpeg if it is there, otherwise keeps them side by side.</summary>
    private async Task FinishStreamAsync(DownloadItem item, bool hasAudio, string videoFile, string audioFile, int skipped, CancellationToken ct)
    {
        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        string Target(string ext) => FileNaming.Unique(item.Folder, stem + "." + ext, _ => false);
        string? note = null;
        string finalName;

        if (hasAudio)
        {
            if (item.Fragmented0 && item.Fragmented1 && Fmp4Merger.CanMerge(videoFile, audioFile))
            {
                finalName = Target("mp4");
                item.Note = "Joining video and audio…";
                Changed(item, save: false);
                await Task.Run(() => Fmp4Merger.Merge(videoFile, audioFile, Path.Combine(item.Folder, finalName)), ct);
                File.Delete(videoFile);
                File.Delete(audioFile);
            }
            else if (!item.Fragmented0 && !item.Fragmented1 && await TryRemuxAsync([videoFile, audioFile], Path.Combine(item.Folder, finalName = Target("mp4")), ct))
            {
                File.Delete(videoFile);
                File.Delete(audioFile);
            }
            else if (Ffmpeg.Find() is not null && await Ffmpeg.MakeAsync($"-i \"{videoFile}\" -i \"{audioFile}\" -c copy \"{Path.Combine(item.Folder, finalName = Target("mp4"))}\"", Path.Combine(item.Folder, finalName), ct))
            {
                File.Delete(videoFile);
                File.Delete(audioFile);
            }
            else
            {
                // No way to join these two kinds of stream here: keep both, named so they sort together.
                finalName = Target(item.Ext0 ?? "ts");
                File.Move(videoFile, Path.Combine(item.Folder, finalName));
                var audioName = FileNaming.Unique(item.Folder, stem + " (audio)." + (item.Ext1 ?? "aac"), _ => false);
                File.Move(audioFile, Path.Combine(item.Folder, audioName));
                note = $"Video and audio were saved as two files ({audioName}). Install ffmpeg next to Vireo to have them joined.";
            }
        }
        else if (item.Fragmented0)
        {
            finalName = Target("mp4");
            File.Move(videoFile, Path.Combine(item.Folder, finalName));
        }
        else if ((item.Ext0 ?? "ts") == "ts" && await TryRemuxAsync([videoFile], Path.Combine(item.Folder, finalName = Target("mp4")), ct))
        {
            File.Delete(videoFile);
        }
        else if ((item.Ext0 ?? "ts") == "ts" && Ffmpeg.Find() is not null && await Ffmpeg.MakeAsync($"-i \"{videoFile}\" -c copy -bsf:a aac_adtstoasc \"{Path.Combine(item.Folder, finalName = Target("mp4"))}\"", Path.Combine(item.Folder, finalName), ct))
        {
            File.Delete(videoFile);
        }
        else
        {
            finalName = Target(item.Ext0 ?? "ts");
            File.Move(videoFile, Path.Combine(item.Folder, finalName));
        }

        item.FileName = finalName;
        Mark(item.FinalPath, item.Stream?.PlaylistUrl ?? item.Url, item.Referer);
        item.State = DownloadState.Completed;
        item.CompletedAt = DateTime.Now;
        item.Total = new FileInfo(item.FinalPath).Length;
        item.Speed = 0;
        item.Error = null;
        item.Note = skipped > 0 ? $"{skipped} piece{(skipped == 1 ? " was" : "s were")} no longer on the server and left out." + (note is null ? "" : " " + note) : note;
        lock (item.Lock) item.ActiveConnections = 0;
        Changed(item, save: true);
    }

    /// <summary>Vireo's own MPEG-TS to MP4 conversion; false when the streams are of a kind it does not handle (nothing is written then).</summary>
    private static async Task<bool> TryRemuxAsync(string[] inputs, string output, CancellationToken ct)
    {
        if (!TsRemuxer.CanRemux(inputs)) return false;
        try
        {
            await Task.Run(() => TsRemuxer.Remux(inputs, output), ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; } // anything unexpected inside the stream: keep the plain file instead
    }

    private void DiscardStreamParts(DownloadItem item)
    {
        if (item.Stream is null || item.FileName.Length == 0) return;
        DeleteSoon(VideoPart(item));
        DeleteSoon(AudioPart(item));
    }
}
