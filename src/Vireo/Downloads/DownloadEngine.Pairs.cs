using System.IO;
using Vireo.Media;

namespace Vireo.Downloads;

/// <summary>A video file and an audio file that are fetched together and joined into one.</summary>
public sealed record PairSpec(string VideoUrl, long VideoLength, string AudioUrl, long AudioLength);

public sealed partial class DownloadEngine
{
    private DownloadItem NewTrack(DownloadItem parent, string url, long length, string name) => new()
    {
        Url = url, Folder = parent.Folder, FileName = name, Parent = parent, Connections = parent.Connections, Total = length,
        UserAgent = parent.UserAgent, Referer = parent.Referer, Cookie = parent.Cookie,
    };

    private async Task RunTrackAsync(DownloadItem child, CancellationToken ct)
    {
        lock (child.Lock) { if (child.Segments.Count == 0) DiscardPartial(child); }
        await ProbeAsync(child, ct);
        using var file = File.OpenHandle(child.PartPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        if (child.CanResume && child.Total > 0 && RandomAccess.GetLength(file) < child.Total) RandomAccess.SetLength(file, child.Total);
        await TransferAsync(child, file, ct);
    }

    private async Task RunPairAsync(DownloadItem item, CancellationToken ct)
    {
        var spec = item.Pair!;
        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        if (item.Children.Count == 0)
        {
            item.Children.Add(NewTrack(item, spec.VideoUrl, spec.VideoLength, stem + ".video.mp4"));
            item.Children.Add(NewTrack(item, spec.AudioUrl, spec.AudioLength, stem + ".audio.m4a"));
        }
        item.Total = spec.VideoLength + spec.AudioLength;
        await Task.WhenAll(item.Children.Select(c => RunTrackAsync(c, ct)));

        var video = item.Children[0].PartPath;
        var audio = item.Children[1].PartPath;
        item.Note = "Joining video and audio…";
        Changed(item, save: false);
        var target = FileNaming.Unique(item.Folder, stem + ".mp4", _ => false);
        string? note = null;
        var merged = false;
        if (Fmp4Merger.CanMerge(video, audio))
        {
            await Task.Run(() => Fmp4Merger.Merge(video, audio, Path.Combine(item.Folder, target)), ct);
            merged = true;
        }
        else if (Ffmpeg.Find() is not null)
        {
            merged = await Ffmpeg.MakeAsync($"-i \"{video}\" -i \"{audio}\" -c copy \"{Path.Combine(item.Folder, target)}\"", Path.Combine(item.Folder, target), ct);
        }

        if (merged)
        {
            File.Delete(video);
            File.Delete(audio);
        }
        else
        {
            File.Move(video, Path.Combine(item.Folder, target));
            var audioName = FileNaming.Unique(item.Folder, stem + " (audio).m4a", _ => false);
            File.Move(audio, Path.Combine(item.Folder, audioName));
            note = $"Video and audio were saved as two files ({audioName}).";
        }

        item.Children.Clear();
        item.FileName = target;
        Mark(item.FinalPath, item.Url, item.Referer);
        item.State = DownloadState.Completed;
        item.CompletedAt = DateTime.Now;
        item.Total = new FileInfo(item.FinalPath).Length;
        item.Speed = 0;
        item.Error = null;
        item.Note = note;
        lock (item.Lock) item.ActiveConnections = 0;
        Changed(item, save: true);
    }
}
