using Vireo.Media;

namespace Vireo.Models;

public enum GrabCategory
{
    All,
    Media,
    Images,
    Documents,
    Archives,
    Other
}

public sealed class GrabbedItem
{
    public required string Url { get; init; }
    public required string FileName { get; set; }
    public string? Title { get; init; }
    public string Extension { get; set; } = "";
    public GrabCategory Category { get; set; } = GrabCategory.Other;
    public bool IsSelected { get; set; } = true;
    public long? SizeBytes { get; set; }
    public DetectedMedia? StreamMedia { get; set; }

    public static GrabCategory ClassifyExtension(string ext)
    {
        ext = ext.Trim().ToLowerInvariant();
        return ext switch
        {
            "mp4" or "m4v" or "mkv" or "webm" or "avi" or "mov" or "flv" or "wmv" or "3gp" or "ts" or "asf"
                or "m2ts" or "vob" or "mp3" or "m4a" or "m4b" or "aac" or "ogg" or "oga" or "opus" or "flac"
                or "wav" or "wma" or "alac" or "aif" or "aiff" or "mid" or "midi" or "weba" or "m3u8" or "mpd" => GrabCategory.Media,

            "jpg" or "jpeg" or "png" or "webp" or "gif" or "svg" or "avif" or "bmp" or "ico" or "tiff" or "tif" => GrabCategory.Images,

            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "txt" or "rtf" or "csv" or "epub"
                or "mobi" or "odt" or "ods" or "odp" => GrabCategory.Documents,

            "zip" or "rar" or "7z" or "tar" or "gz" or "bz2" or "xz" or "iso" or "exe" or "msi" or "apk"
                or "dmg" or "pkg" or "bin" or "torrent" or "deb" or "rpm" or "cab" => GrabCategory.Archives,

            _ => GrabCategory.Other
        };
    }
}
