using System.Diagnostics;

namespace Vireo.Downloads;

/// <summary>
/// A shared speed limit. Every chunk of data spends bytes from a bucket that refills at the limit;
/// if the bucket runs dry the caller sleeps off the debt, so the total stays at the limit
/// however many connections are running.
/// </summary>
public sealed class RateLimiter
{
    private readonly object _lock = new();
    private double _tokens;
    private long _last = Stopwatch.GetTimestamp();

    /// <summary>Bytes per second across all downloads. Zero or less means no limit.</summary>
    public long BytesPerSecond { get; set; }

    public async Task WaitAsync(int bytes, CancellationToken ct)
    {
        var rate = BytesPerSecond;
        if (rate <= 0) return;
        TimeSpan wait;
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            _tokens = Math.Min(rate, _tokens + (now - _last) * (double)rate / Stopwatch.Frequency); // at most one second banked
            _last = now;
            _tokens -= bytes; // below zero is a debt
            wait = _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / rate);
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }
}
