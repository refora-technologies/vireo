using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vireo.Services;

/// <summary>A person's separate browser: its own cookies and sign-ins, history, bookmarks, settings and extensions.</summary>
public sealed record Profile(string Id, string Name, string Color);

/// <summary>
/// The profiles on this computer. Each one keeps everything in a folder of its own and runs as its own Vireo (one process per
/// profile), so nothing can leak from one to another: not cookies, not sign-ins, not history. The first profile ("Personal")
/// is the one Vireo always had, in its old place.
/// </summary>
public static class Profiles
{
    public static readonly string[] Colors = ["#3DDBB9", "#7AA2FF", "#F2A65A", "#E879F9", "#F87171", "#FACC15", "#4ADE80", "#A1A1AA"];

    /// <summary>Where Vireo keeps its profiles (tests point this somewhere else).</summary>
    public static string Root => Environment.GetEnvironmentVariable("VIREO_PROFILES_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vireo");

    private static string ListPath => Path.Combine(Root, "profiles.json");

    public static Profile Default => new("", "Personal", Colors[0]);

    /// <summary>A profile's own folder. The default profile keeps the folder Vireo has always used.</summary>
    public static string FolderOf(string id) => id.Length == 0 ? Path.Combine(Root, "profile") : Path.Combine(Root, "profiles", id);

    /// <summary>Only plain names reach the file system: lower-case letters, digits and dashes.</summary>
    public static bool ValidId(string id) => id.Length is > 0 and <= 40 && Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]*$");

    public static List<Profile> All()
    {
        var list = new List<Profile> { Default };
        try
        {
            if (File.Exists(ListPath))
            {
                var saved = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(ListPath)) ?? [];
                foreach (var p in saved)
                {
                    if (p.Id.Length == 0) list[0] = p with { Name = Clean(p.Name, "Personal"), Color = ColorOr(p.Color) };
                    else if (ValidId(p.Id) && list.All(x => x.Id != p.Id)) list.Add(p with { Name = Clean(p.Name, "Profile"), Color = ColorOr(p.Color) });
                }
            }
        }
        catch { /* a damaged list: the default profile still works */ }
        return list;
    }

    public static Profile Find(string id) => All().FirstOrDefault(p => p.Id == id) ?? (id.Length == 0 ? Default : new Profile(id, id, Colors[1]));

    /// <summary>Makes a new profile. Its folder is created when it first starts.</summary>
    public static Profile Add(string name, string? color = null)
    {
        var list = All();
        var slug = Regex.Replace(Clean(name, "profile").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "profile";
        if (slug.Length > 24) slug = slug[..24].Trim('-');
        var id = slug;
        for (var n = 2; list.Any(p => p.Id == id) || Directory.Exists(FolderOf(id)); n++) id = $"{slug}-{n}";
        var profile = new Profile(id, Clean(name, "Profile"), ColorOr(color ?? Colors[list.Count % Colors.Length]));
        list.Add(profile);
        Save(list);
        return profile;
    }

    public static void Update(Profile profile)
    {
        var list = All();
        var i = list.FindIndex(p => p.Id == profile.Id);
        if (i < 0) return;
        list[i] = profile with { Name = Clean(profile.Name, list[i].Name), Color = ColorOr(profile.Color) };
        Save(list);
    }

    /// <summary>Takes a profile off the list and deletes everything it kept. The default profile cannot be removed.</summary>
    public static bool Remove(string id)
    {
        if (!ValidId(id)) return false;
        var list = All();
        if (list.RemoveAll(p => p.Id == id) == 0) return false;
        Save(list);
        try { if (Directory.Exists(FolderOf(id))) Directory.Delete(FolderOf(id), recursive: true); }
        catch { /* still open in its own Vireo: the folder goes the next time */ }
        return true;
    }

    /// <summary>The command line that opens a profile: no extra words for the default one.</summary>
    public static string[] Arguments(string id, string? url = null)
    {
        var args = new List<string>();
        if (id.Length > 0) { args.Add("--profile"); args.Add(id); }
        if (!string.IsNullOrWhiteSpace(url)) args.Add(url);
        return [.. args];
    }

    /// <summary>Opens a profile in its own Vireo (or brings its window forward if it is already open).</summary>
    public static void Launch(string id, string? url = null)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var a in Arguments(id, url)) start.ArgumentList.Add(a);
        Process.Start(start);
    }

    /// <summary>A desktop shortcut that opens the profile directly. Returns its path, or null when Windows would not make one.</summary>
    public static string? CreateShortcut(Profile profile)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var name = Regex.Replace(profile.Name, "[\\\\/:*?\"<>|]", "");
            var path = Path.Combine(desktop, $"Vireo - {name}.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = Environment.ProcessPath;
            link.Arguments = string.Join(' ', Arguments(profile.Id));
            link.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            link.IconLocation = Environment.ProcessPath + ",0";
            link.Description = $"Vireo ({profile.Name})";
            link.Save();
            return path;
        }
        catch { return null; }
    }

    private static void Save(List<Profile> list)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var tmp = ListPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            File.Move(tmp, ListPath, overwrite: true);
        }
        catch { /* the list is small; the next change writes it again */ }
    }

    private static string Clean(string? name, string fallback)
    {
        var n = (name ?? "").Trim();
        n = Regex.Replace(n, "\\s+", " ");
        if (n.Length > 30) n = n[..30].Trim();
        return n.Length == 0 ? fallback : n;
    }

    private static string ColorOr(string? color) => color is not null && Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$") ? color : Colors[0];
}
