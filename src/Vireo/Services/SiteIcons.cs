using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vireo.Services;

/// <summary>
/// Remembers each site's own icon (the one its tab showed), so lists of sites (history, bookmarks, the start page) can show
/// real icons without asking anyone for them. Kept as small PNG files next to the profile, never in private windows.
/// </summary>
internal static class SiteIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Images = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string?> DataUris = new(StringComparer.OrdinalIgnoreCase);
    private static string? _folder;

    /// <summary>Where the icons live. Set by a normal (not private) window.</summary>
    public static void Use(string profileFolder)
    {
        _folder = Path.Combine(profileFolder, "icons");
        try { Directory.CreateDirectory(_folder); } catch { _folder = null; }
    }

    /// <summary>The site an address belongs to, as the icon files are named: the host without "www.".</summary>
    public static string? Key(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0) return null;
        var host = u.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static string? PathFor(string key) =>
        _folder is null || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : Path.Combine(_folder, key + ".png");

    /// <summary>Keeps the icon a page showed. Written at most once a week per site.</summary>
    public static void Remember(string url, ImageSource? icon)
    {
        if (icon is not BitmapSource bmp || Key(url) is not { } key || PathFor(key) is not { } path) return;
        Images[key] = bmp;
        DataUris.TryRemove(key, out _);
        try
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(7)) return;
        }
        catch { return; }
        if (!bmp.IsFrozen) return;
        _ = Task.Run(() =>
        {
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var file = File.Create(path);
                encoder.Save(file);
            }
            catch { /* an icon is never worth an error */ }
        });
    }

    /// <summary>The site's icon, or null when Vireo has not seen one.</summary>
    public static ImageSource? For(string url)
    {
        if (Key(url) is not { } key) return null;
        return Images.GetOrAdd(key, k =>
        {
            if (PathFor(k) is not { } path || !File.Exists(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        });
    }

    /// <summary>The icon as a data: address, for the start page (which may not read files itself).</summary>
    public static string? DataUri(string url)
    {
        if (Key(url) is not { } key) return null;
        if (DataUris.TryGetValue(key, out var known)) return known;
        try
        {
            // only a file that is there is remembered: a site seen later still gets its icon
            if (PathFor(key) is { } path && File.Exists(path) && new FileInfo(path).Length < 64 * 1024)
                return DataUris[key] = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
        }
        catch { /* none */ }
        return null;
    }
}
