using System.IO;
using System.Text.Json;
using Vireo.Controls;
using Vireo.Downloads;
using Vireo.Media;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

public partial class MainWindow
{
    internal async Task ShowBatchGrabberAsync()
    {
        var tab = Active;
        if (tab?.View?.CoreWebView2 is not { } core) return;

        const string script = """
            (() => {
                try {
                    const seen = new Set();
                    const results = [];
                    const add = (url, name, tag) => {
                        if (!url || typeof url !== 'string') return;
                        try {
                            const u = new URL(url, document.baseURI).href;
                            if (!u.startsWith('http://') && !u.startsWith('https://')) return;
                            if (seen.has(u)) return;
                            seen.add(u);
                            results.push({ url: u, title: (name || '').trim().replace(/[\r\n\t]+/g, ' ').slice(0, 100), tag: tag });
                        } catch {}
                    };

                    for (const a of document.querySelectorAll('a[href]')) {
                        add(a.getAttribute('href'), a.textContent || a.title || a.getAttribute('download'), 'a');
                    }
                    for (const img of document.querySelectorAll('img[src], img[data-src], img[srcset]')) {
                        const s = img.currentSrc || img.src || img.getAttribute('data-src');
                        add(s, img.alt || img.title, 'img');
                        // the biggest picture a srcset offers
                        const sets = (img.getAttribute('srcset') || '').split(',').map(x => x.trim().split(/\s+/));
                        const best = sets.filter(x => x[0]).sort((a, b) => parseFloat(b[1] || '0') - parseFloat(a[1] || '0'))[0];
                        if (best) add(best[0], img.alt || img.title, 'img');
                    }
                    for (const m of document.querySelectorAll('video, audio, source')) {
                        add(m.src || m.getAttribute('src'), m.title, m.tagName.toLowerCase());
                        if (m.poster) add(m.poster, m.title, 'img');
                    }
                    return JSON.stringify(results);
                } catch {
                    return '[]';
                }
            })()
            """;

        string json = "[]";
        try
        {
            json = await core.ExecuteScriptAsync(script);
        }
        catch { }

        var list = new List<GrabbedItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Add all streams already detected on the tab
        foreach (var m in tab.Media.Where(m => m.Status == MediaStatus.Ready))
        {
            var best = m.Choices.FirstOrDefault(c => !c.AudioOnly) ?? m.Choices.FirstOrDefault();
            if (best is not null && seenUrls.Add(best.Url))
            {
                var title = string.IsNullOrWhiteSpace(m.Title) ? tab.DisplayTitle : m.Title;
                var ext = m.Kind == MediaKind.File ? m.Container : "mp4";
                list.Add(new GrabbedItem
                {
                    Url = best.Url,
                    FileName = $"{FileNaming.Sanitize(title)}.{ext}",
                    Title = title,
                    Extension = ext,
                    Category = GrabCategory.Media,
                    StreamMedia = m,
                    IsSelected = true
                });
            }
        }

        // 2. Parse scraped DOM items
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                using var innerDoc = JsonDocument.Parse(root.GetString() ?? "[]");
                ExtractItems(innerDoc.RootElement, list, seenUrls);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                ExtractItems(root, list, seenUrls);
            }
        }
        catch { }

        OpenDrawer("Page Grabber", new BatchGrabberPanel(this, list));
    }

    private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "html", "htm", "xhtml", "php", "asp", "aspx", "jsp", "cgi", "js", "mjs", "css", "json", "xml", "map", "woff", "woff2", "ttf", "otf" };

    private static void ExtractItems(JsonElement array, List<GrabbedItem> list, HashSet<string> seenUrls)
    {
        foreach (var el in array.EnumerateArray())
        {
            var url = el.GetProperty("url").GetString();
            if (string.IsNullOrWhiteSpace(url) || !seenUrls.Add(url)) continue;

            var title = el.GetProperty("title").GetString();
            var tag = el.GetProperty("tag").GetString();

            string ext = "";
            string name = "";
            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath;
                var lastSlash = path.LastIndexOf('/');
                var rawName = lastSlash >= 0 ? path[(lastSlash + 1)..] : "";
                var dot = rawName.LastIndexOf('.');
                if (dot >= 0)
                {
                    ext = rawName[(dot + 1)..].ToLowerInvariant();
                    name = Uri.UnescapeDataString(rawName);
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = !string.IsNullOrWhiteSpace(title) ? FileNaming.Sanitize(title) : "download";
                if (!string.IsNullOrEmpty(ext)) name += "." + ext;
            }

            var category = GrabbedItem.ClassifyExtension(ext);
            if (category == GrabCategory.Other)
            {
                if (tag is "img") category = GrabCategory.Images;
                else if (tag is "video" or "audio") category = GrabCategory.Media;
            }

            // Exclude clutter: links to ordinary web pages and scripts are not files worth saving
            if (category == GrabCategory.Other && (string.IsNullOrWhiteSpace(ext) || PageExtensions.Contains(ext))) continue;

            list.Add(new GrabbedItem
            {
                Url = url,
                FileName = name,
                Title = title,
                Extension = ext,
                Category = category,
                IsSelected = category != GrabCategory.Other
            });
        }
    }
}
