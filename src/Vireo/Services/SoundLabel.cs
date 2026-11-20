using System.Runtime.InteropServices;

namespace Vireo.Services;

/// <summary>
/// Puts Vireo's name and icon on the sound its pages play. The page engine plays every sound from a helper program of its own
/// (msedgewebview2.exe), so without this the Windows Volume mixer and other mixers that read an app's sound name show the
/// engine's name instead of Vireo's. Only sounds of processes started by this Vireo are touched; other programs built on the
/// same engine keep their own. Mixers that go by the program's file name only cannot be changed this way.
/// <para>
/// Windows makes a new sound entry each time sound starts on an output and each time the output changes, so this listens for
/// new entries on every output, and for outputs coming and going, and labels each new entry of ours. The work runs on a thread
/// of its own; nothing here can stop the browser: a failure only leaves the engine's name in the mixer.
/// </para>
/// </summary>
internal static class SoundLabel
{
    public const string Name = "Vireo";

    private static readonly object s_gate = new();
    private static readonly AutoResetEvent s_wake = new(false);
    private static Thread? s_thread;
    private static string s_icon = "";

    /// <summary>For tests: how many sound entries this run has labelled, and the last thing that went wrong (empty if nothing).</summary>
    public static int Labelled { get; private set; }
    public static string LastError { get; private set; } = "";

    /// <summary>Starts listening (once per run). Called when the page engine starts.</summary>
    public static void Start()
    {
        lock (s_gate)
        {
            if (s_thread is not null) { s_wake.Set(); return; }
            s_icon = (Environment.ProcessPath ?? "") + ",0";
            s_thread = new Thread(Run) { IsBackground = true, Name = "Vireo sound label" };
            s_thread.SetApartmentState(ApartmentState.MTA);
            s_thread.Start();
        }
    }

    /// <summary>Looks again now (a page has started playing sound).</summary>
    public static void Nudge() { if (s_thread is not null) s_wake.Set(); }

    private static void Run()
    {
        var outputs = new Outputs();
        try { outputs.Listen(); }
        catch (Exception e) { LastError = e.Message; return; } // no sound system (a server without audio): nothing to label
        while (true)
        {
            try
            {
                outputs.Refresh();
                Labelled += outputs.LabelOurs(s_icon);
            }
            catch (Exception e) { LastError = e.Message; }
            s_wake.WaitOne();
            Thread.Sleep(150); // a burst of events (a new output brings several) becomes one look
            s_wake.Reset();
        }
    }

    // ---- Which processes are ours ----

    /// <summary>
    /// True when <paramref name="pid"/> is a descendant of <paramref name="self"/>: the engine is started by Vireo and its helpers
    /// by the engine, so a helper is two steps away. <paramref name="parents"/> maps each process to the one that started it.
    /// The walk stops after a few steps (a parent's number can belong to a newer process after the parent ended).
    /// </summary>
    internal static bool IsOurs(int pid, int self, IReadOnlyDictionary<int, int> parents, int maxSteps = 4)
    {
        var cur = pid;
        for (var i = 0; i < maxSteps; i++)
        {
            if (!parents.TryGetValue(cur, out var parent) || parent == cur || parent == 0) return false;
            if (parent == self) return true;
            cur = parent;
        }
        return false;
    }

    /// <summary>True when the entry still needs Vireo's name or icon (so a labelled entry is left alone).</summary>
    internal static bool NeedsLabel(string? name, string? icon, string wantedIcon) =>
        !string.Equals(name, Name, StringComparison.Ordinal) || !string.Equals(icon, wantedIcon, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every running process and the one that started it.</summary>
    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e)) map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
        }
        finally { CloseHandle(snap); }
        return map;
    }

    // ---- The sound outputs and their entries ----

    /// <summary>Every active output, each with a listener for new sound entries; and one listener for outputs coming and going.</summary>
    private sealed class Outputs
    {
        private IMMDeviceEnumerator? _enumerator;
        private readonly Dictionary<string, (IAudioSessionManager2 Manager, NewEntryListener Listener)> _byId = [];
        private readonly OutputListener _outputListener = new();

        public void Listen()
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            Check(_enumerator.RegisterEndpointNotificationCallback(_outputListener));
        }

        /// <summary>Starts listening on outputs that are new since the last look.</summary>
        public void Refresh()
        {
            Check(_enumerator!.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out var devices));
            Check(devices.GetCount(out var count));
            for (var i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) != 0) continue;
                if (device.GetId(out var id) != 0 || _byId.ContainsKey(id)) continue;
                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var o) != 0 || o is not IAudioSessionManager2 manager) continue;
                // Windows sends new-entry events only after the entries have been listed once.
                if (manager.GetSessionEnumerator(out _) != 0) continue;
                var listener = new NewEntryListener();
                if (manager.RegisterSessionNotification(listener) != 0) continue;
                _byId[id] = (manager, listener);
            }
        }

        /// <summary>Labels every entry of ours on every output; returns how many it labelled.</summary>
        public int LabelOurs(string icon)
        {
            var parents = ParentMap();
            var self = Environment.ProcessId;
            var done = 0;
            foreach (var (manager, _) in _byId.Values)
            {
                if (manager.GetSessionEnumerator(out var entries) != 0) continue; // the output has gone
                if (entries.GetCount(out var count) != 0) continue;
                for (var i = 0; i < count; i++)
                {
                    if (entries.GetSession(i, out var entry) != 0) continue;
                    if (entry.GetProcessId(out var pid) != 0 || !IsOurs((int)pid, self, parents)) continue;
                    entry.GetDisplayName(out var name);
                    entry.GetIconPath(out var path);
                    if (!NeedsLabel(name, path, icon)) continue;
                    var context = Guid.Empty;
                    var named = entry.SetDisplayName(Name, ref context);
                    var pictured = entry.SetIconPath(icon, ref context);
                    if (named == 0 && pictured == 0) done++;
                }
            }
            return done;
        }

        private static void Check(int hr) { if (hr != 0) Marshal.ThrowExceptionForHR(hr); }
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class NewEntryListener : IAudioSessionNotification
    {
        // Windows asks that nothing slow happens here: the look itself runs on our thread.
        public int OnSessionCreated(IntPtr newSession) { s_wake.Set(); return 0; }
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class OutputListener : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string id, int state) { s_wake.Set(); return 0; }
        public int OnDeviceAdded(string id) { s_wake.Set(); return 0; }
        public int OnDeviceRemoved(string id) => 0;
        public int OnDefaultDeviceChanged(int flow, int role, string? id) { s_wake.Set(); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    // ---- Windows' sound interfaces (Core Audio) ----

    private const int DataFlowRender = 0;
    private const int DeviceStateActive = 1;
    private const int ClsctxAll = 23;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey { public Guid FormatId; public int PropertyId; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCo;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object face);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator entries);
        [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification listener);
        [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification listener);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 entry);
    }

    [ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated(IntPtr newSession);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr listener);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr listener);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    // ---- The process list ----

    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    // ---- For the self-test: what the mixer would read for our entries ----

    /// <summary>The name and icon of every sound entry of this Vireo's processes, and how many entries other programs have named "Vireo".</summary>
    internal static (List<(int Pid, string Name, string Icon)> Ours, int OthersNamedVireo) Survey()
    {
        var ours = new List<(int, string, string)>();
        var others = 0;
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        if (enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out var devices) != 0 || devices.GetCount(out var count) != 0) return (ours, 0);
        var parents = ParentMap();
        for (var i = 0; i < count; i++)
        {
            if (devices.Item(i, out var device) != 0) continue;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var o) != 0 || o is not IAudioSessionManager2 manager) continue;
            if (manager.GetSessionEnumerator(out var entries) != 0 || entries.GetCount(out var n) != 0) continue;
            for (var j = 0; j < n; j++)
            {
                if (entries.GetSession(j, out var entry) != 0 || entry.GetProcessId(out var pid) != 0) continue;
                entry.GetDisplayName(out var name);
                entry.GetIconPath(out var icon);
                if (IsOurs((int)pid, Environment.ProcessId, parents)) ours.Add(((int)pid, name ?? "", icon ?? ""));
                else if (name == Name) others++;
            }
        }
        return (ours, others);
    }
}
