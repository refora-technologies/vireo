using System.IO;
using System.IO.Compression;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

internal sealed partial class SelfTest
{
    private static string MakeSampleExtension()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vireo-sample-ext-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """
            {
              "manifest_version": 3, "name": "Vireo Test Extension", "version": "1.2.3", "description": "Marks every page and has a popup.",
              "action": { "default_popup": "popup.html", "default_title": "Test" },
              "content_scripts": [ { "matches": ["<all_urls>"], "js": ["content.js"], "run_at": "document_idle" } ]
            }
            """);
        File.WriteAllText(Path.Combine(dir, "content.js"), "document.documentElement.setAttribute('data-vireo-ext', 'yes');");
        File.WriteAllText(Path.Combine(dir, "popup.html"), "<!doctype html><html><body style=\"width:320px;margin:0;background:#222;color:#fff;font:15px sans-serif\"><h1 id=\"t\">Hello from the extension</h1><script src=\"popup.js\"></script></body></html>");
        File.WriteAllText(Path.Combine(dir, "popup.js"), "document.getElementById('t').textContent += ' - its script ran';");
        return dir;
    }

    /// <summary>Installing, running, showing and removing a real extension.</summary>
    private async Task RunExtensionStepsAsync()
    {
        if (!w.ExtensionsAvailable) { Console.WriteLine("SKIP  extension steps (no profile yet)"); return; }
        using var site = new PageSite().Html("/plain.html", "<!doctype html><title>Plain</title><body><p>hello</p></body>");
        var dir = MakeSampleExtension();
        try
        {
            var error = await w.InstallExtensionAsync(() => w.Extensions.InstallFolderAsync(w.CurrentProfile!, dir));
            await w.RefreshExtensionsAsync();
            var ext = w.InstalledExtensions.FirstOrDefault(e => e.Manifest.Name == "Vireo Test Extension");
            Check("an unpacked extension installs", error.Length == 0 && ext is not null, error);
            if (ext is null) return;
            Check("with its name, version and popup read from the manifest", ext.Manifest is { Version: "1.2.3", PopupPath: "popup.html" } && ext.PopupUrl == $"chrome-extension://{ext.Id}/popup.html", ext.PopupUrl);

            var tab = await w.OpenTabAsync(site.Base + "/plain.html");
            await WaitFor(() => !tab.IsLoading && tab.Title == "Plain");
            for (var i = 0; i < 30 && await w.EvalAsync(tab, "document.documentElement.getAttribute('data-vireo-ext')") != "\"yes\""; i++) await Task.Delay(200);
            Check("its content script runs in pages", await w.EvalAsync(tab, "document.documentElement.getAttribute('data-vireo-ext')") == "\"yes\"", await w.EvalAsync(tab, "String(document.documentElement.getAttribute('data-vireo-ext'))"));

            Check("it gets a button next to the address bar", w.ExtensionStripCount == 1, w.ExtensionStripCount.ToString());
            await w.OpenExtensionAsync(ext, w.ExtensionStripFirst!);
            string text = "";
            for (var i = 0; i < 40 && !text.Contains("its script ran"); i++)
            {
                await Task.Delay(250);
                if (w.ExtensionPopupView?.CoreWebView2 is { } c) try { text = await c.ExecuteScriptAsync("document.getElementById('t') ? document.getElementById('t').textContent : ''"); } catch { /* still loading */ }
            }
            Check("pressing it opens its popup, and the popup's own script runs", text.Contains("Hello from the extension - its script ran"), text);
            await Task.Delay(400);
            await Shot("35-extension-popup");
            w.CloseExtensionPopup();

            await w.Extensions.SetEnabledAsync(w.CurrentProfile!, ext.Id, false);
            await w.RefreshExtensionsAsync();
            Check("switching it off takes the button away", w.ExtensionStripCount == 0);
            await w.Extensions.SetEnabledAsync(w.CurrentProfile!, ext.Id, true);
            await w.RefreshExtensionsAsync();
            Check("and on brings it back", w.ExtensionStripCount == 1);

            w.ShowExtensions();
            await Task.Delay(900);
            await Shot("36-extensions-panel");
            await HoverEveryButtonAsync("Extensions panel");
            w.CloseDrawer();

            await w.Extensions.RemoveAsync(w.CurrentProfile!, ext.Id);
            await w.RefreshExtensionsAsync();
            Check("removing it takes it away, with its files", w.InstalledExtensions.Count == 0 && !Directory.Exists(ext.Folder), $"{w.InstalledExtensions.Count} left");

            // the same thing as a .zip file
            var zip = Path.Combine(Path.GetTempPath(), "vireo-sample-ext-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
            ZipFile.CreateFromDirectory(dir, zip);
            error = await w.InstallExtensionAsync(() => w.Extensions.InstallFileAsync(w.CurrentProfile!, zip));
            await w.RefreshExtensionsAsync();
            Check("an extension in a .zip file installs too", error.Length == 0 && w.InstalledExtensions.Count == 1, error);
            foreach (var e in w.InstalledExtensions.ToList()) await w.Extensions.RemoveAsync(w.CurrentProfile!, e.Id);
            File.Delete(zip);
            await w.RefreshExtensionsAsync();

            var bad = await w.InstallExtensionAsync(() => w.Extensions.InstallFolderAsync(w.CurrentProfile!, Path.GetTempPath() + "no-such-folder-" + Guid.NewGuid().ToString("N")));
            Check("a folder that is not an extension is refused with a reason", bad.Length > 0, bad);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* in use */ } }
    }
}
