using System.Net;
using System.Text.Json;

namespace Vireo.Downloads;

/// <summary>What a refusal from a file host means, in words, and whether trying again soon can help.</summary>
public enum HostRefusal
{
    /// <summary>Nothing special is known: the usual message.</summary>
    None,
    /// <summary>Too many connections at once for a free account: fewer connections, and try again.</summary>
    TooManyConnections,
    /// <summary>The host wants a person to pass a check on its page first (a captcha).</summary>
    NeedsPage,
    /// <summary>A daily or monthly allowance is used up: only time (or an account) helps.</summary>
    LimitReached,
}

/// <summary>
/// Knowledge about file hosts: their direct-download addresses, and what their refusals mean. Pixeldrain documents its
/// answers (https://pixeldrain.com/api): a 403 with a JSON body whose "value" says why. Other hosts get a message from the
/// status alone (429 too many requests, 509 bandwidth used up).
/// </summary>
public static class FileHosts
{
    /// <summary>The address to download a host's share page from, when the host has one (pixeldrain.com/u/ID); null otherwise.</summary>
    public static string? DirectAddress(Uri page)
    {
        if (IsPixeldrain(page.Host))
        {
            var parts = page.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "u" && IsId(parts[1])) return $"https://{page.Host}/api/file/{parts[1]}?download";
        }
        return null;
    }

    private static bool IsPixeldrain(string host) =>
        host.Equals("pixeldrain.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".pixeldrain.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsId(string s) => s.Length is > 3 and < 40 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The short code a host put in its answer ("transfer_limit_exceeded"), read from a JSON body; null if none.</summary>
    public static string? ReasonCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and < 80 } code ? code : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>What a refusal means.</summary>
    public static HostRefusal Classify(string host, HttpStatusCode status, string? reason) => reason switch
    {
        "max_concurrent_downloads" => HostRefusal.TooManyConnections,
        "transfer_limit_exceeded" or "download_limit_exceeded" => HostRefusal.LimitReached,
        "file_rate_limited_captcha_required" or "virus_detected_captcha_required" or "server_overload_captcha_required" or "hotlink_detected" => HostRefusal.NeedsPage,
        _ when (int)status == 509 => HostRefusal.LimitReached, // "Bandwidth Limit Exceeded", used by many hosts
        _ => HostRefusal.None,
    };

    /// <summary>The message for a refusal that ended a download, or null for the usual one.</summary>
    public static string? Explain(string host, HttpStatusCode status, string? reason)
    {
        var name = SiteName(host);
        return reason switch
        {
            "transfer_limit_exceeded" or "download_limit_exceeded" =>
                $"{name}'s daily download limit for free users is reached. Retry tomorrow (it carries on from where it stopped), or sign in to {name} on its page.",
            "file_rate_limited_captcha_required" or "hotlink_detected" =>
                $"{name} wants you to open this file on its page first (it asks for a quick check). Open the link, then retry here.",
            "server_overload_captcha_required" =>
                $"{name} is very busy and asks for a quick check on its page. Open the link, then retry here.",
            "virus_detected_captcha_required" =>
                $"{name} marked this file as possibly harmful and asks you to confirm on its page. Think twice before opening it.",
            "max_concurrent_downloads" =>
                $"{name} allows only a few downloads at once for free users. Retry when another download has finished.",
            _ when (int)status == 509 => $"{name}'s bandwidth limit is used up for now. Retry later; it carries on from where it stopped.",
            _ when status == HttpStatusCode.TooManyRequests => $"{name} says too many downloads at once. Retry in a few minutes; it carries on from where it stopped.",
            _ => null,
        };
    }

    /// <summary>"pixeldrain" for pixeldrain.com, "example" for files.example.co.uk.</summary>
    public static string SiteName(string host)
    {
        var root = Vireo.Media.MediaProbe.RootDomainOf(host);
        var dot = root.IndexOf('.');
        return dot > 0 ? root[..dot] : root;
    }
}
