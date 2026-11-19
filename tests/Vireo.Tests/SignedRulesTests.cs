using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Vireo.Services;
using Vireo.Signing;
using Xunit;

namespace Vireo.Tests;

/// <summary>The YouTube rules file: signed with Refora's key, checked by Vireo, and nothing else accepted.</summary>
public sealed class SignedRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "tmp", "rules-" + Guid.NewGuid().ToString("N"));
    private readonly (byte[] Seed, byte[] KeyId, string PublicText) _key = Signer.TestKey();
    private Minisign.PublicKey Key => Minisign.ParsePublicKey(_key.PublicText)!;

    public SignedRulesTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* still closing */ } }

    private static string BuiltIn => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "rules", "youtube.json"));
    private static byte[] RulesFile(int version, string? hideExtra = null)
    {
        var text = BuiltIn.Replace("\"version\": 1,", $"\"version\": {version},");
        if (hideExtra is not null) text = text.Replace("\"hide\": [", $"\"hide\": [\"{hideExtra}\", ");
        return Encoding.UTF8.GetBytes(text);
    }
    private string Sign(byte[] file, int forVersion) => Signer.SignWithSeed(_key.Seed, _key.KeyId, file, YouTubeRules.TrustedComment(forVersion));

    // ---- minisign itself ----

    [Fact]
    public void A_real_minisign_signature_is_accepted_and_one_changed_byte_is_not()
    {
        // Basalt's published release list, signed by the official minisign library: proof the format is read the same way.
        var data = Path.Combine(AppContext.BaseDirectory, "data", "minisign");
        var key = Minisign.ParsePublicKey(File.ReadAllText(Path.Combine(data, "basalt-release-key.pub")))!;
        var text = File.ReadAllText(Path.Combine(data, "basalt-1.6.4-SHA256SUMS")).Replace("\r\n", "\n");
        var at = text.IndexOf("\nuntrusted comment:", StringComparison.Ordinal) + 1;
        var list = Encoding.UTF8.GetBytes(text[..at]);
        Assert.True(Minisign.Verify(key, list, text[at..], out var comment));
        Assert.Equal("basalt release 1.6.4", comment);

        list[10] ^= 1;
        Assert.False(Minisign.Verify(key, list, text[at..], out _));
    }

    [Fact]
    public void The_signers_own_signatures_check_out()
    {
        var file = RulesFile(3);
        Assert.Equal(3, YouTubeRules.Checked(file, Sign(file, 3), Key)?.Version);
    }

    [Fact]
    public void A_changed_file_a_changed_signature_or_another_key_is_refused()
    {
        var file = RulesFile(3);
        var signature = Sign(file, 3);

        var changed = (byte[])file.Clone();
        changed[^3] = (byte)' ';
        Assert.Null(YouTubeRules.Checked(changed, signature, Key));

        var lines = signature.Split('\n');
        var global = Convert.FromBase64String(lines[3]);
        global[0] ^= 1;
        lines[3] = Convert.ToBase64String(global);
        Assert.Null(YouTubeRules.Checked(file, string.Join('\n', lines), Key));

        var other = Signer.TestKey();
        Assert.Null(YouTubeRules.Checked(file, Signer.SignWithSeed(other.Seed, other.KeyId, file, YouTubeRules.TrustedComment(3)), Key));
    }

    [Fact]
    public void A_signature_made_for_another_version_is_refused()
    {
        // An old file cannot be passed off as a newer one: the signed comment names the version inside the file.
        var file = RulesFile(3);
        Assert.Null(YouTubeRules.Checked(file, Sign(file, 4), Key));
        Assert.Null(YouTubeRules.Checked(file, Signer.SignWithSeed(_key.Seed, _key.KeyId, file, "something else"), Key));
    }

    // ---- what a rules file may contain ----

    [Theory]
    [InlineData("x { } body")] // could end the rule and start another
    [InlineData("a; b")]
    [InlineData("@import")]
    [InlineData("a\\\\62 ")]
    [InlineData("<script>")]
    public void A_selector_that_could_be_more_than_a_selector_is_refused(string selector)
    {
        var file = RulesFile(3, selector);
        Assert.Null(YouTubeRules.Checked(file, Sign(file, 3), Key));
    }

    [Fact]
    public void A_signed_file_of_another_format_or_shape_is_refused()
    {
        var text = BuiltIn.Replace("\"format\": 1", "\"format\": 2");
        Assert.Null(YouTubeRules.Parse(text));
        Assert.Null(YouTubeRules.Parse(BuiltIn.Replace("\"adPlacements\"", "\"ad-placements\"")));
        Assert.Null(YouTubeRules.Parse("{}"));
        Assert.Null(YouTubeRules.Parse("not json"));
        Assert.NotNull(YouTubeRules.Parse(BuiltIn));
    }

    [Fact]
    public void The_script_gets_the_checked_fields_written_out_again()
    {
        var json = YouTubeRules.Parse(BuiltIn)!.ToScriptJson();
        Assert.Contains("\"playerDataKeys\":[\"adPlacements\"", json);
        Assert.DoesNotContain("</", json);
    }

    // ---- the store: built in, downloaded, kept ----

    private sealed class Files : IDisposable
    {
        private readonly HttpListener _listener = new();
        public Dictionary<string, byte[]> Content { get; } = [];
        public string Base { get; }
        public Files()
        {
            for (var port = 21000 + Random.Shared.Next(20000); ; port++)
            {
                try { _listener.Prefixes.Clear(); _listener.Prefixes.Add($"http://127.0.0.1:{port}/"); _listener.Start(); Base = $"http://127.0.0.1:{port}"; break; }
                catch (HttpListenerException) { /* taken */ }
            }
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _listener.GetContextAsync(); } catch { return; }
                    if (Content.TryGetValue(c.Request.Url!.AbsolutePath, out var bytes)) { c.Response.ContentLength64 = bytes.Length; await c.Response.OutputStream.WriteAsync(bytes); }
                    else c.Response.StatusCode = 404;
                    c.Response.Close();
                }
            });
        }
        public void Dispose() => _listener.Close();
    }

    private YouTubeRulesStore Store(Minisign.PublicKey? key = null) => new(Path.Combine(_root, "saved"), key ?? Key);
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task A_newer_signed_file_is_taken_kept_and_used_at_the_next_start()
    {
        using var site = new Files();
        var v2 = RulesFile(2);
        site.Content["/rules/v1/youtube.json"] = v2;
        site.Content["/rules/v1/youtube.json.minisig"] = Encoding.UTF8.GetBytes(Sign(v2, 2));
        var store = Store();
        Assert.Equal(1, store.Current.Version); // built in
        var changed = 0;
        store.Changed += () => changed++;

        Assert.True(await store.CheckAsync(Http, site.Base + "/rules/v1/youtube.json"));
        Assert.Equal(2, store.Current.Version);
        Assert.Equal(1, changed);
        Assert.Equal(2, Store().Current.Version); // kept, and checked again when read back
        Assert.False(await store.CheckAsync(Http, site.Base + "/rules/v1/youtube.json")); // the same again: nothing new
    }

    [Fact]
    public async Task An_older_file_is_never_taken_even_when_properly_signed()
    {
        using var site = new Files();
        var v3 = RulesFile(3);
        site.Content["/r.json"] = v3;
        site.Content["/r.json.minisig"] = Encoding.UTF8.GetBytes(Sign(v3, 3));
        var store = Store();
        Assert.True(await store.CheckAsync(Http, site.Base + "/r.json"));

        var v2 = RulesFile(2);
        site.Content["/r.json"] = v2;
        site.Content["/r.json.minisig"] = Encoding.UTF8.GetBytes(Sign(v2, 2));
        Assert.False(await store.CheckAsync(Http, site.Base + "/r.json"));
        Assert.Equal(3, store.Current.Version);
    }

    [Fact]
    public async Task A_tampered_too_big_or_unsigned_download_changes_nothing()
    {
        using var site = new Files();
        var store = Store();
        var v5 = RulesFile(5);

        site.Content["/tampered.json"] = RulesFile(6); // signed as 5, but the file says 6
        site.Content["/tampered.json.minisig"] = Encoding.UTF8.GetBytes(Sign(v5, 5));
        Assert.False(await store.CheckAsync(Http, site.Base + "/tampered.json"));

        var big = new byte[YouTubeRules.MaxBytes + 10];
        site.Content["/big.json"] = big;
        site.Content["/big.json.minisig"] = Encoding.UTF8.GetBytes(Sign(big, 5));
        Assert.False(await store.CheckAsync(Http, site.Base + "/big.json"));

        site.Content["/unsigned.json"] = v5; // no .minisig at all
        Assert.False(await store.CheckAsync(Http, site.Base + "/unsigned.json"));

        Assert.False(await store.CheckAsync(Http, "http://127.0.0.1:1/nothing.json")); // no answer
        Assert.Equal(1, store.Current.Version);
        Assert.False(File.Exists(Path.Combine(_root, "saved", "youtube.json")));
    }

    [Fact]
    public async Task A_kept_copy_changed_on_disk_is_ignored_and_the_built_in_rules_are_used()
    {
        using var site = new Files();
        var v4 = RulesFile(4);
        site.Content["/r.json"] = v4;
        site.Content["/r.json.minisig"] = Encoding.UTF8.GetBytes(Sign(v4, 4));
        Assert.True(await Store().CheckAsync(Http, site.Base + "/r.json"));

        var saved = Path.Combine(_root, "saved", "youtube.json");
        File.WriteAllText(saved, File.ReadAllText(saved).Replace("ytd-ad-slot-renderer", "ytd-ad-slot-renderex"));
        Assert.Equal(1, Store().Current.Version);
    }

    [Fact]
    public async Task Without_a_key_Vireo_keeps_to_its_built_in_rules()
    {
        using var site = new Files();
        var v2 = RulesFile(2);
        site.Content["/r.json"] = v2;
        site.Content["/r.json.minisig"] = Encoding.UTF8.GetBytes(Sign(v2, 2));
        var store = new YouTubeRulesStore(Path.Combine(_root, "saved"), null);
        Assert.False(await store.CheckAsync(Http, site.Base + "/r.json"));
        Assert.Equal(1, store.Current.Version);
    }

    // ---- the real key and the published file ----

    [Fact]
    public void The_key_built_into_Vireo_accepts_the_signed_master_copy_of_the_rules()
    {
        // The master copy (src/Vireo/Assets/rules, also built into Vireo) is what the website publishes, unchanged.
        var key = YouTubeRulesStore.BuiltInKey();
        Assert.NotNull(key); // src/Vireo/rules-key.pub is built into Vireo
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "rules", "youtube.json");
        var rules = YouTubeRules.Checked(File.ReadAllBytes(file), File.ReadAllText(file + ".minisig"), key!);
        Assert.NotNull(rules);
    }

    /// <summary>
    /// The real website, exactly as an installed Vireo reads it: a Vireo whose own rules are older downloads the published
    /// file and its signature from vireo.reforatech.com, checks them with the key built into it, takes them and keeps them.
    /// Run after publishing new rules: VIREO_LIVE=1 dotnet test --filter Published_rules.
    /// </summary>
    [Fact]
    public async Task Published_rules_are_taken_by_Vireo_from_the_website()
    {
        if (Environment.GetEnvironmentVariable("VIREO_LIVE") != "1") return;
        var key = YouTubeRulesStore.BuiltInKey()!;
        // No built-in file here: this store starts from the minimal rules (version 0), so any published version is newer.
        var store = new YouTubeRulesStore(Path.Combine(_root, "live"), key, builtInPath: Path.Combine(_root, "none.json"));
        Assert.Equal(0, store.Current.Version);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Vireo-release-check");
        Assert.True(await store.CheckAsync(http)); // YouTubeRulesStore.Address, the address built into Vireo
        var master = YouTubeRules.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "rules", "youtube.json")))!;
        Assert.Equal(master.Version, store.Current.Version);
        Assert.True(File.Exists(Path.Combine(_root, "live", "youtube.json.minisig"))); // kept, with its signature
    }

    // ---- the signing tool's secret key ----

    [Fact]
    public void The_secret_key_opens_only_with_its_password_and_signs_what_Vireo_accepts()
    {
        var pair = Signer.Generate("a long enough password");
        Assert.DoesNotContain("seed", pair.SecretText, StringComparison.OrdinalIgnoreCase);
        var key = Minisign.ParsePublicKey(pair.PublicText)!;
        var file = RulesFile(7);
        var signature = Signer.Sign(pair.SecretText, "a long enough password", file, YouTubeRules.TrustedComment(7));
        Assert.Equal(7, YouTubeRules.Checked(file, signature, key)?.Version);
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => Signer.Sign(pair.SecretText, "the wrong password!!", file, YouTubeRules.TrustedComment(7)));
        Assert.Throws<ArgumentException>(() => Signer.Generate("short"));
    }
}
