using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vireo.Services;

/// <summary>
/// What the YouTube script looks for (see Assets/youtube.js): key names in the player's data and page selectors. Data only,
/// never code, and checked here field by field before it reaches a page.
/// </summary>
public sealed record YouTubeRules(
    int Format, string Name, int Version, string Published,
    string[] PlayerDataKeys, string[] MessageKeys, string[] Hide, string Player, string[] AdPlayerClasses, string[] SkipButtons,
    string Enforcement, string EnforcementDialog, string EnforcementBackdrop)
{
    public const int MaxBytes = 64 * 1024;
    private const int MaxSignatureBytes = 2048;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex Key = new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex ClassName = new("^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
    // A CSS selector, without anything that could end the rule and start another ({ } ;), an at-rule (@), an escape, a
    // comment or markup. Attribute selectors with quotes are fine: the script only ever uses these as selectors.
    private static readonly Regex Selector = new("""^[A-Za-z0-9 #.\-_>+~:()\[\]="'*,^$|]{1,200}$""", RegexOptions.CultureInvariant);

    /// <summary>The rules, if <paramref name="text"/> is a well-formed rules file of this format; null otherwise.</summary>
    public static YouTubeRules? Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) return null;
        YouTubeRules? r;
        try { r = JsonSerializer.Deserialize<YouTubeRules>(text, Json); }
        catch (JsonException) { return null; }
        if (r is null || r.Format != 1 || r.Name != "youtube" || r.Version < 1 || r.Version > 1_000_000) return null;
        bool All(string[]? list, Regex rule, int max) => list is { Length: > 0 } && list.Length <= max && list.All(x => x is not null && rule.IsMatch(x));
        if (!All(r.PlayerDataKeys, Key, 50) || !All(r.MessageKeys, Key, 20) || !All(r.Hide, Selector, 300)
            || !All(r.AdPlayerClasses, ClassName, 20) || !All(r.SkipButtons, Selector, 30)) return null;
        if (new[] { r.Player, r.Enforcement, r.EnforcementDialog, r.EnforcementBackdrop }.Any(s => s is null || !Selector.IsMatch(s))) return null;
        return r;
    }

    /// <summary>The rules as the script receives them: written out again from the checked fields, not the file's text.</summary>
    public string ToScriptJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>What the signature's signed comment must say for this file: its name and version.</summary>
    public static string TrustedComment(int version) => $"vireo rules youtube {version}";

    /// <summary>A downloaded file and its signature: the rules, if the signature is the key's, made for exactly this version.</summary>
    public static YouTubeRules? Checked(byte[] file, string signature, Minisign.PublicKey key)
    {
        if (file.Length > MaxBytes || signature.Length > MaxSignatureBytes) return null;
        if (!Minisign.Verify(key, file, signature, out var comment)) return null;
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(file); } catch (ArgumentException) { return null; }
        var rules = Parse(text);
        return rules is not null && comment == TrustedComment(rules.Version) ? rules : null;
    }
}

/// <summary>
/// Where the YouTube rules come from. The copy built into Vireo is always there; a newer one is downloaded from Vireo's
/// website, from time to time, and used only if it is signed with Refora's rules key (built into Vireo), names its own
/// version in the signed comment, and is newer than what Vireo has. The last good one is kept (with its signature, and
/// checked again at every start), so a failed or tampered download never leaves Vireo without rules.
/// </summary>
public sealed class YouTubeRulesStore
{
    public const string Address = "https://vireo.reforatech.com/rules/v1/youtube.json";
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(12);

    private readonly string _folder;
    private readonly Minisign.PublicKey? _key;
    private readonly string _builtInPath;

    public YouTubeRules Current { get; private set; }
    public event Action? Changed;

    /// <param name="folder">Where the last good download is kept (the profile's "rules" folder).</param>
    /// <param name="key">The rules key; null leaves Vireo with its built-in rules only.</param>
    public YouTubeRulesStore(string folder, Minisign.PublicKey? key, string? builtInPath = null)
    {
        _folder = folder;
        _key = key;
        _builtInPath = builtInPath ?? Path.Combine(AppContext.BaseDirectory, "Assets", "rules", "youtube.json");
        Current = BuiltIn();
        if (Saved() is { } saved && saved.Version > Current.Version) Current = saved;
    }

    private string SavedPath => Path.Combine(_folder, "youtube.json");
    private string SavedSignaturePath => SavedPath + ".minisig";

    /// <summary>Refora's rules key, built into Vireo (null until one has been made: then there are no downloads).</summary>
    public static Minisign.PublicKey? BuiltInKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("rules-key.pub");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return Minisign.ParsePublicKey(reader.ReadToEnd());
    }

    private YouTubeRules BuiltIn()
    {
        try { if (YouTubeRules.Parse(File.ReadAllText(_builtInPath)) is { } r) return r; } catch { /* missing: the minimum below */ }
        // The app's own file is missing or damaged: nothing to hide, no keys to remove, but the script still runs safely.
        return new YouTubeRules(1, "youtube", 0, "", ["adPlacements", "playerAds", "adSlots"], ["enforcementMessageViewModel"], ["ytd-ad-slot-renderer"],
            ".html5-video-player", ["ad-showing"], [".ytp-skip-ad-button"], "ytd-enforcement-message-view-model", "tp-yt-paper-dialog", "tp-yt-iron-overlay-backdrop");
    }

    /// <summary>The last good download, if it still checks out.</summary>
    private YouTubeRules? Saved()
    {
        if (_key is null) return null;
        try
        {
            if (!File.Exists(SavedPath) || !File.Exists(SavedSignaturePath)) return null;
            return YouTubeRules.Checked(File.ReadAllBytes(SavedPath), File.ReadAllText(SavedSignaturePath), _key);
        }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Asks the website for newer rules. True when newer, good rules were taken. Never throws: no network, a refusal, a file
    /// that is too big, badly signed, older or malformed all leave the current rules as they are.
    /// </summary>
    public async Task<bool> CheckAsync(HttpClient http, string address = Address, CancellationToken ct = default)
    {
        if (_key is null) return false;
        try
        {
            var file = await GetSmallAsync(http, address, YouTubeRules.MaxBytes, ct);
            var signature = await GetSmallAsync(http, address + ".minisig", 2048, ct);
            if (file is null || signature is null) return false;
            var rules = YouTubeRules.Checked(file, Encoding.UTF8.GetString(signature), _key);
            if (rules is null || rules.Version <= Current.Version) return false; // tampered, or not newer: an old file is never taken back
            Directory.CreateDirectory(_folder);
            Write(SavedPath, file);
            Write(SavedSignaturePath, signature);
            Current = rules;
            Changed?.Invoke();
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    private static void Write(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The body, if the answer is a success and no bigger than <paramref name="max"/> (stops reading past it).</summary>
    private static async Task<byte[]?> GetSmallAsync(HttpClient http, string address, int max, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        using var res = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > max) return null;
        await using var stream = await res.Content.ReadAsStreamAsync(limit.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, limit.Token)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
