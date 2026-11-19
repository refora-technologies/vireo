using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Vireo.Media;
using Vireo.Services;
using Microsoft.Web.WebView2.Wpf;

namespace Vireo.Models;

/// <summary>One tab: what the person sees, plus the page behind it while it is alive.</summary>
public sealed class BrowserTab : INotifyPropertyChanged
{
    private string _title = "";
    private string _url = "";
    private ImageSource? _favicon;
    private bool _isLoading;
    private bool _isPlayingAudio;
    private TabLife _life = TabLife.Background;
    private bool _canGoBack;
    private bool _canGoForward;

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The page. Null once the tab has been freed to save memory.</summary>
    public WebView2? View { get; set; }

    /// <summary>When the tab was last shown; used to decide how long it has been hidden.</summary>
    public DateTime? HiddenSince { get; set; } = DateTime.UtcNow;

    /// <summary>The browser's own start page: no address to show and nothing to restore.</summary>
    public bool IsStartPage { get; set; }

    public bool IsPinned { get; set; }

    /// <summary>The plain-http address we swapped for https, in case the secure one cannot be reached.</summary>
    public string? UpgradedFrom { get; set; }

    // Videos and audio found on the current page.
    /// <summary>The tab was opened by a page (a pop-up or a link opening a new tab), so it loads by itself and the page, not the address bar, gets the keyboard.</summary>
    public bool OpenedByPage { get; set; }
    /// <summary>When this tab's page process last crashed (a second crash soon after is not reloaded automatically).</summary>
    public DateTime LastCrash { get; set; } = DateTime.MinValue;
    // What the page was stopped from doing (shown on the shield, counted by the self-test)
    public DateTime DialogWindowStart { get; set; } = DateTime.MinValue;
    public int DialogCount { get; set; }
    public int DialogsSuppressed { get; set; }
    public int NotificationsRefused { get; set; }
    public List<DetectedMedia> Media { get; } = [];
    /// <summary>Addresses of the pieces of streams already listed: a player loading them is not a new video.</summary>
    public HashSet<string> ChildKeys { get; } = [];
    /// <summary>Addresses of frames inside the page: one of them may be the page a stream really belongs to (kept to a few dozen).</summary>
    public HashSet<string> FrameUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public const int MaxFrameUrls = 40;
    public const int MaxRememberedReferers = 200;

    public void AddFrameUrl(string url)
    {
        lock (FrameUrls) { if (FrameUrls.Count < MaxFrameUrls) FrameUrls.Add(url); }
    }

    /// <summary>Remembers who asked for an address, but only for addresses that look like media: a page makes hundreds of other requests.</summary>
    public void RememberReferer(string uri, string referer)
    {
        if (!Vireo.Media.MediaClassifier.LooksLikeMediaUrl(uri)) return;
        if (RequestReferers.Count >= MaxRememberedReferers) RequestReferers.Clear();
        RequestReferers[uri] = referer;
    }
    public List<Microsoft.Web.WebView2.Core.CoreWebView2Frame> Frames { get; } = [];
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> RequestReferers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public event Action? MediaChanged;
    public void RaiseMediaChanged() => MediaChanged?.Invoke();
    /// <summary>How many answers on this page have been looked inside (for playlists without a telling address); capped so a busy page costs little.</summary>
    public int Sniffs;
    public const int MaxSniffs = 250;

    /// <summary>Addresses the page's own &lt;video&gt;/&lt;audio&gt; elements asked for: whatever their answer says about itself, they are media.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> MediaElementRequests { get; } = new(StringComparer.Ordinal);

    /// <summary>The page's preview picture (og:image), shown for a found video that has no poster of its own.</summary>
    public string? PageImage { get; set; }

    /// <summary>The window the tab is in now (a tab can be dragged to another window).</summary>
    public MainWindow? Owner { get; set; }

    public void ClearMedia()
    {
        Sniffs = 0;
        MediaElementRequests.Clear();
        PageImage = null;
        lock (FrameUrls) FrameUrls.Clear();
        lock (Frames) Frames.Clear();
        RequestReferers.Clear();
        if (Media.Count == 0 && ChildKeys.Count == 0) return;
        Media.Clear();
        ChildKeys.Clear();
        MediaChanged?.Invoke();
    }

    // What blocking stopped on the current page.
    /// <summary>Whether this tab's page engine has the YouTube ads script (null: not known yet), and its id to remove it.</summary>
    internal bool? YouTubeAdsWanted;
    internal string? YouTubeAdsScript;

    public int Blocked { get; private set; }
    public int BlockedAds { get; private set; }
    public int BlockedTrackers { get; private set; }
    public Dictionary<string, int> BlockedHosts { get; } = [];

    /// <summary>Everything stopped since Vireo started, in all tabs (shown on the start page).</summary>
    public static long TotalAds, TotalTrackers;

    private readonly HashSet<string> _blockedUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _blockedTimes = new(StringComparer.Ordinal);

    /// <summary>How many times this address (without its query) has now been blocked on this page, counting this time.</summary>
    public int BlockedAgain(string url)
    {
        var q = url.IndexOf('?');
        var key = q > 0 ? url[..q] : url;
        var n = _blockedTimes.GetValueOrDefault(key) + 1;
        if (n > 1 || _blockedTimes.Count < 20_000) _blockedTimes[key] = n;
        return n;
    }

    /// <summary>
    /// Counts a blocked request once per address (without its query): an ad script that retries a blocked request in a loop,
    /// often with a new cache-busting query each time, is one blocked item, not thousands. True when the count changed.
    /// </summary>
    public bool CountBlocked(string url, string host, int category)
    {
        var q = url.IndexOf('?');
        var key = q > 0 ? url[..q] : url;
        if (_blockedUrls.Contains(key)) return false;
        if (_blockedUrls.Count < 20_000) _blockedUrls.Add(key);
        Blocked++;
        if (category == 1) { BlockedTrackers++; Interlocked.Increment(ref TotalTrackers); } else { BlockedAds++; Interlocked.Increment(ref TotalAds); }
        BlockedHosts[host] = BlockedHosts.GetValueOrDefault(host) + 1;
        return true;
    }

    public void ResetBlocked()
    {
        Blocked = BlockedAds = BlockedTrackers = 0;
        BlockedHosts.Clear();
        _blockedUrls.Clear();
        _blockedTimes.Clear();
    }

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public ImageSource? Favicon { get => _favicon; set => Set(ref _favicon, value); }
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
    public bool IsPlayingAudio { get => _isPlayingAudio; set => Set(ref _isPlayingAudio, value); }
    public TabLife Life { get => _life; set => Set(ref _life, value); }
    public bool CanGoBack { get => _canGoBack; set => Set(ref _canGoBack, value); }
    public bool CanGoForward { get => _canGoForward; set => Set(ref _canGoForward, value); }

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : IsStartPage || string.IsNullOrEmpty(Url) ? "New tab" : Url;

    public TabFacts Facts => new(IsPlayingAudio, IsLoading, HasAddress: !IsStartPage && !string.IsNullOrEmpty(Url) && Url != "about:blank", IsPinned);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(Title) or nameof(Url)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayTitle)));
    }
}
