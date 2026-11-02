using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Vireo.Services;

/// <summary>
/// One Vireo per profile. Starting Vireo again (a link clicked in another program, the shortcut pressed twice) hands the
/// request to the running window instead of starting a second process on the same profile folder.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();
    public bool IsFirst { get; }

    /// <summary>Tells the running window what to open: an address, or an empty text to just come forward.</summary>
    public event Action<string>? Received;

    public SingleInstance(string profileFolder)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profileFolder).TrimEnd('\\').ToLowerInvariant())))[..16];
        _pipe = "Vireo-" + key;
        _mutex = new Mutex(true, @"Local\Vireo-" + key, out var created);
        IsFirst = created;
        if (created) _ = Task.Run(ListenAsync);
    }

    /// <summary>Sends a request to the running window. False when nobody answered.</summary>
    public bool Send(string what, int timeoutMs = 4000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(what);
            return true;
        }
        catch { return false; }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = (await reader.ReadLineAsync(_stop.Token)) ?? "";
                // Only addresses and "come forward" are accepted: this pipe is open to any program of the same user.
                if (line.Length == 0 || (line.Length < 8192 && !line.Contains('\n') && !line.Contains('\0'))) Received?.Invoke(line);
            }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(200); }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (IsFirst) try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        _mutex.Dispose();
    }
}
