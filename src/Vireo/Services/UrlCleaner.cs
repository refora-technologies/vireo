using System.Text;

namespace Vireo.Services;

/// <summary>Removes the parts of a link that only exist to follow you from site to site.</summary>
public static class UrlCleaner
{
    private static readonly HashSet<string> Tracking = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id", "utm_name", "utm_brand", "utm_social", "utm_social-type",
        "fbclid", "gclid", "gclsrc", "dclid", "gbraid", "wbraid", "msclkid", "twclid", "ttclid", "li_fat_id", "yclid", "igshid", "igsh",
        "mc_eid", "mc_cid", "_hsenc", "_hsmi", "__hssc", "__hstc", "hsctatracking", "vero_id", "oly_enc_id", "oly_anon_id", "wickedid", "ref_src", "ref_url",
        "_ga", "_gl", "s_cid", "mkt_tok", "spm", "scm",
    };

    /// <summary>The same link without tracking parameters. Anything else in it is left exactly as it was.</summary>
    public static string Clean(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0) return url;
        var hash = url.IndexOf('#', q);
        var query = hash < 0 ? url[(q + 1)..] : url[(q + 1)..hash];
        var tail = hash < 0 ? "" : url[hash..];

        var kept = new List<string>();
        var changed = false;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            if (Tracking.Contains(name)) changed = true;
            else kept.Add(pair);
        }
        if (!changed) return url;
        var sb = new StringBuilder(url[..q]);
        if (kept.Count > 0) sb.Append('?').Append(string.Join('&', kept));
        return sb.Append(tail).ToString();
    }
}
