using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vireo.Services;

namespace Vireo;

/// <summary>
/// Keeping Vireo current: a quiet check a minute after start and then daily, a background download, and the update installed
/// when Vireo closes (or at once with "Restart to update" in the menu, which brings the tabs back).
/// </summary>
public partial class MainWindow
{
    private static Updater? s_updater;
    private static DispatcherTimer? s_updateTimer;
    private static Mutex? s_updateLock;
    private static bool s_toldReady;

    /// <summary>The updater this process runs, if it is the one that does (one process per computer, whatever the profiles).</summary>
    internal static Updater? Updates => s_updater;

    private void StartUpdates()
    {
        if (SelfTesting && Environment.GetEnvironmentVariable("VIREO_UPDATE_FEED") is null) return;
        if (s_updater is not null) return;
        // Several profiles are several Vireos: only one of them checks and downloads, so they never fetch the same file twice.
        try
        {
            s_updateLock = new Mutex(initiallyOwned: true, @"Local\Vireo.Updater", out var mine);
            if (!mine) { s_updateLock.Dispose(); s_updateLock = null; return; }
        }
        catch { return; }

        s_updater = new Updater();
        s_updater.Changed += () => Dispatcher.BeginInvoke(ShowUpdateState);
        s_updateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(SelfTesting ? 1 : 60) };
        s_updateTimer.Tick += (_, _) =>
        {
            s_updateTimer!.Stop();
            _ = CheckAndScheduleAsync();
        };
        s_updateTimer.Start();
    }

    private async Task CheckAndScheduleAsync()
    {
        if (s_updater is null) return;
        try { await s_updater.RunAsync(download: _settings.AutoUpdate); } catch { }
        if (s_updateTimer is null) return;
        s_updateTimer.Interval = Updater.NextCheckIn(s_updater.FailuresInARow);
        s_updateTimer.Start();
    }

    /// <summary>"Check now" in Settings, and turning automatic updates back on.</summary>
    internal void CheckForUpdatesNow()
    {
        if (s_updater is null) { StartUpdates(); if (s_updater is null) return; }
        s_updateTimer?.Stop();
        _ = CheckAndScheduleAsync();
    }

    /// <summary>Puts the update's state on every window: the dot on the menu button and the menu's "Restart to update".</summary>
    private void ShowUpdateState()
    {
        var ready = s_updater is { State: UpdateState.Ready, Latest: { } latest } ? latest : null;
        foreach (var w in s_windows.ToList())
        {
            w.UpdateDot.Visibility = ready is null ? Visibility.Collapsed : Visibility.Visible;
            w.UpdateMenuItem.Visibility = ready is null ? Visibility.Collapsed : Visibility.Visible;
            if (ready is not null) w.UpdateMenuText.Text = $"Restart to update to {ready.Label}";
            w._updatesStatus?.Invoke();
        }
        if (ready is not null && !s_toldReady)
        {
            s_toldReady = true;
            (LastActive ?? this).ShowToast($"Vireo {ready.Label} is ready", "It installs when you close Vireo, or now from the menu.", () => (LastActive ?? this).ShowMenu());
        }
    }

    private void OnMenuUpdate(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        RestartToUpdate();
    }

    /// <summary>Saves the session, hands over to the installer (which reopens Vireo with the same tabs), and closes.</summary>
    internal void RestartToUpdate()
    {
        if (s_updater is null) return;
        SaveAnySession();
        if (!s_updater.Install(reopen: true, App.ProfileId))
        {
            ShowToast("The update could not start", "It will be tried again when Vireo closes.", isError: true);
            return;
        }
        s_installing = true;
        Application.Current.Shutdown();
    }

    private static bool s_installing;

    /// <summary>
    /// Vireo is closing: a downloaded update installs now, quietly, ready for next time. Not while another Vireo (another profile)
    /// is still open, since installing would close it; the update then waits for the next time.
    /// </summary>
    internal static void InstallUpdateOnExit()
    {
        if (s_installing || s_updater is not { State: UpdateState.Ready } u || s_settings is not { AutoUpdate: true }) return;
        try
        {
            var me = Environment.ProcessId;
            var exe = Environment.ProcessPath;
            var others = Process.GetProcessesByName("Vireo").Count(p => p.Id != me && SameExe(p, exe));
            if (others > 0) return;
        }
        catch { return; }
        s_installing = u.Install(reopen: false);
    }

    private static bool SameExe(Process p, string? exe)
    {
        try { return exe is null || string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); }
        catch { return true; } // cannot tell: assume it is ours, and do not install under it
    }

    // ---- Settings: the "Updates" row ----

    private Action? _updatesStatus;

    private UIElement UpdatesRow()
    {
        var status = UiKit.Muted("");
        var button = new Button { Style = (Style)FindResource("SecondaryButton"), Content = "Check now", Margin = new Thickness(10, 0, 0, 0) };
        void Show()
        {
            var u = s_updater;
            (string text, string action) = u?.State switch
            {
                null => ("Checked automatically", "Check now"),
                UpdateState.Idle => ("Checked automatically", "Check now"),
                UpdateState.Checking => ("Checking…", "Check now"),
                UpdateState.UpToDate => ($"Up to date{(u.LastChecked is { } t ? $" · checked {t:HH:mm}" : "")}", "Check now"),
                UpdateState.Available => ($"Vireo {u.Latest?.Label} is available{(u.Error is { } e ? $" · {e}" : "")}", "Download"),
                UpdateState.Downloading => ($"Downloading {u.Latest?.Label}… {u.Progress}%", "Check now"),
                UpdateState.Ready => ($"Vireo {u.Latest?.Label} is ready", "Restart to update"),
                _ => (u.Error ?? "Could not check", "Try again"),
            };
            status.Text = text;
            button.Content = action;
            button.IsEnabled = u?.State is not (UpdateState.Checking or UpdateState.Downloading);
        }
        button.Click += (_, _) =>
        {
            if (s_updater is { State: UpdateState.Ready }) { RestartToUpdate(); return; }
            if (s_updater is { State: UpdateState.Available })
            {
                _ = s_updater.RunAsync(download: true);
                return;
            }
            CheckForUpdatesNow();
        };
        _updatesStatus = Show;
        Show();
        // Laid out like every other row: the words on the left (the state under the label, wrapping as needed, and a small
        // "Release notes" link under it), one button on the right. Three things on one line ran off a narrow panel.
        status.FontSize = 12;
        status.TextWrapping = TextWrapping.Wrap;
        status.Margin = new Thickness(0, 2, 0, 0);
        var notes = new TextBlock { Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Release notes")) { Foreground = UiKit.Brush("Accent"), TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
        link.Click += (_, _) => (s_updater ?? new Updater()).OpenReleasePage();
        notes.Inlines.Add(link);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), MinWidth = 150 };
        words.Children.Add(new TextBlock { Text = "Updates", FontSize = 13 });
        words.Children.Add(status);
        words.Children.Add(notes);
        var row = new Grid { Margin = new Thickness(14, 11, 12, 11) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(words);
        button.Margin = new Thickness(0);
        button.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }

    /// <summary>For tests: the menu's update item is showing.</summary>
    internal bool UpdateOffered => UpdateMenuItem.Visibility == Visibility.Visible && UpdateDot.Visibility == Visibility.Visible;
}
