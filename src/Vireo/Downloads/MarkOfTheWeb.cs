using System.IO;

namespace Vireo.Downloads;

/// <summary>
/// Marks a downloaded file as coming from the internet (the "Zone.Identifier" note Windows reads), exactly as other browsers do.
/// With it, Windows SmartScreen checks a program before it first runs and Office opens documents in Protected View.
/// </summary>
public static class MarkOfTheWeb
{
    public static void Apply(string path, string? url, string? referer)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return;
        try
        {
            var lines = new List<string> { "[ZoneTransfer]", "ZoneId=3" };
            // Only real web addresses, never anything with a password or a token in it beyond the address itself.
            if (Clean(referer) is { } r) lines.Add("ReferrerUrl=" + r);
            if (Clean(url) is { } u) lines.Add("HostUrl=" + u);
            File.WriteAllText(path + ":Zone.Identifier", string.Join("\r\n", lines) + "\r\n");
        }
        catch { /* not NTFS (a USB stick, a network share): nothing to mark */ }
    }

    /// <summary>Is the file marked as from the internet? (For tests.)</summary>
    public static bool IsMarked(string path)
    {
        try { return File.Exists(path + ":Zone.Identifier") && File.ReadAllText(path + ":Zone.Identifier").Contains("ZoneId=3"); }
        catch { return false; }
    }

    private static string? Clean(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
        // Strip any user:password@ part, as Windows' own guidance asks.
        return new UriBuilder(u) { UserName = "", Password = "" }.Uri.ToString();
    }
}
