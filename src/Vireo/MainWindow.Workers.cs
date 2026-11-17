using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Vireo.Media;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

// Players that fetch their stream from a Web Worker (Twitch and Kick's IVS player, and others): the engine reports nothing a
// worker loads to the app, so Vireo attaches to the page's workers through the DevTools protocol and looks at their answers.
public partial class MainWindow
{
    private void WatchWorkers(BrowserTab tab, CoreWebView2 core)
    {
        var workers = new ConcurrentDictionary<string, string>(); // session -> worker type
        var pending = new ConcurrentDictionary<string, (string Url, string Session)>(); // request id -> answer that needs reading
        MainWindow O() => tab.Owner ?? this;

        var attached = core.GetDevToolsProtocolEventReceiver("Target.attachedToTarget");
        attached.DevToolsProtocolEventReceived += async (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                var session = doc.RootElement.GetProperty("sessionId").GetString() ?? "";
                var info = doc.RootElement.GetProperty("targetInfo");
                var type = info.GetProperty("type").GetString() ?? "";
                if (type is "worker" or "shared_worker" or "service_worker")
                {
                    workers[session] = type;
                    await core.CallDevToolsProtocolMethodForSessionAsync(session, "Network.enable", "{}");
                }
                else if (type == "iframe")
                {
                    // A player frame from another site runs in its own process: its workers are found the same way.
                    await core.CallDevToolsProtocolMethodForSessionAsync(session, "Target.setAutoAttach", """{"autoAttach":true,"waitForDebuggerOnStart":false,"flatten":true}""");
                }
                if (doc.RootElement.TryGetProperty("waitingForDebugger", out var w) && w.GetBoolean())
                    await core.CallDevToolsProtocolMethodForSessionAsync(session, "Runtime.runIfWaitingForDebugger", "{}");
            }
            catch { /* the worker ended already */ }
        };
        core.GetDevToolsProtocolEventReceiver("Target.detachedFromTarget").DevToolsProtocolEventReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                workers.TryRemove(doc.RootElement.GetProperty("sessionId").GetString() ?? "", out string? _);
            }
            catch { }
        };

        core.GetDevToolsProtocolEventReceiver("Network.responseReceived").DevToolsProtocolEventReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.SessionId) || !workers.ContainsKey(e.SessionId)) return; // pages are seen by the engine's own event
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                var response = doc.RootElement.GetProperty("response");
                var url = response.GetProperty("url").GetString() ?? "";
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || MediaClassifier.ClearlyNotMedia(url)) return;
                var status = response.GetProperty("status").GetInt32();
                string? Header(string name)
                {
                    if (!response.TryGetProperty("headers", out var h)) return null;
                    foreach (var p in h.EnumerateObject()) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value.GetString();
                    return null;
                }
                var type = Header("content-type") ?? (response.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null);
                var length = long.TryParse(Header("content-length"), out var l) ? l : (long?)null;
                var found = MediaClassifier.Classify(url, status, type, length, Header("content-range"), Header("content-disposition"));
                var referer = core.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? core.Source : tab.Url;
                if (found is not null)
                {
                    if (found.Kind is MediaKind.Hls or MediaKind.Dash) pending[doc.RootElement.GetProperty("requestId").GetString() ?? ""] = (url, e.SessionId); // keep its text too
                    O().AddMedia(tab, core, new DetectedMedia
                    {
                        Url = url, Kind = found.Kind, Container = found.Container, Mime = found.Mime, SizeBytes = found.Size,
                        IsAudio = MediaClassifier.IsAudio(found.Mime, found.Container), Title = tab.DisplayTitle, Referer = referer,
                    });
                    return;
                }
                if (MediaClassifier.NamesAPlaylist(url))
                {
                    pending[doc.RootElement.GetProperty("requestId").GetString() ?? ""] = (url, e.SessionId);
                    O().AddMedia(tab, core, new DetectedMedia { Url = url, Kind = url.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ? MediaKind.Dash : MediaKind.Hls, Container = "mp4", Title = tab.DisplayTitle, Referer = referer, Quiet = true });
                    return;
                }
                // Untyped answers are looked inside once they have fully arrived (as for pages).
                if (tab.Sniffs < BrowserTab.MaxSniffs && (MediaClassifier.ShouldSniff(url, status, type, length) || MediaClassifier.ShouldScanForLinks(url, status, type, length)))
                    pending[doc.RootElement.GetProperty("requestId").GetString() ?? ""] = (url, e.SessionId);
            }
            catch { }
        };
        core.GetDevToolsProtocolEventReceiver("Network.loadingFinished").DevToolsProtocolEventReceived += async (_, e) =>
        {
            if (string.IsNullOrEmpty(e.SessionId)) return;
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                var id = doc.RootElement.GetProperty("requestId").GetString() ?? "";
                if (!pending.TryRemove(id, out var want)) return;
                var known = tab.Media.Any(m => m.Url == want.Url);
                if (!known) tab.Sniffs++;
                var json = await core.CallDevToolsProtocolMethodForSessionAsync(want.Session, "Network.getResponseBody", JsonSerializer.Serialize(new { requestId = id }));
                using var body = JsonDocument.Parse(json);
                var text = body.RootElement.GetProperty("body").GetString() ?? "";
                if (body.RootElement.TryGetProperty("base64Encoded", out var b64) && b64.GetBoolean()) text = Encoding.UTF8.GetString(Convert.FromBase64String(text));
                var referer = core.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? core.Source : tab.Url;
                InlinePlaylists.Remember(want.Url, text);
                if (known) return;
                if (MediaClassifier.SniffPlaylist(Encoding.UTF8.GetBytes(text.Length > 1024 ? text[..1024] : text)) is { } kind)
                    O().AddMedia(tab, core, new DetectedMedia { Url = want.Url, Kind = kind, Container = "mp4", Title = tab.DisplayTitle, Referer = referer });
                else
                    foreach (var link in MediaClassifier.FindStreamLinks(text))
                        O().AddMedia(tab, core, new DetectedMedia { Url = link, Kind = link.Contains(".mpd", StringComparison.OrdinalIgnoreCase) && !link.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? MediaKind.Dash : MediaKind.Hls, Container = "mp4", Title = tab.DisplayTitle, Referer = referer, Quiet = true });
            }
            catch { /* the answer is gone */ }
        };

        // Attach to every worker the page (or one of its frames) starts. Network events are switched on for workers only, so
        // ordinary pages cost nothing extra.
        _ = core.CallDevToolsProtocolMethodAsync("Target.setAutoAttach", """{"autoAttach":true,"waitForDebuggerOnStart":false,"flatten":true}""");
    }
}
