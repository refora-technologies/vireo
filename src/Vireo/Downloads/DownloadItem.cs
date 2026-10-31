using System.IO;
namespace Vireo.Downloads;

public enum DownloadState { Queued, Downloading, Paused, Completed, Failed, Cancelled }

/// <summary>One stretch of the file that one connection is fetching. Segments can be split while they run.</summary>
public sealed class Segment
{
    public long Start { get; set; }
    /// <summary>Last byte of this stretch (inclusive). Shrinks when another connection takes over its tail.</summary>
    public long End { get; set; }
    /// <summary>The next byte that has NOT been written yet. Everything from Start up to here is on disk.</summary>
    public long Done { get; set; }
    public bool Active { get; set; }

    public long Remaining => Math.Max(0, End - Done + 1);
}

/// <summary>What the person asked to download, plus what the page knew (so logged-in downloads work).</summary>
public sealed record DownloadRequest(string Url, string? FileName = null, string? Referer = null, string? UserAgent = null, string? Cookie = null, string? Folder = null, StreamSpec? Stream = null, PairSpec? Pair = null);

public sealed class DownloadItem
{
    internal readonly object Lock = new();
    internal CancellationTokenSource? Cts;
    internal Task? Run;
    internal StopReason Stop;
    internal long StreamBytes;       // bytes of a download that cannot be split or resumed
    internal int RetryAttempts;
    internal long WindowBytes;       // bytes since the last speed sample
    internal DateTime SampleAt = DateTime.UtcNow;
    internal DateTime LastSaved = DateTime.MinValue;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Url { get; init; } = "";
    public string? Referer { get; init; }
    public string? UserAgent { get; init; }
    public string? Cookie { get; init; }
    public string Folder { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? ContentType { get; set; }

    /// <summary>Size in bytes, or -1 while unknown.</summary>
    public long Total { get; set; } = -1;
    /// <summary>The server allows asking for part of the file, so it can be split and resumed.</summary>
    public bool CanResume { get; set; }
    public int Connections { get; set; } = 8;

    public DownloadState State { get; set; } = DownloadState.Queued;
    public string? Error { get; set; }
    public double Speed { get; set; }
    public int ActiveConnections { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }

    public List<Segment> Segments { get; } = [];

    // ---- Streams (HLS and friends): a download made of many pieces ----
    public StreamSpec? Stream { get; set; }
    /// <summary>Two files fetched side by side and joined at the end (YouTube's video and audio).</summary>
    public PairSpec? Pair { get; set; }
    internal DownloadItem? Parent;
    internal List<DownloadItem> Children { get; } = [];
    /// <summary>A line shown under the name while something special is going on ("Recording 02:13", "Joining video and audio…").</summary>
    public string? Note { get; set; }
    internal volatile bool FinishRequested;
    public int StreamStage { get; set; }
    public int Done0 { get; set; }
    public long Bytes0 { get; set; }
    public int Done1 { get; set; }
    public long Bytes1 { get; set; }
    public bool Fragmented0 { get; set; }
    public bool Fragmented1 { get; set; }
    public string? Ext0 { get; set; }
    public string? Ext1 { get; set; }

    public string FinalPath => Path.Combine(Folder, FileName);
    public string PartPath => FinalPath + ".vireopart";

    public long Downloaded
    {
        get
        {
            lock (Lock)
            {
                if (Children.Count > 0) return Children.Sum(c => c.Downloaded);
                if (Segments.Count == 0) return Interlocked.Read(ref StreamBytes);
                return Segments.Sum(s => s.Done - s.Start);
            }
        }
    }

    public double Fraction => State == DownloadState.Completed ? 1 : Total > 0 ? Math.Clamp((double)Downloaded / Total, 0, 1) : 0;

    public TimeSpan? Eta => Speed > 1 && Total > 0 ? TimeSpan.FromSeconds((Total - Downloaded) / Speed) : null;

    public bool IsActive => State is DownloadState.Downloading or DownloadState.Queued;

    public event Action<DownloadItem>? Changed;
    internal void RaiseChanged() => Changed?.Invoke(this);

    /// <summary>Next stretch to work on: one nobody has, else the larger half of the biggest stretch in progress.</summary>
    internal Segment? TakeSegment(long minSplit)
    {
        lock (Lock)
        {
            foreach (var s in Segments)
                if (!s.Active && s.Remaining > 0) { s.Active = true; return s; }

            var victim = Segments.Where(s => s.Active).OrderByDescending(s => s.Remaining).FirstOrDefault();
            if (victim is null || victim.Remaining < 2 * minSplit) return null;

            // The victim keeps the front half. Its owner stops when it reaches the new end.
            var mid = victim.Done + victim.Remaining / 2 - 1;
            var taken = new Segment { Start = mid + 1, End = victim.End, Done = mid + 1, Active = true };
            victim.End = mid;
            Segments.Add(taken);
            return taken;
        }
    }
}

internal enum StopReason { None, Pause, Cancel, Shutdown }
