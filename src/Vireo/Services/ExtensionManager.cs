using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Vireo.Services;

/// <summary>What an extension says about itself in its manifest.json.</summary>
public sealed record ExtensionManifest(string Name, string Version, string Description, string? PopupPath, string? OptionsPath, string? IconPath, int ManifestVersion);

public sealed record ExtensionInfo(string Id, ExtensionManifest Manifest, string Folder, bool Enabled)
{
    public string? PopupUrl => Manifest.PopupPath is { Length: > 0 } p ? $"chrome-extension://{Id}/{p.TrimStart('/')}" : null;
    public string? OptionsUrl => Manifest.OptionsPath is { Length: > 0 } p ? $"chrome-extension://{Id}/{p.TrimStart('/')}" : null;
    public string? IconFile => Manifest.IconPath is { Length: > 0 } p ? Path.Combine(Folder, p.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)) : null;
}

/// <summary>
/// Extensions for the engine Vireo is built on. The engine runs them (content scripts, background workers, pages) but draws nothing:
/// there is no toolbar button and no popup, and the browser stores will not install into it. So Vireo installs unpacked ones
/// from a folder or a .zip/.crx file, keeps its own copy in the profile, and draws the buttons and popups itself.
/// </summary>
public sealed class ExtensionManager
{
    private readonly string _root;
    private readonly string _index;
    private Dictionary<string, string> _folders = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExtensionManager(string profileFolder)
    {
        // Next to the profile, not inside it: the engine refuses an extension that lives in its own data folder.
        _root = Path.GetFullPath(profileFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-extensions"; // absolute: the engine does not accept a relative path
        _index = Path.Combine(_root, "index.json");
        try { if (File.Exists(_index)) _folders = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_index)) ?? []; }
        catch { _folders = []; }
    }

    // ---- Reading a manifest ----

    public static ExtensionManifest? ReadManifest(string folder)
    {
        try
        {
            var file = Path.Combine(folder, "manifest.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var m = doc.RootElement;
            string Text(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Localised(folder, v.GetString() ?? "", m) : "";
            var name = Text("name");
            if (name.Length == 0) return null;

            string? popup = null, icon = null, options = null;
            JsonElement action = default;
            var hasAction = (m.TryGetProperty("action", out action) || m.TryGetProperty("browser_action", out action) || m.TryGetProperty("page_action", out action)) && action.ValueKind == JsonValueKind.Object;
            if (hasAction)
            {
                if (action.TryGetProperty("default_popup", out var p) && p.ValueKind == JsonValueKind.String) popup = p.GetString();
                if (action.TryGetProperty("default_icon", out var i)) icon = BestIcon(i);
            }
            icon ??= m.TryGetProperty("icons", out var icons) ? BestIcon(icons) : null;
            if (m.TryGetProperty("options_page", out var op) && op.ValueKind == JsonValueKind.String) options = op.GetString();
            else if (m.TryGetProperty("options_ui", out var ou) && ou.ValueKind == JsonValueKind.Object && ou.TryGetProperty("page", out var up) && up.ValueKind == JsonValueKind.String) options = up.GetString();
            var version = m.TryGetProperty("manifest_version", out var mv) && mv.TryGetInt32(out var n) ? n : 0;
            return new ExtensionManifest(name, Text("version"), Text("description"), popup, options, icon, version);
        }
        catch { return null; }
    }

    /// <summary>"__MSG_appName__" is looked up in the extension's own translations.</summary>
    private static string Localised(string folder, string text, JsonElement manifest)
    {
        if (!text.StartsWith("__MSG_", StringComparison.Ordinal) || !text.EndsWith("__", StringComparison.Ordinal)) return text;
        var key = text[6..^2];
        var locale = manifest.TryGetProperty("default_locale", out var d) ? d.GetString() ?? "en" : "en";
        foreach (var l in new[] { locale, "en", "en_US" })
        {
            try
            {
                var file = Path.Combine(folder, "_locales", l, "messages.json");
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetProperty("message", out var msg)) return msg.GetString() ?? text;
            }
            catch { /* try the next language */ }
        }
        return text;
    }

    private static string? BestIcon(JsonElement icon)
    {
        if (icon.ValueKind == JsonValueKind.String) return icon.GetString();
        if (icon.ValueKind != JsonValueKind.Object) return null;
        return icon.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
            .OrderBy(p => int.TryParse(p.Name, out var size) ? Math.Abs(size - 48) : 999).Select(p => p.Value.GetString()).FirstOrDefault();
    }

    // ---- Getting an extension in ----

    /// <summary>The folder inside <paramref name="folder"/> that holds manifest.json (it is sometimes one level down in a downloaded .zip).</summary>
    public static string? FindRoot(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        if (File.Exists(Path.Combine(folder, "manifest.json"))) return folder;
        var inner = Directory.GetDirectories(folder);
        return inner.Length == 1 && File.Exists(Path.Combine(inner[0], "manifest.json")) ? inner[0] : null;
    }

    /// <summary>Unpacks a .zip, or a .crx (a .zip with a signed header in front).</summary>
    public static string Unpack(string file)
    {
        var bytes = File.ReadAllBytes(file);
        var offset = 0;
        if (bytes.Length > 12 && bytes[0] == 'C' && bytes[1] == 'r' && bytes[2] == '2' && bytes[3] == '4')
        {
            var version = BitConverter.ToUInt32(bytes, 4);
            if (version == 3) offset = 12 + (int)BitConverter.ToUInt32(bytes, 8);
            else if (version == 2) offset = 16 + (int)BitConverter.ToUInt32(bytes, 8) + (int)BitConverter.ToUInt32(bytes, 12);
            else throw new InvalidDataException("This kind of extension file is not known.");
        }
        var temp = Path.Combine(Path.GetTempPath(), "vireo-ext-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(temp);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, offset, bytes.Length - offset), ZipArchiveMode.Read);
            zip.ExtractToDirectory(temp); // refuses entries that point outside the folder
            return temp;
        }
        catch { try { Directory.Delete(temp, true); } catch { /* gone */ } throw; }
    }

    /// <summary>Copies an unpacked extension into the profile and gives it to the engine.</summary>
    public async Task<ExtensionInfo> InstallFolderAsync(CoreWebView2Profile profile, string source)
    {
        var root = FindRoot(source) ?? throw new InvalidDataException("There is no manifest.json in that folder.");
        var manifest = ReadManifest(root) ?? throw new InvalidDataException("That manifest.json cannot be read.");
        await _gate.WaitAsync();
        try
        {
            var safe = new string(manifest.Name.Where(char.IsLetterOrDigit).Take(24).ToArray());
            var target = Path.Combine(_root, (safe.Length > 0 ? safe : "extension") + "-" + Guid.NewGuid().ToString("N")[..6]);
            CopyFolder(root, target);
            CoreWebView2BrowserExtension added;
            try { added = await profile.AddBrowserExtensionAsync(target); }
            catch { try { Directory.Delete(target, true); } catch { /* gone */ } throw; }
            // The engine keeps one extension per id: installing the same one again replaces it, so the older copy is not needed.
            if (_folders.TryGetValue(added.Id, out var old) && !string.Equals(old, target, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(old, true); } catch { /* in use */ }
            _folders[added.Id] = target;
            SaveIndex();
            return new ExtensionInfo(added.Id, manifest, target, added.IsEnabled);
        }
        finally { _gate.Release(); }
    }

    public async Task<ExtensionInfo> InstallFileAsync(CoreWebView2Profile profile, string file)
    {
        var temp = Unpack(file);
        try { return await InstallFolderAsync(profile, temp); }
        finally { try { Directory.Delete(temp, true); } catch { /* the engine may still be reading */ } }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from).Where(d => Path.GetFileName(d) is not ("node_modules" or ".git"))) CopyFolder(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // ---- What is installed ----

    public async Task<List<ExtensionInfo>> ListAsync(CoreWebView2Profile profile)
    {
        var list = new List<ExtensionInfo>();
        var installed = await profile.GetBrowserExtensionsAsync();
        foreach (var e in installed)
        {
            if (!_folders.TryGetValue(e.Id, out var folder) || ReadManifest(folder) is not { } manifest) continue; // not one of ours
            list.Add(new ExtensionInfo(e.Id, manifest, folder, e.IsEnabled));
        }
        return [.. list.OrderBy(x => x.Manifest.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task SetEnabledAsync(CoreWebView2Profile profile, string id, bool enabled)
    {
        var ext = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Id == id);
        if (ext is not null) await ext.EnableAsync(enabled);
    }

    public async Task RemoveAsync(CoreWebView2Profile profile, string id)
    {
        var ext = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Id == id);
        if (ext is not null) await ext.RemoveAsync();
        await _gate.WaitAsync();
        try
        {
            if (_folders.Remove(id, out var folder)) try { Directory.Delete(folder, true); } catch { /* in use */ }
            SaveIndex();
        }
        finally { _gate.Release(); }
    }

    private void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(_index, JsonSerializer.Serialize(_folders));
        }
        catch { /* the engine still has them; only the names would be lost */ }
    }
}
