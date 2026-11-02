using System.IO;
using Vireo.Services;
using Xunit;

namespace Vireo.Tests;

public class SessionTests
{
    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("http://127.0.0.1:8080/", true)]
    [InlineData("file:///C:/docs/a.pdf", true)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("view-source:https://example.com", false)]
    [InlineData("data:text/html,hi", false)]
    public void Only_real_pages_are_brought_back(string? url, bool expected) => Assert.Equal(expected, SessionStore.Restorable(url));

    [Fact]
    public void The_active_tab_is_followed_even_when_other_tabs_are_left_out()
    {
        var state = SessionStore.Snapshot(
        [
            ("about:blank", "", false),
            ("https://a.test/", "A", true),
            ("", "Start", false),
            ("https://b.test/", "B", false),
        ], active: 3);
        Assert.Equal(["https://a.test/", "https://b.test/"], state.Tabs.Select(t => t.Url));
        Assert.Equal(1, state.Active);
        Assert.True(state.Tabs[0].Pinned);
    }

    [Fact]
    public void An_active_tab_that_is_not_kept_falls_back_to_the_first()
    {
        var state = SessionStore.Snapshot([("https://a.test/", "A", false), ("about:blank", "", false)], active: 1);
        Assert.Equal(0, state.Active);
    }

    [Fact]
    public void A_huge_session_is_capped()
    {
        var state = SessionStore.Snapshot(Enumerable.Range(0, 500).Select(i => ($"https://x.test/{i}", "", false)), 0);
        Assert.Equal(SessionStore.MaxTabs, state.Tabs.Count);
    }

    [Fact]
    public void A_session_survives_a_round_trip_through_the_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), "vireo-session-" + Guid.NewGuid().ToString("N")[..8], "session.json");
        try
        {
            var state = SessionStore.Snapshot([("https://a.test/", "Ä title", true), ("https://b.test/", "B", false)], 1);
            SessionStore.Save(path, state);
            var loaded = SessionStore.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(state.Tabs, loaded!.Tabs);
            Assert.Equal(1, loaded.Active);
            Assert.False(File.Exists(path + ".tmp"));
            SessionStore.Delete(path);
            Assert.Null(SessionStore.Load(path));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { /* gone */ } }
    }

    [Fact]
    public void A_damaged_or_hostile_file_gives_no_session_instead_of_a_crash()
    {
        var path = Path.Combine(Path.GetTempPath(), "vireo-bad-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Null(SessionStore.Load(path));
            File.WriteAllText(path, """{"Tabs":[{"Url":"javascript:alert(1)","Title":"x","Pinned":false},{"Url":"https://ok.test/","Title":"ok","Pinned":false}],"Active":99}""");
            var loaded = SessionStore.Load(path);
            Assert.Equal(["https://ok.test/"], loaded!.Tabs.Select(t => t.Url)); // a script address is never restored
            Assert.Equal(0, loaded.Active);
        }
        finally { File.Delete(path); }
    }
}
