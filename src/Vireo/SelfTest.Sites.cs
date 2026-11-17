using System.IO;
using System.Text.Json;
using Vireo.Media;
using Vireo.Services;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>Public video sites, as a person would use them: open the page, press play, see what Vireo offers. Only on request (needs the internet).</summary>
    // Name, page, the kind of link to follow from that page to a video (a person picks one), and the site's own play button.
    private static readonly (string Name, string Url, string? Follow, string? Play)[] Sites =
    [
        ("Vimeo", "https://vimeo.com/76979871", null, null),
        ("Vimeo 2", "https://vimeo.com/1084537", null, null),
        ("Dailymotion", "https://www.dailymotion.com/embed/video/x5kesuj", null, null),
        ("Dailymotion home", "https://www.dailymotion.com/", @"dailymotion\.com/video/[a-z0-9]{6,}$", null),
        ("Twitch live", "https://www.twitch.tv/directory/all", @"twitch\.tv/(?!directory|downloads|jobs|search|settings|turbo|store|prime|subscriptions|friends|inventory|wallet|drops|p/)[A-Za-z0-9_]{3,}$", null),
        ("Kick live", "https://kick.com/browse", @"kick\.com/(?!browse|categories|category|following|search|clips|terms|privacy|dmca|community|login|signup|events|help|careers|about|brand|safety|stream-?\w*)[A-Za-z0-9_-]{3,}$", null),
        ("Rumble", "https://rumble.com/v7iggak-america-first-ep.-1779.html", null, null),
        ("Rumble live", "https://rumble.com/v7i8vjy-watch-newsmax2-live-real-news-for-real-people.html", null, null),
        ("Bilibili", "https://www.bilibili.com/", @"bilibili\.com/(en/)?video/BV[0-9A-Za-z]{10}", null),
        ("archive.org", "https://archive.org/details/BigBuckBunny_124", null, null),
        ("TED", "https://www.ted.com/talks", @"ted\.com/talks/[a-z0-9_]{8,}$", null),
        ("PeerTube", "https://framatube.org/videos/browse?scope=local", @"/w/[A-Za-z0-9]{8,}$", null),
        ("Odysee", "https://odysee.com/$/discover?t=featured", @"odysee\.com/@[^/]+/[^/]+$", null),
        ("Streamable", "https://streamable.com/moo", null, null),
        ("SoundCloud", "https://soundcloud.com/forss/flickermood", null, ".sound__soundActions .playButton, .playButton, button[title='Play']"),
        ("Bandcamp", "https://bandcamp.com/", @"bandcamp\.com/(album|track)/", ".playbutton, .play-btn, button[aria-label='Play']"),
        ("TikTok", "https://www.tiktok.com/@scout2015/video/6718335390845095173", null, null),
        ("Facebook video", "https://www.facebook.com/watch/?v=10153231379946729", null, null),
        ("9GAG", "https://9gag.com/trending", null, null),
        ("Coub", "https://coub.com/hot", null, null),
        ("Bitchute", "https://www.bitchute.com/", @"/video/[A-Za-z0-9]{6,}/?$", null),
        ("CNN video", "https://edition.cnn.com/videos", @"/video/[a-z0-9-]{8,}", null),
        ("Euronews", "https://www.euronews.com/video", null, null),
        ("DW live TV", "https://www.dw.com/en/live-tv/channel-english", null, null),
        ("Vidyard demo", "https://www.vidyard.com/", null, null),
        ("Wistia demo", "https://wistia.com/", null, null),
        ("JW Player", "https://www.jwplayer.com/", null, null),
        ("Video.js", "https://videojs.com/", null, ".vjs-big-play-button"),
        ("hls.js demo", "https://hlsjs.video-dev.org/demo/", null, null),
        ("Shaka demo", "https://shaka-player-demo.appspot.com/demo/#audiolang=en;textlang=en;uilang=en;asset=https://storage.googleapis.com/shaka-demo-assets/angel-one/dash.mpd;panel=CUSTOM%20CONTENT;build=uncompiled", null, null),
        ("Bitmovin demo", "https://bitmovin.com/demos/stream-test", null, null),
        ("Reddit", "https://www.reddit.com/r/videos/", @"reddit\.com/r/videos/comments/", null),
        ("Kick channel", "https://kick.com/adinross", null, null),
        ("DM video", "https://www.dailymotion.com/video/x5kesuj", null, null),
        ("Twitter/X", "https://x.com/NASA", null, null),
    ];

    private async Task RunSiteSurveyAsync()
    {
        if (Environment.GetEnvironmentVariable("VIREO_SURVEY_NOBLOCK") == "1") { w.Settings.BlockAds = false; w.Settings.Commit(); }
        var only = Environment.GetEnvironmentVariable("VIREO_SITES");
        // One page given at run time (kept out of the list above): VIREO_SURVEY_URL=https://...
        var extra = Environment.GetEnvironmentVariable("VIREO_SURVEY_URL");
        var list = Sites.ToList();
        if (!string.IsNullOrEmpty(extra)) { list.Insert(0, ("Given page", extra, null, null)); only = only is null ? "Given" : only + ",Given"; }
        foreach (var (name, url, follow, play) in list)
        {
            if (only is not null && !only.Split(',').Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase))) continue;
            var line = await SurveyOneAsync(name, url, follow, play);
            Console.WriteLine("SITE  " + line);
        }
    }

    private async Task<string> SurveyOneAsync(string name, string url, string? follow, string? play)
    {
        var tab = await w.OpenTabAsync("about:blank");
        // VIREO_NETLOG=1: every answer the page gets (type, status, kind of request) and every request the blocker stops.
        var netlog = Environment.GetEnvironmentVariable("VIREO_NETLOG") == "1" ? new System.Collections.Concurrent.ConcurrentQueue<string>() : null;
        if (netlog is not null)
        {
            MainWindow.BlockedLog = l => netlog.Enqueue(l);
            tab.View!.CoreWebView2.WebResourceResponseReceived += (_, e) =>
            {
                try
                {
                    var type = e.Response.Headers.Contains("Content-Type") ? e.Response.Headers.GetHeader("Content-Type") : "";
                    netlog.Enqueue($"{e.Response.StatusCode} {type,-32} {e.Request.Uri}");
                }
                catch { }
            };
        }
        w.NavigateActive(url);
        await Task.Delay(500);
        try
        {
            await WaitFor(() => !tab.IsLoading, 30000);
            await Task.Delay(3000);
            await DismissAsync(tab);
            // A listing page: go to the first video on it, the way a person would pick one.
            if (follow is not null)
            {
                var pick = await w.EvalAsync(tab, $"(() => {{ const re = new RegExp({JsonSerializer.Serialize(follow)}, 'i'); const a = [...document.querySelectorAll('a[href]')].map(a => a.href.split('?')[0].split('#')[0]).find(h => re.test(h)); return a || ''; }})()");
                var next = JsonSerializer.Deserialize<string>(pick);
                if (!string.IsNullOrEmpty(next))
                {
                    w.NavigateActive(next);
                    await Task.Delay(1500);
                    await WaitFor(() => !tab.IsLoading, 30000);
                    await Task.Delay(3000);
                    await DismissAsync(tab);
                }
            }
            if (play is not null) await ClickAsync(tab, play);
            // Press play: a real click in the middle of the biggest video or player frame.
            var box = await w.EvalAsync(tab, """
                (() => {
                  const els = [...document.querySelectorAll('video, iframe, [class*=player], [id*=player]')].map(e => [e, e.getBoundingClientRect()]).filter(([e, r]) => r.width > 200 && r.height > 120 && r.top < innerHeight);
                  els.sort((a, b) => b[1].width * b[1].height - a[1].width * a[1].height);
                  document.querySelectorAll('video, audio').forEach(v => { try { v.muted = true; v.play().catch(() => {}); } catch {} });
                  if (!els.length) return null;
                  const r = els[0][1];
                  return JSON.stringify({ x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) });
                })()
                """);
            if (box != "null" && JsonSerializer.Deserialize<string>(box) is { } b && JsonSerializer.Deserialize<JsonElement>(b) is var p)
            {
                var core = tab.View!.CoreWebView2;
                foreach (var type in new[] { "mousePressed", "mouseReleased" })
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = p.GetProperty("x").GetInt32(), y = p.GetProperty("y").GetInt32(), button = "left", clickCount = 1 }));
            }
            // Give the player time to ask for its stream, then for Vireo to read it.
            for (var i = 0; i < 25 && !(tab.Media.Any(m => m.Status == MediaStatus.Ready) && tab.Media.All(m => m.Status != MediaStatus.Probing)); i++) await Task.Delay(1000);
            var found = tab.Media.Where(m => !(m.Quiet && m.Status == MediaStatus.Failed)).ToList();
            var summary = found.Count == 0 ? "NOTHING FOUND"
                : string.Join("\n      ", found.Take(5).Select(m =>
                    $"{m.Kind}/{m.Status}{(m.Live ? "/live" : "")} best={m.Choices.FirstOrDefault(c => !c.AudioOnly)?.Label ?? m.Choices.FirstOrDefault()?.Label ?? "-"} n={m.Choices.Count} size={m.SizeBytes}{(m.Error is { } e ? " err=" + e : "")} {m.Url}"));
            // The Download button on the page's own player, as a person presses it.
            await w.EvalAsync(tab, "window.__vireoTest && window.__vireoTest.hover()");
            await Task.Delay(600);
            await w.EvalAsync(tab, "window.__vireoTest && window.__vireoTest.press()");
            for (var i = 0; i < 20; i++)
            {
                var st = await OverlayState(tab);
                if (st.GetProperty("rows").GetArrayLength() > 0 || (st.TryGetProperty("note", out var n0) && n0.GetString() is { Length: > 0 })) break;
                await Task.Delay(250);
            }
            var menuState = await OverlayState(tab);
            summary += $"\n      BUTTON {menuState.GetProperty("buttons").GetInt32()} shown; menu: {(menuState.GetProperty("rows").GetArrayLength() > 0 ? string.Join(" | ", menuState.GetProperty("rows").EnumerateArray().Select(r => r.GetString())) : (menuState.TryGetProperty("note", out var n1) ? n1.GetString() : "(no menu)"))}";
            await Shot("site-" + new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant());
            // Optionally prove it downloads: the smallest quality of the first finished (not live) video, to the end, then checked.
            if (Environment.GetEnvironmentVariable("VIREO_SURVEY_DOWNLOAD") == "1" && found.FirstOrDefault(m => m.Status == MediaStatus.Ready && !m.Live && m.Choices.Count > 0) is { } item)
            {
                // Into the test's own folder, never the person's Downloads.
                var folder = Path.Combine(outDir, "site-downloads");
                Directory.CreateDirectory(folder);
                w.Settings.DownloadFolder = Path.GetFullPath(folder);
                w.Settings.Commit();
                summary += "\n      CHOICES " + string.Join(" | ", item.Choices.Select(c => $"{c.Label} {c.Detail}"));
                var choice = item.Choices.Where(c => !c.AudioOnly).OrderBy(c => c.SizeBytes ?? long.MaxValue).FirstOrDefault() ?? item.Choices[0];
                await w.StartChoiceAsync(tab, item, choice);
                var d = await Finish(i => i.Url == choice.Url || i.Stream?.PlaylistUrl == choice.Url || i.Pair?.VideoUrl == choice.Url, 180000);
                var probe = d is { State: Downloads.DownloadState.Completed } && FfprobeRuns() ? Probe(d.FinalPath) : null;
                summary += $"\n      DOWNLOAD {choice.Label}: {d?.State} {d?.Error} {(d is null ? "" : Format.Bytes(d.Total))} {(probe is { } pr ? $"plays {pr.Duration:0.0}s, {pr.Video} video, {pr.Audio} audio" : "")}";
            }
            if (netlog is not null)
            {
                await Task.Delay(1500);
                var file = Path.Combine(outDir, "netlog-" + new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant() + ".txt");
                File.WriteAllLines(file, netlog);
                var frames = await w.EvalAsync(tab, "JSON.stringify({videos:[...document.querySelectorAll('video')].map(v=>({src:v.currentSrc,paused:v.paused,ready:v.readyState,w:v.videoWidth,err:v.error&&v.error.code})),frames:[...document.querySelectorAll('iframe')].map(f=>f.src)})");
                summary += $"\n      PAGE {frames}\n      NETLOG {file} ({netlog.Count} lines)";
                MainWindow.BlockedLog = null;
            }
            return $"{name,-16} | {Short(tab.Url, 70)} | {summary}";
        }
        catch (Exception e) { return $"{name,-16} | {Short(url, 70)} | ERROR {e.Message}"; }
        finally { await w.CloseTabAsync(tab); }
    }

    /// <summary>Closes what stands between a person and the video: cookie banners, sign-up boxes (Escape, then their buttons).</summary>
    private async Task DismissAsync(Models.BrowserTab tab)
    {
        var core = tab.View!.CoreWebView2;
        foreach (var type in new[] { "keyDown", "keyUp" })
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key = "Escape", code = "Escape", windowsVirtualKeyCode = 27 }));
        await w.EvalAsync(tab, """
            (() => {
              const words = /^(accept( all)?( cookies)?|agree|i agree|i understand|got it|ok|start|allow( all)?|not now|maybe later|no thanks|close|dismiss|continue as guest|skip)$/i;
              for (const b of [...document.querySelectorAll('button, [role=button], a')]) {
                const t = (b.innerText || b.getAttribute('aria-label') || '').trim();
                if (t.length < 30 && words.test(t)) { try { b.click(); } catch {} }
              }
              // the little X of a dialog
              for (const b of [...document.querySelectorAll('[role=dialog] button, [class*=modal] button, [class*=dialog] button, [class*=popup] button')])
                if (/^(close|×|✕|x)?$/i.test((b.innerText || '').trim()) && /close/i.test((b.getAttribute('aria-label') || '') + ' ' + b.className)) { try { b.click(); } catch {} }
            })()
            """);
        await Task.Delay(800);
    }

    private async Task ClickAsync(Models.BrowserTab tab, string selector)
    {
        var box = await w.EvalAsync(tab, $"(() => {{ const e = document.querySelector({JsonSerializer.Serialize(selector)}); if (!e) return null; e.scrollIntoView({{ block: 'center' }}); const r = e.getBoundingClientRect(); return JSON.stringify({{ x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) }}); }})()");
        if (box == "null" || JsonSerializer.Deserialize<string>(box) is not { } b) return;
        var p = JsonSerializer.Deserialize<JsonElement>(b);
        var core = tab.View!.CoreWebView2;
        foreach (var type in new[] { "mousePressed", "mouseReleased" })
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = p.GetProperty("x").GetInt32(), y = p.GetProperty("y").GetInt32(), button = "left", clickCount = 1 }));
        await Task.Delay(1500);
    }

    private static string Short(string s, int n = 90) => s.Length <= n ? s : s[..n] + "…";
}
