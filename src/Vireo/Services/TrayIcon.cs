using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Vireo.Services;

/// <summary>
/// Vireo's icon in the notification area (system tray), through Windows' own API: no extra libraries, a few kilobytes.
/// Shows a tooltip, says when it is clicked, and can show a short balloon message. Comes back if Explorer restarts.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 77; // WM_APP + 77
    private const int WmLeftButtonUp = 0x0202, WmRightButtonUp = 0x0205, BalloonClicked = 0x0405;
    private const int NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const int NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, int count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private readonly int _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private string _tip;
    private bool _disposed;

    /// <summary>Clicked (either button). Raised on the window's thread.</summary>
    public event Action? Clicked;

    public TrayIcon(string tip)
    {
        _tip = Short(tip, 127);
        // A message-only kind of window to receive the icon's clicks (never shown).
        _window = new HwndSource(new HwndSourceParameters("Vireo tray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(OnMessage);
        var small = new IntPtr[1];
        _icon = ExtractIconEx(Environment.ProcessPath ?? "", 0, null, small, 1) > 0 ? small[0] : IntPtr.Zero;
        Add();
    }

    /// <summary>The text shown when the pointer rests on the icon (Windows keeps up to 127 characters).</summary>
    public string Tip
    {
        get => _tip;
        set
        {
            var tip = Short(value, 127);
            if (tip == _tip) return;
            _tip = tip;
            var data = Data(NifTip);
            Shell_NotifyIcon(NimModify, ref data);
        }
    }

    /// <summary>A short message from the icon (Windows shows it as a notification).</summary>
    public void Balloon(string title, string text)
    {
        var data = Data(NifInfo);
        data.InfoTitle = Short(title, 63);
        data.Info = Short(text, 255);
        data.InfoFlags = 0x4 | 0x20; // the app's own icon, large
        Shell_NotifyIcon(NimModify, ref data);
    }

    private void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip);
        Shell_NotifyIcon(NimAdd, ref data);
    }

    private NotifyIconData Data(int flags) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(), Window = _window.Handle, Id = 1, Flags = flags,
        CallbackMessage = CallbackMessage, Icon = _icon, Tip = _tip, Info = "", InfoTitle = "",
    };

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            var what = (int)(lParam.ToInt64() & 0xFFFF);
            if (what is WmLeftButtonUp or WmRightButtonUp or BalloonClicked) { Clicked?.Invoke(); handled = true; }
        }
        else if (msg == _taskbarCreated) Add(); // Explorer restarted: the notification area is new and empty
        return IntPtr.Zero;
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = Data(0);
        Shell_NotifyIcon(NimDelete, ref data);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _window.Dispose();
    }
}
