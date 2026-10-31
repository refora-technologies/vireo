using System.Net.Http;

namespace Vireo.Downloads;

/// <summary>The headers a browser would send, so servers that check where a request comes from see something believable.</summary>
public static class RequestHeaders
{
    /// <summary>How the page that asked relates to the address it asks for.</summary>
    public static string SiteRelation(string url, string? referer)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return "cross-site";
        if (!Uri.TryCreate(referer, UriKind.Absolute, out var from) || from.Scheme is not ("http" or "https")) return "none";
        if (from.Scheme == target.Scheme && from.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) && from.Port == target.Port) return "same-origin";
        return Media.MediaProbe.RootDomainOf(from.Host).Equals(Media.MediaProbe.RootDomainOf(target.Host), StringComparison.OrdinalIgnoreCase) ? "same-site" : "cross-site";
    }

    /// <summary>
    /// Fills in a request. <paramref name="script"/> is for pieces of a stream, which a page's player asks for with scripts (and so sends
    /// Origin and the Sec-Fetch headers); a plain file download is a navigation and sends neither.
    /// </summary>
    public static void Apply(HttpRequestMessage req, string url, string userAgent, string? referer, string? cookie, bool script)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity"); // a compressed answer would not match the size of the file
        SetReferer(req, url, referer, script);
        if (!string.IsNullOrEmpty(cookie)) req.Headers.TryAddWithoutValidation("Cookie", cookie);
    }

    /// <summary>Changes where the request says it came from (an empty referer sends none).</summary>
    public static void SetReferer(HttpRequestMessage req, string url, string? referer, bool script)
    {
        foreach (var h in new[] { "Referer", "Origin", "Sec-Fetch-Site", "Sec-Fetch-Mode", "Sec-Fetch-Dest" }) req.Headers.Remove(h);
        if (script)
        {
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", SiteRelation(url, referer));
        }
        if (string.IsNullOrEmpty(referer) || !Uri.TryCreate(referer, UriKind.Absolute, out var r) || r.Scheme is not ("http" or "https")) return;
        req.Headers.TryAddWithoutValidation("Referer", referer);
        if (script) req.Headers.TryAddWithoutValidation("Origin", r.GetLeftPart(UriPartial.Authority));
    }
}
