using System.Text.RegularExpressions;

namespace Vireo.Services;

/// <summary>Turns what a person types in the address bar into a page address or a search.</summary>
public static partial class UrlInput
{
    public const string DefaultSearch = "https://www.google.com/search?q={0}";

    private static readonly string[] Schemes = ["http", "https", "file", "about", "data", "view-source", "blob"];

    [GeneratedRegex(@"^(localhost|(\d{1,3}\.){3}\d{1,3}|\[[0-9a-fA-F:]+\])(:\d{1,5})?([/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalHost();

    // host.tld[:port][/path] with no spaces; the last label is letters (or punycode).
    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+([a-z]{2,}|xn--[a-z0-9-]+)(:\d{1,5})?([/?#]\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Domain();

    /// <summary>The address to open for typed text, or null when it is empty.</summary>
    public static string? Resolve(string? input, string searchTemplate = DefaultSearch)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        var colon = text.IndexOf(':');
        if (colon > 0 && Schemes.Contains(text[..colon], StringComparer.OrdinalIgnoreCase) && !(char.IsDigit(text.ElementAtOrDefault(colon + 1)) && !text.Contains("://")))
            return text;

        if (!text.Contains(' '))
        {
            if (LocalHost().IsMatch(text)) return "http://" + text;
            if (Domain().IsMatch(text)) return "https://" + text;
        }
        return string.Format(searchTemplate, Uri.EscapeDataString(text));
    }

    /// <summary>The short form shown in the address bar while it is not being edited.</summary>
    public static string Display(string? url)
    {
        if (string.IsNullOrEmpty(url) || url == "about:blank") return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        if (u.Scheme is "http" or "https")
        {
            var path = u.PathAndQuery == "/" ? "" : u.PathAndQuery;
            var host = u.Host + (u.IsDefaultPort ? "" : ":" + u.Port);
            return (u.Scheme == "http" ? "http://" : "") + host + path + u.Fragment;
        }
        return url;
    }
}
