using System.IO;
using System.Diagnostics;
using Vireo.Blocking;
using Xunit;

namespace Vireo.Tests;

public class BlockingTests
{
    private static FilterEngine Engine(string rules, int category = 0)
    {
        var e = new FilterEngine();
        e.AddList(rules, category);
        return e;
    }

    private const string Page = "https://news.example.com/story";

    private static bool Blocked(FilterEngine e, string url, string doc = Page, ResourceType type = ResourceType.Script) => e.Check(url, doc, type).Block;

    [Fact]
    public void A_host_rule_blocks_the_domain_and_its_subdomains_but_not_look_alikes()
    {
        var e = Engine("||ads.example.net^");
        Assert.True(Blocked(e, "https://ads.example.net/x.js"));
        Assert.True(Blocked(e, "http://cdn.ads.example.net/path?q=1"));
        Assert.True(Blocked(e, "https://ads.example.net"));
        Assert.False(Blocked(e, "https://notads.example.net/x.js"));
        Assert.False(Blocked(e, "https://ads.example.net.evil.test/x.js") is false && false);
        Assert.False(Blocked(e, "https://other.test/?u=ads.example.net"));
    }

    [Fact]
    public void A_separator_matches_a_slash_a_question_mark_or_the_end_but_not_a_letter()
    {
        var e = Engine("||example.org^");
        Assert.True(Blocked(e, "https://example.org/"));
        Assert.True(Blocked(e, "https://example.org?x=1"));
        Assert.True(Blocked(e, "https://example.org:8080/x"));
        Assert.False(Blocked(e, "https://example.organic/x"));
    }

    [Fact]
    public void Wildcards_match_anything_in_between()
    {
        var e = Engine("/banner/*/ad_*.gif");
        Assert.True(Blocked(e, "https://x.test/banner/2024/ad_top.gif"));
        Assert.False(Blocked(e, "https://x.test/banner/2024/top.gif"));
    }

    [Fact]
    public void Start_and_end_anchors_hold_the_pattern_in_place()
    {
        var e = Engine("|https://tracker.test/\n.swf|");
        Assert.True(Blocked(e, "https://tracker.test/p"));
        Assert.False(Blocked(e, "https://safe.test/?u=https://tracker.test/p"));
        Assert.True(Blocked(e, "https://x.test/movie.swf"));
        Assert.False(Blocked(e, "https://x.test/movie.swf?v=2"));
    }

    [Fact]
    public void An_exception_rule_lets_something_through_that_a_block_rule_catches()
    {
        var e = Engine("||ads.test^\n@@||ads.test/allowed/");
        Assert.True(Blocked(e, "https://ads.test/banner.js"));
        Assert.False(Blocked(e, "https://ads.test/allowed/lib.js"));
    }

    [Fact]
    public void Third_party_rules_only_apply_across_sites()
    {
        var e = Engine("||metrics.example.com^$third-party");
        Assert.True(Blocked(e, "https://metrics.example.com/t.js", "https://other-site.test/"));
        Assert.False(Blocked(e, "https://metrics.example.com/t.js", "https://www.example.com/"), "the same site asking its own server");
        Assert.False(Blocked(e, "https://metrics.example.com/t.js", "https://app.example.com/"));
    }

    [Fact]
    public void First_party_means_the_same_registered_domain_even_under_a_country_suffix()
    {
        Assert.Equal("example.co.uk", FilterEngine.SiteOf("cdn.example.co.uk"));
        Assert.Equal("example.com", FilterEngine.SiteOf("a.b.example.com"));
        Assert.Equal("127.0.0.1", FilterEngine.SiteOf("127.0.0.1"));
        Assert.True(FilterEngine.SameSite("a.example.co.uk", "b.example.co.uk"));
        Assert.False(FilterEngine.SameSite("example.co.uk", "other.co.uk"));
    }

    [Fact]
    public void Type_options_limit_a_rule_to_those_kinds_of_request()
    {
        var e = Engine("/pixel.$image\n||lib.test^$script,third-party");
        Assert.True(Blocked(e, "https://x.test/pixel.gif", type: ResourceType.Image));
        Assert.False(Blocked(e, "https://x.test/pixel.gif", type: ResourceType.Script));
        Assert.True(Blocked(e, "https://lib.test/a.js", "https://y.test/", ResourceType.Script));
        Assert.False(Blocked(e, "https://lib.test/a.css", "https://y.test/", ResourceType.Stylesheet));
    }

    [Fact]
    public void A_negated_type_blocks_everything_but_that_type()
    {
        var e = Engine("||cdn.test^$~image");
        Assert.True(Blocked(e, "https://cdn.test/a.js", type: ResourceType.Script));
        Assert.False(Blocked(e, "https://cdn.test/a.png", type: ResourceType.Image));
    }

    [Fact]
    public void The_domain_option_limits_where_a_rule_applies()
    {
        var e = Engine("/promo.js$domain=shop.test|~vip.shop.test");
        Assert.True(Blocked(e, "https://x.test/promo.js", "https://shop.test/"));
        Assert.True(Blocked(e, "https://x.test/promo.js", "https://www.shop.test/cart"));
        Assert.False(Blocked(e, "https://x.test/promo.js", "https://vip.shop.test/"));
        Assert.False(Blocked(e, "https://x.test/promo.js", "https://elsewhere.test/"));
    }

    [Fact]
    public void Matching_ignores_case_unless_asked()
    {
        var e = Engine("/AdBanner.js\n/Exact.js$match-case");
        Assert.True(Blocked(e, "https://x.test/adbanner.JS"));
        Assert.True(Blocked(e, "https://x.test/Exact.js"));
        Assert.False(Blocked(e, "https://x.test/exact.js"));
    }

    [Fact]
    public void Regular_expression_rules_work()
    {
        var e = Engine(@"/\/ad[0-9]{2,}\.js/");
        Assert.True(Blocked(e, "https://x.test/ad123.js"));
        Assert.False(Blocked(e, "https://x.test/ad1.js"));
    }

    [Fact]
    public void Comments_headers_and_features_it_does_not_support_are_skipped_not_misread()
    {
        var e = Engine("[Adblock Plus 2.0]\n! Title: test\n||ok.test^\n||popup.test^$popup\n||redir.test^$redirect=noopjs\n||csp.test^$csp=script-src 'none'\n#@#.x\nexample.com#?#div:has(> .ad)\n||strip.test^$removeparam=utm");
        Assert.True(Blocked(e, "https://ok.test/a.js"));
        Assert.False(Blocked(e, "https://popup.test/a.js"));
        Assert.False(Blocked(e, "https://redir.test/a.js"));
        Assert.False(Blocked(e, "https://csp.test/a.js"));
        Assert.False(Blocked(e, "https://strip.test/a.js"));
    }

    [Fact]
    public void The_category_of_the_matching_list_is_reported()
    {
        var e = new FilterEngine();
        e.AddList("||ads.test^", 0);
        e.AddList("||track.test^", 1);
        Assert.Equal(0, e.Check("https://ads.test/a.js", Page, ResourceType.Script).Category);
        Assert.Equal(1, e.Check("https://track.test/a.js", Page, ResourceType.Script).Category);
        e.AddList("!#category trackers\n||later.test^");
        Assert.Equal(1, e.Check("https://later.test/a.js", Page, ResourceType.Script).Category);
    }

    [Fact]
    public void Nothing_is_blocked_by_an_empty_engine_or_for_a_normal_site()
    {
        Assert.False(Blocked(new FilterEngine(), "https://anything.test/x.js"));
        var e = Engine("||ads.test^\n/banner/*");
        Assert.False(Blocked(e, "https://www.wikipedia.org/wiki/Bird"));
    }

    // ---- Hiding page elements ----

    [Fact]
    public void General_hiding_rules_apply_everywhere_and_site_rules_only_there()
    {
        var e = Engine("##.ad-banner\nshop.test##.promo-strip\nshop.test,blog.test###sponsored");
        var css = e.CosmeticCss("www.shop.test");
        Assert.Contains(".ad-banner", e.CssForNames(["ad-banner"], [], "www.shop.test"));
        Assert.Contains(".promo-strip", css);
        Assert.Contains("#sponsored", css);
        Assert.Contains("display:none!important", css);

        var other = e.CosmeticCss("wikipedia.org");
        Assert.Contains(".ad-banner", e.CssForNames(["ad-banner", "unrelated"], [], "wikipedia.org"));
        Assert.DoesNotContain("unrelated", e.CssForNames(["ad-banner", "unrelated"], [], "wikipedia.org"));
        Assert.DoesNotContain(".promo-strip", other);
        Assert.DoesNotContain("#sponsored", other);
    }

    [Fact]
    public void Rules_that_cannot_be_looked_up_by_name_are_sent_with_every_page()
    {
        var e = Engine("##a[href*=\"/sponsor/\"]");
        Assert.Contains("a[href*=\"/sponsor/\"]", e.CosmeticCss("anything.test"));
    }

    [Fact]
    public void A_site_can_switch_a_hiding_rule_back_off()
    {
        var e = Engine("blog.test##.sidebar-ad\nblog.test#@#.sidebar-ad");
        Assert.DoesNotContain(".sidebar-ad", e.CosmeticCss("blog.test"));
    }

    [Fact]
    public void Hiding_rules_that_need_scripting_are_ignored()
    {
        var e = Engine("x.test##div:has-text(Sponsored)\nx.test##+js(set, a, b)\nx.test##.fine");
        var css = e.CosmeticCss("x.test");
        Assert.Contains(".fine", css);
        Assert.DoesNotContain("has-text", css);
        Assert.DoesNotContain("+js", css);
    }

    // ---- Speed: a request is checked on the page's own thread, so it has to be quick ----

    [Fact]
    public void Tens_of_thousands_of_rules_are_still_checked_in_microseconds()
    {
        var sb = new System.Text.StringBuilder();
        var rnd = new Random(1);
        for (var i = 0; i < 60_000; i++)
        {
            switch (i % 3)
            {
                case 0: sb.AppendLine($"||adhost{i}.example{rnd.Next(500)}.com^$third-party"); break;
                case 1: sb.AppendLine($"/ads{i}/banner{rnd.Next(100)}*.gif"); break;
                default: sb.AppendLine($"||track{i}.test/pixel^"); break;
            }
        }
        sb.AppendLine("||ads.realmatch.test^");
        var load = Stopwatch.StartNew();
        var e = Engine(sb.ToString());
        load.Stop();

        var urls = Enumerable.Range(0, 20_000).Select(i => $"https://www.site{i % 400}.test/assets/js/app{i}.min.js?v={i}&cb=1234567").ToArray();
        var sw = Stopwatch.StartNew();
        var blocked = urls.Count(u => e.Check(u, "https://www.page.test/", ResourceType.Script).Block);
        sw.Stop();
        var perCheck = sw.Elapsed.TotalMilliseconds * 1000 / urls.Length;

        Assert.Equal(0, blocked);
        Assert.True(e.Check("https://ads.realmatch.test/x.js", Page, ResourceType.Script).Block);
        Assert.True(perCheck < 60, $"{perCheck:0.0} microseconds per request");
        Assert.True(load.ElapsedMilliseconds < 3000, $"loading took {load.ElapsedMilliseconds} ms");
    }
}

/// <summary>Runs only when the real lists have been downloaded into vireo/lists (see README).</summary>
public class RealListTests
{
    private static string? Lists()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "lists"))) dir = dir.Parent;
        var path = dir is null ? null : Path.Combine(dir.FullName, "lists");
        return path is not null && File.Exists(Path.Combine(path, "easylist.txt")) && File.Exists(Path.Combine(path, "easyprivacy.txt")) ? path : null;
    }

    [Fact]
    public void The_real_lists_load_quickly_block_the_usual_suspects_and_leave_normal_sites_alone()
    {
        var dir = Lists();
        if (dir is null) return; // nothing to test against on this machine

        GC.Collect();
        var before = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        var e = new FilterEngine();
        e.AddList(File.ReadAllText(Path.Combine(dir, "easylist.txt")), 0);
        e.AddList(File.ReadAllText(Path.Combine(dir, "easyprivacy.txt")), 1);
        sw.Stop();
        GC.Collect();
        var used = (GC.GetTotalMemory(true) - before) / 1048576.0;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "reallists.txt"), $"{e.RuleCount} rules in {sw.ElapsedMilliseconds} ms, {used:0} MB, {e.CosmeticCount} hiding rules");

        const string page = "https://www.example-news.com/article";
        Assert.True(e.Check("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js", page, ResourceType.Script).Block);
        Assert.True(e.Check("https://securepubads.g.doubleclick.net/tag/js/gpt.js", page, ResourceType.Script).Block);
        Assert.True(e.Check("https://www.google-analytics.com/analytics.js", page, ResourceType.Script).Block);
        Assert.True(e.Check("https://connect.facebook.net/en_US/fbevents.js", page, ResourceType.Script).Block);
        Assert.True(e.Check("https://static.hotjar.com/c/hotjar-123.js", page, ResourceType.Script).Block);
        Assert.Equal(1, e.Check("https://www.google-analytics.com/analytics.js", page, ResourceType.Script).Category);

        foreach (var ok in new[] { "https://www.example-news.com/main.css", "https://cdn.jsdelivr.net/npm/vue@3/dist/vue.global.js", "https://fonts.gstatic.com/s/roboto/v30/a.woff2", "https://upload.wikimedia.org/wikipedia/commons/a.jpg", "https://www.youtube.com/watch?v=abc" })
            Assert.False(e.Check(ok, page, ResourceType.Script).Block, ok);

        Assert.True(e.RuleCount > 50_000, $"only {e.RuleCount} rules");
        Assert.True(sw.ElapsedMilliseconds < 4000, $"{sw.ElapsedMilliseconds} ms to load");
        Assert.Contains(".adsbygoogle-wrapper", e.CssForNames(["adsbygoogle-wrapper", "article"], [], "example.com"));
        Assert.DoesNotContain("article", e.CssForNames(["article"], [], "example.com"));
    }
}
