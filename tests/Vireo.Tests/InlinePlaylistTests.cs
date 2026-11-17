using System.Net;
using System.Net.Http;
using Vireo.Media;
using Xunit;

namespace Vireo.Tests;

/// <summary>Playlists a page writes in script (blob: addresses): kept, made absolute, and served from memory.</summary>
public class InlinePlaylistTests
{
    private const string Page = "https://watch.example.com/ep/1";

    [Fact]
    public void A_playlist_made_in_script_gets_an_address_whose_pieces_point_at_the_page_site()
    {
        var address = InlinePlaylists.Add("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-KEY:METHOD=AES-128,URI=\"/keys/k1\"\n#EXTINF:4,\nseg/0.ts\n#EXTINF:4,\nhttps://cdn.example.net/x/1.ts\n#EXT-X-ENDLIST\n", Page, "blob:https://watch.example.com/aaa");
        Assert.NotNull(address);
        Assert.True(InlinePlaylists.IsInline(address!));
        var text = InlinePlaylists.TextAt(address!)!;
        Assert.Contains("https://watch.example.com/ep/seg/0.ts", text);
        Assert.Contains("https://cdn.example.net/x/1.ts", text);
        Assert.Contains("URI=\"https://watch.example.com/keys/k1\"", text);
    }

    [Fact]
    public void Text_that_is_not_a_playlist_is_not_kept()
    {
        Assert.Null(InlinePlaylists.Add("console.log('hi')", Page, "blob:x"));
        Assert.Null(InlinePlaylists.Add("", Page, null));
    }

    [Fact]
    public void A_live_playlist_rewritten_every_few_seconds_keeps_one_address_and_its_newest_text()
    {
        var a = InlinePlaylists.Add("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:10\n#EXTINF:4,\nhttps://live.example.org/s/10.ts\n", Page, "blob:l1");
        var b = InlinePlaylists.Add("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:11\n#EXTINF:4,\nhttps://live.example.org/s/11.ts\n", Page, "blob:l2");
        Assert.Equal(a, b);
        Assert.Contains("/s/11.ts", InlinePlaylists.TextAt(b!));
    }

    [Fact]
    public void A_master_made_before_its_variants_is_pointed_at_them_once_they_arrive()
    {
        var master = InlinePlaylists.Add("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=1280x720\nblob:https://watch.example.com/v720\n", Page, "blob:https://watch.example.com/master");
        var variant = InlinePlaylists.Add("#EXTM3U\n#EXTINF:4,\nhttps://cdn.example.net/720/0.ts\n#EXT-X-ENDLIST\n", Page, "blob:https://watch.example.com/v720");
        Assert.Contains(variant!, InlinePlaylists.TextAt(master!));
    }

    [Fact]
    public async Task The_handler_answers_from_memory_and_passes_everything_else_on()
    {
        var address = InlinePlaylists.Add("#EXTM3U\n#EXTINF:4,\nhttps://cdn.example.net/z/0.ts\n#EXT-X-ENDLIST\n", Page, null)!;
        var inner = new Recorder();
        using var http = new HttpClient(new InlinePlaylists.Handler(inner));
        using var res = await http.GetAsync(address);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("#EXTM3U", await res.Content.ReadAsStringAsync());
        Assert.Equal(0, inner.Calls);
        using var missing = await http.GetAsync($"http://{InlinePlaylists.Host}/nothing.m3u8");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var other = await http.GetAsync("https://example.com/a");
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task A_playlist_that_answers_only_once_is_served_from_the_copy_the_page_received()
    {
        const string url = "https://once.example.com/manifest/v1.m3u8?rid=42";
        InlinePlaylists.Remember(url, "#EXTM3U\n#EXTINF:4,\nhttps://cdn.example.com/0.ts\n#EXT-X-ENDLIST\n");
        using var http = new HttpClient(new InlinePlaylists.Handler(new Fixed(HttpStatusCode.NoContent, "")));
        using var res = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("#EXTM3U", await res.Content.ReadAsStringAsync());
        // A fresh answer wins when there is one (a live playlist grows)
        using var live = new HttpClient(new InlinePlaylists.Handler(new Fixed(HttpStatusCode.OK, "#EXTM3U\n#EXTINF:4,\nhttps://cdn.example.com/1.ts\n")));
        Assert.Contains("1.ts", await (await live.GetAsync(url)).Content.ReadAsStringAsync());
        // Text that is not a playlist is never kept
        InlinePlaylists.Remember("https://x.example/a.m3u8", "<html>nope</html>");
        Assert.False(InlinePlaylists.WasReceived("https://x.example/a.m3u8"));
    }

    private sealed class Fixed(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(body) });
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
