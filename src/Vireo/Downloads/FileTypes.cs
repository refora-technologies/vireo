using System.IO;

namespace Vireo.Downloads;

/// <summary>
/// Sorting downloads into folders by what they are (when the person turns it on): Video, Music, Pictures, Documents,
/// Programs and Compressed, each a folder inside the download folder. Anything else stays in the download folder itself.
/// </summary>
public static class FileTypes
{
    public const string Video = "Video", Music = "Music", Pictures = "Pictures", Documents = "Documents", Programs = "Programs", Compressed = "Compressed";

    public static readonly IReadOnlyList<string> All = [Video, Music, Pictures, Documents, Programs, Compressed];

    private static readonly Dictionary<string, string> ByExtension = Build(
        (Video, "mp4 mkv webm avi mov wmv flv m4v ts mts 3gp mpg mpeg ogv"),
        (Music, "mp3 m4a aac flac wav ogg oga opus wma alac aiff"),
        (Pictures, "jpg jpeg png gif webp bmp svg heic heif avif tif tiff ico raw"),
        (Documents, "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt csv epub mobi md"),
        (Programs, "exe msi msix msixbundle appx appxbundle apk dmg pkg deb rpm appimage"),
        (Compressed, "zip rar 7z tar gz tgz bz2 xz zst iso cab lz lzma"));

    private static Dictionary<string, string> Build(params (string Kind, string Extensions)[] groups)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, list) in groups)
            foreach (var ext in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map[ext] = kind;
        return map;
    }

    /// <summary>The folder name for a file ("Video" for "film.mkv"), or null when it belongs in the download folder itself.</summary>
    public static string? KindOf(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        // A split archive ("film.part1.rar", "backup.7z.001") is compressed too.
        if (ext.Length == 3 && ext.All(char.IsAsciiDigit)) ext = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName)).TrimStart('.');
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var kind) ? kind : null;
    }

    /// <summary>
    /// Whether an address points straight at a file worth offering to download, by its extension. Web pages are not, and
    /// neither are pictures: people copy picture addresses all the time without wanting a download each time.
    /// </summary>
    public static bool IsFileAddress(Uri uri)
    {
        var name = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/'));
        var slash = name.LastIndexOf('/');
        return KindOf(slash >= 0 ? name[(slash + 1)..] : name) is { } kind && kind != Pictures;
    }
}
