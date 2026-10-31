using System.IO;
namespace Vireo.Services;

/// <summary>Human-friendly sizes, speeds and times.</summary>
public static class Format
{
    public static string Bytes(long n)
    {
        if (n < 0) return "";
        if (n < 1024) return $"{n} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double v = n / 1024.0;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{(v >= 100 ? v.ToString("0") : v.ToString("0.0"))} {units[i]}";
    }

    /// <summary>"4:12" or "1:02:03".</summary>
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond < 1 ? "" : Bytes((long)bytesPerSecond) + "/s";

    public static string Eta(TimeSpan? t)
    {
        if (t is not { } s || s < TimeSpan.Zero) return "";
        if (s.TotalSeconds < 60) return $"{Math.Max(1, (int)Math.Round(s.TotalSeconds))} s left";
        if (s.TotalMinutes < 60) return $"{(int)Math.Round(s.TotalMinutes)} min left";
        return $"{s.TotalHours:0.0} h left";
    }

    /// <summary>"Today 22:14", "Yesterday 09:03" or "3 Oct".</summary>
    public static string When(DateTime t, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        if (t.Date == today) return $"Today {t:HH:mm}";
        if (t.Date == today.AddDays(-1)) return $"Yesterday {t:HH:mm}";
        return t.Year == today.Year ? $"{t.Day} {t:MMM}" : $"{t.Day} {t:MMM yyyy}";
    }

    /// <summary>A glyph (Segoe Fluent Icons) that suggests the kind of file.</summary>
    public static int IconFor(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".mkv" or ".webm" or ".mov" or ".avi" or ".m4v" => 0xE714,   // video
            ".mp3" or ".m4a" or ".flac" or ".wav" or ".ogg" or ".opus" => 0xE8D6,   // music
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" => 0xEB9F, // picture
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => 0xF012,                  // archive
            ".exe" or ".msi" => 0xE7B8,                                              // app
            ".pdf" or ".doc" or ".docx" or ".txt" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => 0xE8A5, // document
            _ => 0xE8A5,
        };
    }
}
