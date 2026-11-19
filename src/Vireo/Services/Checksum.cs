using System.IO;
using System.Security.Cryptography;

namespace Vireo.Services;

/// <summary>
/// Checking a downloaded file against the checksum its site publishes (SHA-256, or the older SHA-1 and MD5, told apart by
/// length). Reads the file once, in the background, without loading it into memory.
/// </summary>
public static class Checksum
{
    /// <summary>The kind of checksum a pasted text is, from its length; null when it is not one.</summary>
    public static HashAlgorithmName? KindOf(string text)
    {
        var hex = Clean(text);
        if (hex.Length == 0 || !hex.All(Uri.IsHexDigit)) return null;
        return hex.Length switch { 64 => HashAlgorithmName.SHA256, 40 => HashAlgorithmName.SHA1, 32 => HashAlgorithmName.MD5, _ => null };
    }

    /// <summary>
    /// The checksum alone, in lower case: without a "SHA-256:" in front, the file name after it (as sha256sum writes it), or
    /// the spaces and colons some sites put in.
    /// </summary>
    public static string Clean(string text)
    {
        var t = text.Trim();
        var colon = t.IndexOf(':');
        if (colon is > 0 and < 9 && t[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && t[..colon].Any(c => c != '-' && !Uri.IsHexDigit(c)))
            t = t[(colon + 1)..].Trim();
        var first = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (first.Length >= 32 && first.All(Uri.IsHexDigit)) return first.ToLowerInvariant();
        return new string(t.Where(c => !char.IsWhiteSpace(c) && c is not (':' or '-')).ToArray()).ToLowerInvariant();
    }

    public static async Task<string> OfFileAsync(string path, HashAlgorithmName kind, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(kind);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        long done = 0, total = Math.Max(1, file.Length);
        var lastReport = 0L;
        int read;
        while ((read = await file.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            if (done - lastReport > 32 << 20) { lastReport = done; progress?.Report((double)done / total); }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static bool Matches(string computed, string pasted) => string.Equals(Clean(computed), Clean(pasted), StringComparison.Ordinal);
}
