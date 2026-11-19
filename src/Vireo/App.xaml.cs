using System.IO;
using System.Windows;
using System.Windows.Threading;
using Vireo.Services;

namespace Vireo;

public partial class App : Application
{
    private readonly Queue<DateTime> _recent = new();

    /// <summary>How many problems have been written to the log this run (the self-test expects none).</summary>
    internal static int Errors;

    /// <summary>Where problems are written down, so a crash leaves something to look at.</summary>
    /// A profile kept elsewhere (VIREO_DATA: tests and developers) keeps its own log, so a test run never writes into the
    /// person's real one.
    public static string CrashLogPath =>
        Environment.GetEnvironmentVariable("VIREO_DATA") is { Length: > 0 } data
            ? Path.Combine(Path.GetFullPath(data), "crash.log")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vireo", "crash.log");

    /// <summary>The profile this Vireo runs ("--profile work"); empty for the default one.</summary>
    internal static string ProfileId { get; private set; } = ProfileFrom(Environment.GetCommandLineArgs());

    /// <summary>The id after "--profile", if it is a valid one.</summary>
    internal static string ProfileFrom(string[] args)
    {
        var i = Array.IndexOf(args, "--profile");
        return i >= 0 && i + 1 < args.Length && Profiles.ValidId(args[i + 1]) ? args[i + 1] : "";
    }

    /// <summary>The folder the person's data lives in (the same rule the window uses).</summary>
    internal static string ProfileFolder() =>
        ProfileId.Length > 0 ? Profiles.FolderOf(ProfileId) : Environment.GetEnvironmentVariable("VIREO_DATA") ?? Profiles.FolderOf("");

    internal static SingleInstance? Instance;

    /// <summary>True for the value that follows an option taking one ("--profile Work"), so it is not taken for a link.</summary>
    internal static bool IsOptionValue(string arg)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, arg);
        return i > 0 && args[i - 1] is "--profile";
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // "Vireo.exe --register" lists Vireo among the browsers Windows can use, then quits.
        if (e.Args.Contains("--register")) { DefaultBrowser.Register(Environment.ProcessPath!); Shutdown(0); return; }
        // Uninstalling: out of the list of browsers, and the downloader no longer starts with Windows.
        if (e.Args.Contains("--unregister")) { DefaultBrowser.Unregister(); AutoStart.RemoveAll(); Shutdown(0); return; }

        // Only one Vireo per profile: a second launch hands its link to the first and leaves.
        var instance = new SingleInstance(ProfileFolder());
        if (!instance.IsFirst)
        {
            // Windows starting the downloader at sign-in while Vireo already runs: nothing to do.
            if (e.Args.Contains("--downloads") && e.Args.Contains("--quiet")) { instance.Dispose(); Shutdown(0); return; }
            // "--downloads" (the Start menu's Vireo Downloads) brings the running Vireo's downloads forward.
            var link = e.Args.Contains("--downloads") ? DownloadsRequest : e.Args.FirstOrDefault(a => !a.StartsWith("--") && !IsOptionValue(a)) ?? "";
            if (instance.Send(link)) { instance.Dispose(); Shutdown(0); return; }
        }
        else
        {
            Instance = instance;
            instance.Received += link => Dispatcher.BeginInvoke(() =>
            {
                if (link == DownloadsRequest) { Vireo.MainWindow.ShowDownloadsFromOutside(); return; }
                // Only the downloader is running (the browser was closed with downloads going): the browser comes back.
                if (Vireo.MainWindow.InBackground) Vireo.MainWindow.OpenBrowserFromBackground(link);
                else (Vireo.MainWindow.LastActive ?? MainWindow as MainWindow)?.OpenFromOutside(link);
            });
        }
        // A steady 30 frames a second on machines that draw without a graphics card (see Motion).
        Motion.TuneForThisMachine();
        // "When downloads finish: shut down" warns for a minute first, with Cancel in front.
        WhenDone.Warn = what => new Vireo.Controls.WhenDoneCountdown(what, WhenDone.Warning).Run();
        base.OnStartup(e);
        ForgetOldPrivateDownloads();
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("Unhandled (fatal)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log("Background task", args.Exception); args.SetObserved(); };

        // "Vireo.exe --downloads": only the downloader, in the notification area; no browser window and no page engine.
        if (e.Args.Contains("--downloads") && !e.Args.Contains("--selftest")) Vireo.MainWindow.StartDownloaderOnly(quiet: e.Args.Contains("--quiet"));
        else new Vireo.MainWindow().Show();
    }

    /// <summary>What a second start sends to ask for the downloads (not an address: it cannot be mistaken for one).</summary>
    internal const string DownloadsRequest = "vireo:downloads";

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is shutting down or signing out: no question, no background; downloads are kept for next time.
        Vireo.MainWindow.EndingSession = true;
        if (Vireo.MainWindow.InBackground) Vireo.MainWindow.QuitFromBackground();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // A downloaded update installs as Vireo closes, ready for the next start.
        Vireo.MainWindow.InstallUpdateOnExit();
        base.OnExit(e);
    }

    /// <summary>
    /// Earlier versions kept a private window's download list in a temporary folder and could leave it behind (after a crash,
    /// or always): page addresses and sign-in cookies. Private windows now keep nothing on disk; whatever an older run left goes.
    /// </summary>
    internal static void ForgetOldPrivateDownloads()
    {
        try
        {
            var old = Path.Combine(Path.GetTempPath(), "VireoPrivate");
            if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
        }
        catch { /* in use or locked: tried again next start */ }
    }

    /// <summary>
    /// A mistake while drawing or handling a click should not take the browser and every open tab with it: it is written to the
    /// log and the app carries on. If the same thing keeps happening (a loop), it stops with a message instead of spinning.
    /// </summary>
    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Window", e.Exception);
        var now = DateTime.UtcNow;
        _recent.Enqueue(now);
        while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromSeconds(10)) _recent.Dequeue();
        if (_recent.Count > 5)
        {
            MessageBox.Show($"Vireo keeps hitting the same problem and has to close.\n\n{e.Exception.Message}\n\nDetails are in:\n{CrashLogPath}", "Vireo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
        e.Handled = true;
    }

    internal static void Log(string where, Exception? error)
    {
        Interlocked.Increment(ref Errors);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {error?.GetType().Name}: {error?.Message}\n{error}\n\n");
        }
        catch { /* nowhere to write; nothing more to do */ }
    }
}
