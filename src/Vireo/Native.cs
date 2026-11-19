using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Vireo;

/// <summary>The few Windows calls WPF does not wrap.</summary>
internal static class Native
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags; public uint Count; public uint Timeout; }

    /// <summary>Lights the window's taskbar button until the window is brought forward (how Windows apps say "something finished").</summary>
    public static void FlashTaskbar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        const uint Tray = 2, UntilForeground = 12;
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = hwnd, Flags = Tray | UntilForeground, Count = 3, Timeout = 0 };
        _ = FlashWindowEx(ref info);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    /// <summary>
    /// A popup (a separate window WPF draws above the page) is made "always on top" by WPF, so it floats over every program.
    /// This takes that away and puts it directly above <paramref name="owner"/> in the stack of windows: over Vireo, never
    /// over anything that is in front of Vireo.
    /// </summary>
    public static void KeepJustAbove(System.Windows.Controls.Primitives.Popup popup, Window owner)
    {
        if (popup.Child is null || PresentationSource.FromVisual(popup.Child) is not HwndSource source) return;
        var me = source.Handle;
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        if (ownerHandle == IntPtr.Zero) return;
        const uint NoSize = 0x1, NoMove = 0x2, NoActivate = 0x10;
        _ = SetWindowPos(me, new IntPtr(-2) /* not topmost */, 0, 0, 0, 0, NoSize | NoMove | NoActivate);
        // Placed after (below) the window that is just above the owner: that is, right on top of the owner.
        const uint Above = 3; // GW_HWNDPREV
        var aboveOwner = GetWindow(ownerHandle, Above);
        if (aboveOwner == me) return; // already there
        _ = SetWindowPos(me, aboveOwner == IntPtr.Zero ? IntPtr.Zero /* the top */ : aboveOwner, 0, 0, 0, 0, NoSize | NoMove | NoActivate);
    }

    /// <summary>Every time <paramref name="popup"/> opens, it is put just above <paramref name="owner"/> (see <see cref="KeepJustAbove"/>).</summary>
    public static void KeepWithWindow(System.Windows.Controls.Primitives.Popup popup, Window owner) =>
        popup.Opened += (_, _) => KeepJustAbove(popup, owner);

    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int RoundCorners = 2;

    /// <summary>Round corners (Windows 11) and a dark frame, so the window matches the rest of the browser.</summary>
    public static void StyleWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var dark = 1;
        var round = RoundCorners;
        _ = DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, WindowCornerPreference, ref round, sizeof(int));
    }

    public static (int X, int Y, int Width, int Height) WindowBounds(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var r);
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    /// <summary>
    /// Rounds the corners of a native child window (a web page), which WPF cannot clip. <paramref name="radius"/> and the size are
    /// in pixels; a radius of 0 makes it square again. Windows owns the region once it is set.
    /// </summary>
    public static void RoundWindow(IntPtr hwnd, int width, int height, int radius)
    {
        if (hwnd == IntPtr.Zero || width <= 0 || height <= 0) return;
        var region = radius <= 0 ? IntPtr.Zero : CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        _ = SetWindowRgn(hwnd, region, true);
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minSize, IntPtr maxSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public int Size;
        public int PageFaultCount;
        public IntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    /// <summary>
    /// The memory a process has in use (its working set), read directly. (System.Diagnostics.Process takes a snapshot of every
    /// process on the computer to answer this for one; for twenty engine processes that made the menu wait.) Null if it ended.
    /// </summary>
    public static long? WorkingSetOf(int processId)
    {
        const uint QueryLimitedInformation = 0x1000;
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var c = new ProcessMemoryCounters { Size = Marshal.SizeOf<ProcessMemoryCounters>() };
            return GetProcessMemoryInfo(handle, out c, c.Size) ? (long)c.WorkingSetSize : null;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>Hints the OS to reclaim free pages from our working set, ideal after discarding tabs.</summary>
    public static void TrimWorkingSet()
    {
        try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); }
        catch { /* best effort */ }
    }
}
