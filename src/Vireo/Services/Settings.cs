using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vireo.Services;

public enum TabSide { Top, Bottom, Left, Right }

public enum TrackingLevel { Off, Basic, Balanced, Strict }

/// <summary>Everything the person can change. Saved to settings.json in the profile folder.</summary>
public sealed record PinnedSite(string Title, string Url);

public sealed class Settings
{
    // Tabs
    public TabSide TabSide { get; set; } = TabSide.Top;
    public bool VerticalCollapsed { get; set; }

    // Memory
    public bool SleepHiddenTabs { get; set; } = true;
    /// <summary>Zero means automatic: faster on computers with little memory.</summary>
    public int SleepAfterSeconds { get; set; }
    public int FreeAfterMinutes { get; set; }

    // Blocking and privacy
    public bool BlockAds { get; set; } = true;
    public bool BlockPopups { get; set; } = true;
    /// <summary>Also load EasyList and EasyPrivacy (downloaded and refreshed in the background).</summary>
    public bool UseCommunityLists { get; set; } = true;
    public DateTime? ListsUpdatedAt { get; set; }
    public List<string> AdAllowSites { get; set; } = [];
    public TrackingLevel Tracking { get; set; } = TrackingLevel.Balanced;
    public bool HttpsOnly { get; set; }
    public bool StripTrackingParams { get; set; } = true;
    public bool BlockWebRtcLeak { get; set; }
    public bool ClearDataOnExit { get; set; }

    // Downloads
    public int DownloadConnections { get; set; } = 8;
    public int MaxActiveDownloads { get; set; } = 3;
    /// <summary>Kilobytes per second shared by all downloads; 0 means no limit.</summary>
    public int SpeedLimitKBps { get; set; }
    public string DownloadFolder { get; set; } = "";
    /// <summary>Where a private window's downloads go: "" until asked (at the first private download), "same" for
    /// <see cref="DownloadFolder"/>, or a folder of its own. Only the place is kept, never what was saved there.</summary>
    public string PrivateDownloadFolder { get; set; } = "";
    /// <summary>Download new versions in the background and install them when Vireo closes.</summary>
    public bool AutoUpdate { get; set; } = true;

    // Buttons drawn on videos and audio in pages
    public bool ShowDownloadButtons { get; set; } = true;
    /// <summary>"hover" shows the button when you move over a video; "always" keeps it visible.</summary>
    public string DownloadButtonMode { get; set; } = "hover";
    public List<string> NoButtonSites { get; set; } = [];
    /// <summary>Sites allowed to start several downloads in a row by themselves.</summary>
    public List<string> AutoDownloadSites { get; set; } = [];

    // Start page
    /// <summary>Sites the person put on the start page themselves; they come first, in this order.</summary>
    public List<PinnedSite> PinnedSites { get; set; } = [];
    /// <summary>Often-visited sites the person took off the start page.</summary>
    public List<string> HiddenSites { get; set; } = [];
    /// <summary>When the pinned sites last changed (UTC), so sync knows which computer's list is newest.</summary>
    public DateTime? PinnedSitesChangedAt { get; set; }

    // Sync
    /// <summary>The "Vireo Sync" folder inside a cloud-synced folder; null when sync is off.</summary>
    public string? SyncFolder { get; set; }
    /// <summary>This computer's name for its file in the sync folder (random, says nothing about the computer).</summary>
    public string? SyncDeviceId { get; set; }
    public string? SyncDeviceName { get; set; }

    // Search
    /// <summary>Which version of the file this is; lets an older default be replaced once.</summary>
    public int Version { get; set; }
    public string SearchTemplate { get; set; } = UrlInput.DefaultSearch;
    /// <summary>Ask the search engine for ideas while typing. This sends what you type in the address bar to that engine.</summary>
    public bool SearchSuggestions { get; set; } = true;

    // Starting and remembering
    /// <summary>Open the tabs of the last visit when Vireo starts.</summary>
    public bool RestoreTabs { get; set; } = true;
    /// <summary>Let the engine's password manager offer to save passwords and fill them in.</summary>
    public bool SavePasswords { get; set; } = true;
    /// <summary>Fill in names, addresses and similar form fields.</summary>
    public bool FillForms { get; set; } = true;

    // Pages
    /// <summary>Zoom chosen for a site (1.0 is not stored). Remembered like other browsers do, per site.</summary>
    public Dictionary<string, double> SiteZoom { get; set; } = [];
    public const int MaxSiteZooms = 300;

    [JsonIgnore] public string? Path { get; private set; }

    /// <summary>Stop saving to disk. A private window uses the settings but leaves no trace.</summary>
    public void Detach() => Path = null;

    public event Action? Changed;

    public static Settings Load(string folder)
    {
        var path = System.IO.Path.Combine(folder, "settings.json");
        Settings s;
        try { s = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch { s = new(); } // a damaged file must never stop the browser from starting
        s.Path = path;
        if (s.Version < 2)
        {
            // Before version 2 the saved engine was only ever the old default (the setting did nothing), so move to the new default.
            s.SearchTemplate = UrlInput.DefaultSearch;
            s.Version = 2;
        }
        if (!s.SearchTemplate.Contains("{0}")) s.SearchTemplate = UrlInput.DefaultSearch;
        if (string.IsNullOrWhiteSpace(s.DownloadFolder))
            s.DownloadFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        s.DownloadConnections = Math.Clamp(s.DownloadConnections, 1, 32);
        s.MaxActiveDownloads = Math.Clamp(s.MaxActiveDownloads, 1, 10);
        return s;
    }

    /// <summary>Tells everything that listens, and saves.</summary>
    public void Commit()
    {
        Changed?.Invoke();
        if (Path is null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, Path, overwrite: true);
        }
        catch { /* settings are a convenience; never crash over them */ }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
}
