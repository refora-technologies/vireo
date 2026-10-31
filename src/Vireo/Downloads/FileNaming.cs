using System.IO;
using System.Net.Http.Headers;

namespace Vireo.Downloads;

/// <summary>Picks a safe, unused file name for a download.</summary>
public static class FileNaming
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["audio/mpeg"] = ".mp3", ["audio/mp4"] = ".m4a", ["audio/ogg"] = ".ogg",
        ["application/pdf"] = ".pdf", ["application/zip"] = ".zip", ["application/x-7z-compressed"] = ".7z",
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/webp"] = ".webp", ["image/gif"] = ".gif",
        ["application/vnd.microsoft.portable-executable"] = ".exe", ["application/x-msdownload"] = ".exe",
    };

    /// <summary>The name the server asked for, from the Content-Disposition header.</summary>
    public static string? FromDisposition(ContentDispositionHeaderValue? header)
    {
        var name = header?.FileNameStar ?? header?.FileName;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim('"');
    }

    public static string FromUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var last = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
            if (!string.IsNullOrWhiteSpace(last)) return last;
        }
        catch { /* fall through */ }
        return "download";
    }

    public static string Sanitize(string name, string? contentType = null)
    {
        var clean = new string(name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (clean.Length == 0) clean = "download";
        var stem = Path.GetFileNameWithoutExtension(clean);
        if (Reserved.Contains(stem)) clean = "_" + clean;
        if (!Path.HasExtension(clean) && contentType is not null && Extensions.TryGetValue(contentType.Split(';')[0].Trim(), out var ext)) clean += ext;
        if (clean.Length > 150)
        {
            var e = Path.GetExtension(clean);
            clean = clean[..(150 - e.Length)] + e;
        }
        return clean;
    }

    /// <summary>The name, or "name (1)", "name (2)"... when the file or a download in progress already uses it.</summary>
    public static string Unique(string folder, string name, Func<string, bool> isTaken)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (var i = 1; File.Exists(Path.Combine(folder, candidate)) || File.Exists(Path.Combine(folder, candidate) + ".vireopart") || isTaken(candidate); i++)
            candidate = $"{stem} ({i}){ext}";
        return candidate;
    }
}
