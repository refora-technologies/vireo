using System.Windows;
using Vireo.Services;

namespace Vireo;

// Sync between computers, through a folder the person's own cloud service keeps in step.
public partial class MainWindow
{
    private static SyncService? s_sync;
    internal static SyncService? Sync => s_sync;

    private void StartSync()
    {
        if (_private || s_sync is not null) return;
        s_sync = new SyncService(_dataFolder, _settings, _library, OpenTabsForSync, work => Dispatcher.Invoke(work));
        s_sync.Start();
        Closed += (_, _) => { if (s_windows.Count == 0) { s_sync?.Dispose(); s_sync = null; } };
    }

    /// <summary>The real pages open in every window, for "tabs on your other computers".</summary>
    private static List<SyncTab> OpenTabsForSync() =>
        [.. s_windows.SelectMany(w => w._tabs).Where(t => !t.IsStartPage && SessionStore.Restorable(t.Url)).Take(60).Select(t => new SyncTab(t.Url, t.DisplayTitle))];

    private void OnMenuSync(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; ToggleDrawer("Sync", ShowSync); }

    internal void ShowSync()
    {
        if (s_sync is null && !_private) StartSync();
        OpenDrawer("Sync", new Controls.SyncPanel(this, s_sync));
    }

    internal void OpenLink(string url) => _ = OpenTabAsync(url);
}
