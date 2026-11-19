using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Vireo.Downloads;

namespace Vireo.Services;

/// <summary>
/// Notices a link to a file copied in another program, so the downloader can offer it (only when the person turned it on).
/// Windows tells us when the clipboard changes (AddClipboardFormatListener: no polling). What was copied is looked at here,
/// on this computer, and only kept if it is a single web address to a file; everything else is ignored at once and never
/// stored. Copies made in Vireo itself, and anything a password manager marks as private, are not even read.
/// </summary>
public sealed class ClipboardWatch : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);

    private readonly HwndSource _window;
    private bool _pending;
    private bool _disposed;

    /// <summary>A web address to a file was copied in another program.</summary>
    public event Action<Uri>? LinkCopied;

    public ClipboardWatch()
    {
        _window = new HwndSource(new HwndSourceParameters("Vireo clipboard") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(OnMessage);
        AddClipboardFormatListener(_window.Handle);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Read a moment later, outside this message: the program that copied may still hold the clipboard, and several
        // updates in a row (some programs write several formats) are looked at once.
        if (msg == WmClipboardUpdate && !_pending && !_disposed)
        {
            _pending = true;
            _window.Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(150);
                _pending = false;
                if (!_disposed && !CopiedInVireo() && await ReadAsync() is { } link) LinkCopied?.Invoke(link);
            }, DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    /// <summary>Links copied in Vireo (its address bar, a page in it) are not offered back to the person.</summary>
    private static bool CopiedInVireo()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == Environment.ProcessId;
    }

    private static async Task<Uri?> ReadAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // Password managers mark what they copy as not for clipboard tools; it is not read at all.
                if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return null;
                if (!Clipboard.ContainsText()) return null;
                return FileLink(Clipboard.GetText());
            }
            catch (ExternalException) { /* another program has the clipboard open for a moment */ }
            await Task.Delay(60);
        }
        return null;
    }

    /// <summary>The copied text as a link to a file, or null: one web address (http or https) to a file, or a file host's page.</summary>
    public static Uri? FileLink(string text)
    {
        var t = text.Trim();
        if (t.Length is < 10 or > 2048 || t.Contains('\n') || t.Contains(' ')) return null;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) return null;
        return FileTypes.IsFileAddress(uri) || FileHosts.DirectAddress(uri) is not null ? uri : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveClipboardFormatListener(_window.Handle);
        _window.Dispose();
    }
}
