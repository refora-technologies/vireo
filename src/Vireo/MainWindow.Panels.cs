using System.Windows;
using System.Windows.Controls;
using Vireo.Services;

namespace Vireo;

// The panels that open in the drawer.
public partial class MainWindow
{
    private SuspensionSettings? _suspensionOverride;

    /// <summary>How long a hidden tab waits before it sleeps and before it is freed. Zero in settings means automatic.</summary>
    public SuspensionSettings Suspension
    {
        get => _suspensionOverride ?? FromSettings();
        set => _suspensionOverride = value;
    }

    private SuspensionSettings FromSettings()
    {
        var auto = SuspensionPolicy.ForMemory(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        var sleep = _settings.SleepAfterSeconds > 0 ? TimeSpan.FromSeconds(_settings.SleepAfterSeconds) : auto.SuspendAfter;
        var free = _settings.FreeAfterMinutes > 0 ? TimeSpan.FromMinutes(_settings.FreeAfterMinutes) : auto.DiscardAfter;
        return new SuspensionSettings(sleep, free, _settings.SleepHiddenTabs);
    }

    private Button PanelButton(string text, Action open)
    {
        var b = new Button { Style = (Style)FindResource("SecondaryButton"), Content = text };
        b.Click += (_, _) => open();
        return b;
    }

    private Button ExtensionsButton()
    {
        var b = new Button { Style = (Style)FindResource("SecondaryButton"), Content = "Manage…", Height = 30, Padding = new Thickness(14, 0, 14, 0), FontSize = 12 };
        b.Click += (_, _) => ShowExtensions();
        return b;
    }

    internal void ShowSettings() => OpenDrawer("Settings", BuildSettings());

    /// <summary>Where "start with Windows" is written: Windows' Run key, or a key of their own for tests.</summary>
    internal static string AutoStartKey = AutoStart.DefaultRunKey;

    private bool SetAutoStart(bool on) => Environment.ProcessPath is { } exe && AutoStart.Set(on, exe, App.ProfileId, AutoStartKey);

    /// <summary>
    /// "Use Vireo for downloads" from the welcome page: the downloader stays in the notification area, starts with Windows,
    /// and offers copied links; off undoes all three.
    /// </summary>
    internal void UseAsDownloadManager(bool on)
    {
        _settings.DownloadManager = on;
        _settings.WatchClipboard = on;
        _settings.Commit();
        SetAutoStart(on);
        ApplyDownloadManagerChoices();
    }

    private UIElement BuildSettings()
    {
        void Save() => _settings.Commit();
        var s = _settings;

        var tabs = UiKit.Card(
            UiKit.Block("Where the tabs go", "Top and bottom put them in a row. Left and right make a list, which can fold down to icons.",
                UiKit.Segmented<TabSide>([("Top", TabSide.Top), ("Bottom", TabSide.Bottom), ("Left", TabSide.Left), ("Right", TabSide.Right)], s.TabSide, SetTabSide)),
            UiKit.Row("Continue where you left off", "Opens the tabs from your last visit when Vireo starts. Only the one you were on loads at once; the others load when you click them.", UiKit.Switch(s.RestoreTabs, on => { s.RestoreTabs = on; Save(); if (!on) SessionStore.Delete(SessionPath); })),
            UiKit.Row("Fold the side list to icons", "Only when tabs are on the left or right.", UiKit.Switch(s.VerticalCollapsed, on =>
            {
                if (s.VerticalCollapsed != on) OnCollapseTabs(this, new RoutedEventArgs());
            })));

        var memory = UiKit.Card(
            UiKit.Row("Put hidden tabs to sleep", "A sleeping tab uses no processor time and wakes at once.", UiKit.Switch(s.SleepHiddenTabs, on => { s.SleepHiddenTabs = on; Save(); })),
            UiKit.Row("Sleep after", "Automatic picks 30 seconds, or 10 on a low-memory computer.",
                UiKit.Stepper(s.SleepAfterSeconds, 0, 600, 10, v => v == 0 ? "Automatic" : $"{v} seconds", v => { s.SleepAfterSeconds = v; Save(); })),
            UiKit.Row("Free completely after", "Frees the page from memory. Its title and address are kept.",
                UiKit.Stepper(s.FreeAfterMinutes, 0, 120, 1, v => v == 0 ? "Automatic" : $"{v} minutes", v => { s.FreeAfterMinutes = v; Save(); })));

        var search = UiKit.Card(
            UiKit.Block("Search engine", null,
                UiKit.Segmented(SearchEngines.All.Select(e => (e.Name, e.Template)).ToList(), s.SearchTemplate, t => { s.SearchTemplate = t; Save(); })),
            UiKit.Row("Suggest searches as you type", "Asks your search engine for ideas, which tells it what you type in the address bar. Not used in private windows.", UiKit.Switch(s.SearchSuggestions, on => { s.SearchSuggestions = on; Save(); })));

        var status = new TextBlock { FontSize = 12, Foreground = UiKit.Brush("Text3"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        void ShowDefaultStatus() => status.Text = DefaultBrowser.IsDefault() ? "Vireo is your default browser" : "Not your default browser yet";
        ShowDefaultStatus();
        var makeDefault = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "Make default…", Height = 30, Padding = new Thickness(14, 0, 14, 0), FontSize = 12 };
        makeDefault.Click += (_, _) =>
        {
            try { DefaultBrowser.Register(Environment.ProcessPath!); } catch { /* the registry is locked down: Settings may still list it */ }
            DefaultBrowser.OpenSettings();
        };
        var defaultRow = new StackPanel { Orientation = Orientation.Horizontal };
        defaultRow.Children.Add(status);
        defaultRow.Children.Add(makeDefault);
        var import = new Button { Style = (Style)FindResource("SecondaryButton"), Content = "Import…", Height = 30, Padding = new Thickness(14, 0, 14, 0), FontSize = 12 };
        import.Click += (_, _) => ShowImport();
        var yours = UiKit.Card(
            UiKit.Block("Default browser", "Web links and pages from other programs open in Vireo. Windows asks you to confirm.", defaultRow),
            UiKit.Row("Bring your data here", "Bookmarks and history from Chrome, Edge, Firefox and others.", import),
            UiKit.Row("Extensions", "Add features from an unpacked extension folder or file.", ExtensionsButton()),
            UiKit.Row("Sync", "Bookmarks, history, start-page sites and open tabs on all your computers, through your own cloud folder.", PanelButton(s_sync?.IsOn == true ? "Manage…" : "Set up…", ShowSync)),
            UiKit.Row("Profiles", "Separate browsers for work and personal: their own sign-ins, history and settings.", PanelButton("Manage…", ShowProfiles)));

        var about = UiKit.Card(
            UiKit.Row("Vireo", "A small, quick browser built on the WebView2 engine, by Refora Technologies.", UiKit.Number(Services.Updater.Label(Services.Updater.Current))),
            UpdatesRow(),
            UiKit.Row("Install updates automatically", "New versions download in the background and install when you close Vireo. Never asks for administrator rights.", UiKit.Switch(s.AutoUpdate, on => { s.AutoUpdate = on; Save(); if (on) CheckForUpdatesNow(); })),
            UiKit.Row("Your data", _dataFolder, UiKit.Muted("")));

        var privacy = UiKit.Card(
            UiKit.Block("Tracking prevention", "The browser's own list of trackers. Strict stops the most but can break a few sites.",
                UiKit.Segmented<TrackingLevel>([("Off", TrackingLevel.Off), ("Basic", TrackingLevel.Basic), ("Balanced", TrackingLevel.Balanced), ("Strict", TrackingLevel.Strict)], s.Tracking, v => { s.Tracking = v; Save(); })),
            UiKit.Row("Clean links", "Removes utm_, fbclid, gclid and similar tags that follow you between sites.", UiKit.Switch(s.StripTrackingParams, on => { s.StripTrackingParams = on; Save(); })),
            UiKit.Row("Prefer secure connections", "Opens https:// instead of http://. If a site has no secure version you are asked first.", UiKit.Switch(s.HttpsOnly, on => { s.HttpsOnly = on; Save(); })),
            UiKit.Row("Hide your network address from calls", "Stops video-call technology from revealing your local address. Applies after a restart.", UiKit.Switch(s.BlockWebRtcLeak, on => { s.BlockWebRtcLeak = on; Save(); })),
            UiKit.Row("Offer to save passwords", "The browser engine keeps them (the same password manager Microsoft Edge uses). Never in private windows.", UiKit.Switch(s.SavePasswords, on => { s.SavePasswords = on; Save(); })),
            UiKit.Row("Fill in forms", "Names, addresses and similar fields.", UiKit.Switch(s.FillForms, on => { s.FillForms = on; Save(); })),
            UiKit.Block("Clear browsing data now", "Removes it from this computer at once.", ClearButtons()),
            UiKit.Row("Forget everything when closing", "Clears history, cookies and site data each time Vireo closes.", UiKit.Switch(s.ClearDataOnExit, on => { s.ClearDataOnExit = on; Save(); })));

        static string Speed(int kb) => kb == 0 ? "Unlimited" : kb < 1024 ? $"{kb} KB/s" : $"{kb / 1024.0:0.#} MB/s";
        var folder = new TextBlock { Text = s.DownloadFolder, FontSize = 11.5, FontFamily = UiKit.Mono, Foreground = UiKit.Brush("Text3"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230, VerticalAlignment = VerticalAlignment.Center, ToolTip = s.DownloadFolder };
        var change = new Button { Style = (Style)FindResource("SecondaryButton"), Content = "Change…", Margin = new Thickness(10, 0, 0, 0) };
        change.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = s.DownloadFolder, Title = "Where should downloads go?" };
            if (dialog.ShowDialog(this) != true) return;
            s.DownloadFolder = dialog.FolderName;
            folder.Text = s.DownloadFolder;
            Save();
        };
        var where = new StackPanel { Orientation = Orientation.Horizontal };
        where.Children.Add(folder);
        where.Children.Add(change);

        string PrivateText() => s.PrivateDownloadFolder switch
        {
            "" => "Asked at the first private download",
            Controls.PrivateFolderDialog.SameAsNormal => "Same as normal downloads",
            var p => p,
        };
        var privateFolder = new TextBlock { Text = PrivateText(), FontSize = 11.5, FontFamily = UiKit.Mono, Foreground = UiKit.Brush("Text3"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230, VerticalAlignment = VerticalAlignment.Center, ToolTip = PrivateText() };
        var privateChange = new Button { Style = (Style)FindResource("SecondaryButton"), Content = "Change…", Margin = new Thickness(10, 0, 0, 0) };
        privateChange.Click += (_, _) =>
        {
            var ask = new Controls.PrivateFolderDialog(this, s.DownloadFolder, s.PrivateDownloadFolder);
            ask.ShowDialog();
            if (ask.Choice is null) return;
            s.PrivateDownloadFolder = ask.Choice;
            privateFolder.Text = PrivateText();
            privateFolder.ToolTip = PrivateText();
            Save();
        };
        var privateWhere = new StackPanel { Orientation = Orientation.Horizontal };
        privateWhere.Children.Add(privateFolder);
        privateWhere.Children.Add(privateChange);

        var downloads = UiKit.Card(
            UiKit.Row("Download buttons on videos and audio", "A button appears on the video or song a page is playing, with its qualities.", UiKit.Switch(s.ShowDownloadButtons, on => { s.ShowDownloadButtons = on; Save(); })),
            UiKit.Block("When to show them", null, UiKit.Segmented<string>([("On hover", "hover"), ("Always", "always")], s.DownloadButtonMode, v => { s.DownloadButtonMode = v; Save(); })),
            UiKit.Row("Connections per download", "More connections finish big files sooner. Some servers allow only a few.", UiKit.Stepper(s.DownloadConnections, 1, 16, 1, v => v.ToString(), v => { s.DownloadConnections = v; Save(); })),
            UiKit.Row("Downloads at once", null, UiKit.Stepper(s.MaxActiveDownloads, 1, 8, 1, v => v.ToString(), v => { s.MaxActiveDownloads = v; Save(); })),
            UiKit.Row("Speed limit", "Shared by all downloads, so the rest of your connection stays free.", UiKit.Stepper(s.SpeedLimitKBps, 0, 102400, 256, Speed, v => { s.SpeedLimitKBps = v; Save(); })),
            UiKit.Block("Closing Vireo while downloads run", "Keep downloading: the browser closes and only the downloader stays, in the notification area, until they are done.",
                UiKit.Segmented<string>([("Ask", "ask"), ("Keep downloading", "background"), ("Stop them", "stop")], s.CloseWithDownloads, v => { s.CloseWithDownloads = v; Save(); })),
            UiKit.Block("Save to", null, where),
            UiKit.Block("Private window downloads", "Asked once, at the first download in a private window. Vireo keeps no record of what was saved.", privateWhere));

        var manager = UiKit.Card(
            UiKit.Row("Use Vireo as your download manager", "The downloader keeps its icon in the notification area, by the clock, also when the browser is closed (it uses about 35 MB), and tells you when each download is finished.",
                UiKit.Switch(s.DownloadManager, on => { s.DownloadManager = on; Save(); ApplyDownloadManagerChoices(); })),
            UiKit.Row("Start the downloader with Windows", "Only the downloader starts, quietly, in the notification area. Windows' Startup apps list shows it as Vireo Downloads.",
                UiKit.Switch(AutoStart.IsOn(App.ProfileId, AutoStartKey), on => SetAutoStart(on))),
            UiKit.Row("Offer links you copy", "A link to a file copied in another program brings a \"Download this?\" notice by the clock. What you copy is looked at on this computer only and never kept.",
                UiKit.Switch(s.WatchClipboard, on => { s.WatchClipboard = on; Save(); ApplyDownloadManagerChoices(); })),
            UiKit.Row("Sort downloads into folders", "Video, Music, Pictures, Documents, Programs and Compressed, inside the download folder. A folder you pick yourself is left as it is.",
                UiKit.Switch(s.SortByType, on => { s.SortByType = on; Save(); })));

        return UiKit.Page(UiKit.Heading("Tabs"), tabs, UiKit.Heading("Privacy"), privacy, UiKit.Heading("Downloads"), downloads, UiKit.Heading("Download manager"), manager, UiKit.Heading("Memory"), memory, UiKit.Heading("Search"), search, UiKit.Heading("Make it yours"), yours, UiKit.Heading("About"), about);
    }

}
