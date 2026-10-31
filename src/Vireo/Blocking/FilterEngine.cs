using System.Text;
using System.Text.RegularExpressions;

namespace Vireo.Blocking;

[Flags]
public enum ResourceType
{
    None = 0, Script = 1, Image = 2, Stylesheet = 4, Font = 8, Media = 16, XmlHttpRequest = 32, Subdocument = 64,
    Ping = 128, WebSocket = 256, Other = 512, Document = 1024, Popup = 2048,
    /// <summary>What a rule with no type options applies to: everything except the page itself.</summary>
    All = Script | Image | Stylesheet | Font | Media | XmlHttpRequest | Subdocument | Ping | WebSocket | Other | Popup,
}

public readonly record struct Verdict(bool Block, string? Rule = null, int Category = 0);

/// <summary>
/// Decides whether a request is an advertisement or a tracker, using the Adblock Plus filter format
/// that EasyList and EasyPrivacy are written in. Rules are indexed by a word taken from their pattern, so
/// a request is only compared with the few rules that share a word with its address.
/// Build it once (AddList), then call Check from any thread.
/// </summary>
public sealed class FilterEngine
{
    private enum Party { Any, Third, First }

    private sealed class Rule
    {
        public string[] Parts = [];
        public bool AnchorHost, AnchorStart, AnchorEnd, Exception, MatchCase;
        public ResourceType Types = ResourceType.All;
        public Party Party;
        public string[]? Include, Exclude;
        public Regex? Regex;
        public string Text = "";
        public int Category;
    }

    /// <summary>
    /// Whole hosts blocked outright ("||tracker.example^" and nothing else), as host lists such as AdGuard's are made of.
    /// Kept as 64-bit hashes rather than rule objects: 190,000 hosts cost about 5 MB this way instead of about 40 MB.
    /// </summary>
    private readonly HashSet<ulong> _blockedHosts = [];
    private readonly Dictionary<ulong, int> _hostCategory = [];
    public int BlockedHostCount => _blockedHosts.Count;

    private readonly Dictionary<string, List<Rule>> _block = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Rule>> _allow = new(StringComparer.Ordinal);
    private readonly List<Rule> _blockLoose = [];
    private readonly List<Rule> _allowLoose = [];

    private readonly List<string> _genericHide = [];
    private readonly Dictionary<string, List<string>> _specificHide = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _unhide = new(StringComparer.Ordinal);
    private string[] _genericCss = [];
    private readonly Dictionary<string, List<string>> _byClass = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _byId = new(StringComparer.Ordinal);

    public int RuleCount { get; private set; }
    public int CosmeticCount => _genericHide.Count + _byClass.Values.Sum(v => v.Count) + _byId.Values.Sum(v => v.Count) + _specificHide.Values.Sum(v => v.Count);

    /// <summary>Hiding rules that apply to every site are only used up to this many, to keep pages light.</summary>
    public int GenericCosmeticLimit { get; set; } = 3000;

    /// <summary>Keep each rule's original text (for showing which rule fired). Off by default: it costs memory.</summary>
    public bool KeepRuleText { get; set; }

    // ---- Loading ----

    /// <summary>Adds the rules in a filter list. <paramref name="category"/> says what they block (0 ads, 1 trackers).</summary>
    public void AddList(string text, int category = 0)
    {
        var cat = category;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 2) continue;
            if (line[0] == '!')
            {
                // Vireo's own lists may switch category part-way: "!#category trackers".
                if (line.StartsWith("!#category ", StringComparison.Ordinal)) cat = line[11..].Trim() == "trackers" ? 1 : 0;
                continue;
            }
            if (line[0] == '[') continue; // [Adblock Plus 2.0]
            if (TryHostRule(line, cat)) continue;
            if (!TryCosmetic(line)) TryNetwork(line, cat);
        }
        _genericCss = BuildGenericCss();
    }

    private bool TryCosmetic(string line)
    {
        var hide = line.IndexOf("##", StringComparison.Ordinal);
        var show = line.IndexOf("#@#", StringComparison.Ordinal);
        if (hide < 0 && show < 0) return line.Contains("#?#") || line.Contains("#$#") || line.Contains("#%#"); // extended syntax: ignore
        var isException = show >= 0 && (hide < 0 || show < hide);
        var at = isException ? show : hide;
        var domains = line[..at];
        var selector = line[(at + (isException ? 3 : 2))..].Trim();
        if (selector.Length == 0 || !SupportedSelector(selector)) return true;

        string[] include = [];
        if (domains.Length > 0)
        {
            var all = domains.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (all.Any(d => d.StartsWith('~'))) return true; // "everywhere except" rules are skipped: safer than hiding too much
            include = all;
        }

        if (isException)
        {
            foreach (var d in include.Length == 0 ? [""] : include)
            {
                if (!_unhide.TryGetValue(d, out var set)) _unhide[d] = set = new(StringComparer.Ordinal);
                set.Add(selector);
            }
        }
        else if (include.Length == 0) AddGeneric(selector);
        else
            foreach (var d in include)
            {
                if (!_specificHide.TryGetValue(d, out var list)) _specificHide[d] = list = [];
                list.Add(selector);
            }
        RuleCount++;
        return true;
    }

    /// <summary>Plain ".name" and "#name" rules are looked up by the names a page really uses; the rest are sent to every page.</summary>
    private void AddGeneric(string selector)
    {
        if (PlainSelector(selector))
        {
            var table = selector[0] == '.' ? _byClass : _byId;
            var name = selector[1..];
            if (!table.TryGetValue(name, out var list)) table[name] = list = [];
            list.Add(selector);
        }
        else _genericHide.Add(selector);
    }

    private static bool PlainSelector(string s) => s.Length > 1 && (s[0] is '.' or '#') && s.Skip(1).All(c => IsWord(c) || c is '-' or '_');

    private static bool SupportedSelector(string s) =>
        !s.StartsWith("+js(", StringComparison.Ordinal) && !s.Contains(":has-text(") && !s.Contains(":contains(") && !s.Contains(":-abp-") && !s.Contains(":xpath(")
        && !s.Contains(":matches-css") && !s.Contains(":upward(") && !s.Contains(":remove(") && !s.Contains(":style(") && !s.Contains(":not(:") && !s.Contains('{') && !s.Contains('}');

    private string[] BuildGenericCss() => BuildGenericCssWithout(_unhide.TryGetValue("", out var u) ? u : null);

    private string[] BuildGenericCssWithout(HashSet<string>? unhidden)
    {
        var picked = _genericHide.Where(s => unhidden is null || !unhidden.Contains(s)).OrderBy(s => s.Length).Take(GenericCosmeticLimit).ToList();
        var css = new List<string>();
        for (var i = 0; i < picked.Count; i += 400)
            css.Add(string.Join(',', picked.Skip(i).Take(400)) + "{display:none!important}");
        return [.. css];
    }

    private void TryNetwork(string line, int category)
    {
        var rule = new Rule { Text = KeepRuleText ? line : "", Category = category };
        var pattern = line;
        if (pattern.StartsWith("@@", StringComparison.Ordinal)) { rule.Exception = true; pattern = pattern[2..]; }

        // Options come after the last "$", unless that "$" is part of a regular expression.
        var dollar = pattern.LastIndexOf('$');
        if (dollar >= 0 && !(pattern.StartsWith('/') && pattern.IndexOf('/', 1) is var close && close > dollar))
        {
            if (!ApplyOptions(rule, pattern[(dollar + 1)..])) return;
            pattern = pattern[..dollar];
        }

        if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
        {
            try { rule.Regex = new Regex(pattern[1..^1], (rule.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30)); }
            catch (ArgumentException) { return; }
        }
        else
        {
            if (pattern.StartsWith("||", StringComparison.Ordinal)) { rule.AnchorHost = true; pattern = pattern[2..]; }
            else if (pattern.StartsWith('|')) { rule.AnchorStart = true; pattern = pattern[1..]; }
            if (pattern.EndsWith('|')) { rule.AnchorEnd = true; pattern = pattern[..^1]; }
            if (pattern.StartsWith('*')) rule.AnchorHost = rule.AnchorStart = false;
            if (!rule.MatchCase) pattern = pattern.ToLowerInvariant();
            rule.Parts = pattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
            // A rule with nothing to match and no options would block everything.
            if (rule.Parts.Length == 0 && rule.Party == Party.Any && rule.Types == ResourceType.All && rule.Include is null) return;
        }

        Index(rule);
        RuleCount++;
    }

    private static bool ApplyOptions(Rule rule, string options)
    {
        ResourceType include = ResourceType.None, exclude = ResourceType.None;
        foreach (var raw in options.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var opt = raw.Trim().ToLowerInvariant();
            var negate = opt.StartsWith('~');
            if (negate) opt = opt[1..];
            switch (opt)
            {
                case "third-party" or "3p": rule.Party = negate ? Party.First : Party.Third; continue;
                case "first-party" or "1p": rule.Party = negate ? Party.Third : Party.First; continue;
                case "match-case": rule.MatchCase = true; continue;
                case "important": continue;
                case "script": Add(ResourceType.Script); continue;
                case "image": Add(ResourceType.Image); continue;
                case "stylesheet" or "css": Add(ResourceType.Stylesheet); continue;
                case "font": Add(ResourceType.Font); continue;
                case "media": Add(ResourceType.Media); continue;
                case "xmlhttprequest" or "xhr": Add(ResourceType.XmlHttpRequest); continue;
                case "subdocument" or "frame": Add(ResourceType.Subdocument); continue;
                case "ping": Add(ResourceType.Ping); continue;
                case "websocket": Add(ResourceType.WebSocket); continue;
                case "other": Add(ResourceType.Other); continue;
                case "object": Add(ResourceType.Other); continue;
                case "popup": Add(ResourceType.Popup); continue;
                case "document" or "doc": Add(ResourceType.Document); continue;
            }
            if (opt.StartsWith("domain=", StringComparison.Ordinal))
            {
                var inc = new List<string>();
                var exc = new List<string>();
                foreach (var d in opt[7..].Split('|', StringSplitOptions.RemoveEmptyEntries))
                    (d.StartsWith('~') ? exc : inc).Add(d.TrimStart('~'));
                rule.Include = inc.Count > 0 ? [.. inc] : null;
                rule.Exclude = exc.Count > 0 ? [.. exc] : null;
                continue;
            }
            return false; // popup, redirect=, csp=, removeparam, document, elemhide...: not something this engine does, so skip the rule

            void Add(ResourceType t) { if (negate) exclude |= t; else include |= t; }
        }
        if (include != ResourceType.None) rule.Types = include;
        rule.Types &= ~exclude;
        return rule.Types != ResourceType.None;
    }

    // ---- Index: each rule is filed under one word from its pattern ----

    private void Index(Rule rule)
    {
        var token = rule.Regex is null ? BestToken(rule)?.ToLowerInvariant() : null; // looked up in lower case, whatever the rule's own case
        var table = rule.Exception ? _allow : _block;
        if (token is null) { (rule.Exception ? _allowLoose : _blockLoose).Add(rule); return; }
        if (!table.TryGetValue(token, out var list)) table[token] = list = [];
        list.Add(rule);
    }

    /// <summary>The longest run of letters and digits that a matching address must contain whole.</summary>
    private static string? BestToken(Rule r)
    {
        string? best = null;
        for (var p = 0; p < r.Parts.Length; p++)
        {
            var part = r.Parts[p];
            for (var i = 0; i < part.Length;)
            {
                if (!IsWord(part[i])) { i++; continue; }
                var j = i;
                while (j < part.Length && IsWord(part[j])) j++;
                // A word touching a wildcard or an open edge could be only part of a longer word in the address.
                var startsClean = i > 0 || (p == 0 && (r.AnchorStart || r.AnchorHost));
                var endsClean = j < part.Length || (p == r.Parts.Length - 1 && r.AnchorEnd);
                if (startsClean && endsClean && j - i >= 3 && (best is null || j - i > best.Length)) best = part[i..j];
                i = j;
            }
        }
        return best;
    }

    private static bool IsWord(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
    private static bool IsSeparator(char c) => !(IsWord(c) || c is '_' or '-' or '.' or '%');

    // ---- Asking ----

    /// <summary>Whether a request should be stopped. <paramref name="documentUrl"/> is the page that made it.</summary>
    public Verdict Check(string url, string documentUrl, ResourceType type)
    {
        if (RuleCount == 0 || url.Length < 4) return default;
        var lower = url.ToLowerInvariant();
        var host = HostOf(lower);
        var docHost = HostOf(documentUrl.ToLowerInvariant());
        var third = docHost.Length > 0 && !SameSite(host, docHost);

        var hit = Find(_block, _blockLoose, url, lower, host, docHost, third, type);
        var hostHit = hit is null && type != ResourceType.Document ? BlockedHost(host, docHost) : null;
        if (hit is null && hostHit is null) return default;
        if (Find(_allow, _allowLoose, url, lower, host, docHost, third, type) is not null) return default;
        return hit is not null ? new Verdict(true, hit.Text, hit.Category) : new Verdict(true, "||" + host + "^", hostHit!.Value);
    }

    /// <summary>The host, or a domain it belongs to, is on a host list. A page's own site is never refused this way.</summary>
    private int? BlockedHost(string host, string docHost)
    {
        if (_blockedHosts.Count == 0 || host.Length == 0) return null;
        if (docHost.Length > 0 && SameSite(host, docHost)) return null;
        for (var h = host; ; )
        {
            var key = HostHash(h);
            if (_blockedHosts.Contains(key)) return _hostCategory.TryGetValue(key, out var c) ? c : 1;
            var dot = h.IndexOf('.');
            if (dot < 0 || h.IndexOf('.', dot + 1) < 0) return null; // stop before the bare top-level part ("com")
            h = h[(dot + 1)..];
        }
    }

    private static ulong HostHash(string host)
    {
        // FNV-1a, 64 bits: collisions among a few hundred thousand hosts are vanishingly unlikely.
        var hash = 14695981039346656037UL;
        foreach (var c in host) { hash ^= c; hash *= 1099511628211UL; }
        return hash;
    }

    /// <summary>"||host^" with nothing after it: a whole host, filed in the compact set.</summary>
    private bool TryHostRule(string line, int category)
    {
        if (line.Length < 6 || !line.StartsWith("||", StringComparison.Ordinal)) return false;
        var end = line.EndsWith("^|", StringComparison.Ordinal) ? line.Length - 2 : line.EndsWith('^') ? line.Length - 1 : -1;
        if (end <= 2) return false;
        var host = line[2..end];
        if (!host.Contains('.')) return false;
        foreach (var ch in host) if (!(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_')) return false;
        var key = HostHash(host);
        if (_blockedHosts.Add(key)) { if (category != 1) _hostCategory[key] = category; RuleCount++; }
        return true;
    }

    private Rule? Find(Dictionary<string, List<Rule>> table, List<Rule> loose, string url, string lower, string host, string docHost, bool third, ResourceType type)
    {
        for (var i = 0; i < lower.Length;)
        {
            if (!IsWord(lower[i])) { i++; continue; }
            var j = i;
            while (j < lower.Length && IsWord(lower[j])) j++;
            if (j - i >= 3 && table.TryGetValue(lower[i..j], out var list))
                foreach (var r in list)
                    if (Matches(r, url, lower, host, docHost, third, type)) return r;
            i = j;
        }
        foreach (var r in loose)
            if (Matches(r, url, lower, host, docHost, third, type)) return r;
        return null;
    }

    private static bool Matches(Rule r, string url, string lower, string host, string docHost, bool third, ResourceType type)
    {
        if ((r.Types & type) == 0) return false;
        if (r.Party == Party.Third && !third) return false;
        if (r.Party == Party.First && third) return false;
        if (r.Include is not null && !r.Include.Any(d => InDomain(docHost, d))) return false;
        if (r.Exclude is not null && r.Exclude.Any(d => InDomain(docHost, d))) return false;
        if (r.Regex is not null)
        {
            try { return r.Regex.IsMatch(url); } catch (RegexMatchTimeoutException) { return false; }
        }
        return MatchPattern(r, r.MatchCase ? url : lower);
    }

    private static bool InDomain(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    private static bool MatchPattern(Rule r, string s)
    {
        if (r.Parts.Length == 0) return true;
        if (r.AnchorHost)
        {
            var hostStart = s.IndexOf("://", StringComparison.Ordinal);
            hostStart = hostStart < 0 ? 0 : hostStart + 3;
            var hostEnd = s.IndexOfAny(['/', '?', '#'], hostStart);
            if (hostEnd < 0) hostEnd = s.Length;
            if (MatchFrom(r, s, 0, hostStart, anchored: true)) return true;
            for (var i = hostStart; i < hostEnd; i++)
                if (s[i] == '.' && MatchFrom(r, s, 0, i + 1, anchored: true)) return true;
            return false;
        }
        return MatchFrom(r, s, 0, 0, anchored: r.AnchorStart);
    }

    private static bool MatchFrom(Rule r, string s, int partIndex, int from, bool anchored)
    {
        var part = r.Parts[partIndex];
        var last = partIndex == r.Parts.Length - 1;
        for (var i = from; i <= s.Length; i++)
        {
            var end = TestAt(s, i, part);
            if (end >= 0)
            {
                if (last ? !r.AnchorEnd || end == s.Length : MatchFrom(r, s, partIndex + 1, end, anchored: false)) return true;
            }
            if (anchored) return false;
        }
        return false;
    }

    /// <summary>Where the part ends if it matches at position i, or -1. "^" matches one separator, or the end of the address.</summary>
    private static int TestAt(string s, int i, string part)
    {
        var p = i;
        foreach (var c in part)
        {
            if (c == '^')
            {
                if (p == s.Length) continue;
                if (!IsSeparator(s[p])) return -1;
                p++;
            }
            else
            {
                if (p >= s.Length || s[p] != c) return -1;
                p++;
            }
        }
        return p;
    }

    private static string HostOf(string url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal);
        start = start < 0 ? 0 : start + 3;
        var end = url.IndexOfAny(['/', '?', '#'], start);
        var authority = end < 0 ? url[start..] : url[start..end];
        var at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        if (authority.StartsWith('[')) { var b = authority.IndexOf(']'); return b > 0 ? authority[..(b + 1)] : authority; }
        var colon = authority.IndexOf(':');
        return colon >= 0 ? authority[..colon] : authority;
    }

    private static readonly HashSet<string> SecondLevel = ["co", "com", "org", "net", "gov", "edu", "ac", "or", "ne", "go"];

    /// <summary>The part of a host name that one owner controls: "cdn.example.co.uk" gives "example.co.uk".</summary>
    public static string SiteOf(string host)
    {
        if (host.Length == 0 || host.StartsWith('[') || char.IsDigit(host[^1])) return host; // an address, not a name
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        var take = labels[^1].Length == 2 && SecondLevel.Contains(labels[^2]) ? 3 : 2;
        return string.Join('.', labels[^Math.Min(take, labels.Length)..]);
    }

    public static bool SameSite(string a, string b) => SiteOf(a) == SiteOf(b);

    // ---- Hiding page elements ----

    /// <summary>
    /// Style rules to apply when a page starts: the ones written for this site, and the few general ones that
    /// cannot be looked up by name. The rest come from <see cref="CssForNames"/> once the page says what it contains.
    /// </summary>
    public string CosmeticCss(string host)
    {
        host = host.ToLowerInvariant();
        var sb = new StringBuilder();
        var unhide = new HashSet<string>(StringComparer.Ordinal);
        if (_unhide.TryGetValue("", out var all)) unhide.UnionWith(all);
        var labels = host.Split('.');
        for (var i = 0; i < labels.Length - 1; i++)
        {
            var domain = string.Join('.', labels[i..]);
            if (_unhide.TryGetValue(domain, out var u)) unhide.UnionWith(u);
        }
        foreach (var chunk in unhide.Count == 0 ? _genericCss : BuildGenericCssWithout(unhide)) sb.Append(chunk);
        for (var i = 0; i < labels.Length - 1; i++)
        {
            var domain = string.Join('.', labels[i..]);
            if (_specificHide.TryGetValue(domain, out var list))
            {
                var keep = list.Where(s => !unhide.Contains(s)).ToList();
                if (keep.Count > 0) sb.Append(string.Join(',', keep)).Append("{display:none!important}");
            }
        }
        return sb.ToString();
    }

    /// <summary>The hiding rules for the class names and ids a page uses, as one style sheet.</summary>
    public string CssForNames(IEnumerable<string> classes, IEnumerable<string> ids, string host)
    {
        HashSet<string> unhide = [];
        if (_unhide.TryGetValue("", out var all)) unhide.UnionWith(all);
        var labels = host.ToLowerInvariant().Split('.');
        for (var i = 0; i < labels.Length - 1; i++)
            if (_unhide.TryGetValue(string.Join('.', labels[i..]), out var u)) unhide.UnionWith(u);

        List<string> picked = [];
        foreach (var c in classes) if (_byClass.TryGetValue(c, out var l)) picked.AddRange(l.Where(s => !unhide.Contains(s)));
        foreach (var id in ids) if (_byId.TryGetValue(id, out var l)) picked.AddRange(l.Where(s => !unhide.Contains(s)));
        return picked.Count == 0 ? "" : string.Join(',', picked) + "{display:none!important}";
    }
}
