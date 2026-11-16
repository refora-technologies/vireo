using System.Diagnostics;
using Microsoft.Win32;

namespace Vireo.Services;

/// <summary>
/// Makes Vireo appear in Windows' list of browsers, so it can be chosen as the default for web links and pages.
/// Windows does not let a program choose itself as the default (a person has to confirm in Settings), so registering only
/// makes the choice available and then opens the right Settings page.
/// </summary>
public static class DefaultBrowser
{
    public const string ProgIdPage = "VireoHTML";
    public const string ProgIdUrl = "VireoURL";
    public const string ClientName = "Vireo";

    /// <summary>Writes the registration under <paramref name="software"/> in the current user's registry (a test points it elsewhere).</summary>
    public static void Register(string exePath, string software = "Software")
    {
        var command = $"\"{exePath}\" \"%1\"";
        var icon = $"\"{exePath}\",0";
        using var root = Registry.CurrentUser;

        void Set(string path, string? name, string value)
        {
            using var key = root.CreateSubKey(path);
            key.SetValue(name, value);
        }

        // How a web page file and a web link are opened
        Set($@"{software}\Classes\{ProgIdPage}", null, "Vireo HTML Document");
        Set($@"{software}\Classes\{ProgIdPage}\DefaultIcon", null, icon);
        Set($@"{software}\Classes\{ProgIdPage}\shell\open\command", null, command);
        Set($@"{software}\Classes\{ProgIdUrl}", null, "Vireo URL");
        Set($@"{software}\Classes\{ProgIdUrl}", "URL Protocol", "");
        Set($@"{software}\Classes\{ProgIdUrl}\DefaultIcon", null, icon);
        Set($@"{software}\Classes\{ProgIdUrl}\shell\open\command", null, command);

        // The entry in the list of browsers
        var client = $@"{software}\Clients\StartMenuInternet\{ClientName}";
        Set(client, null, "Vireo");
        Set($@"{client}\DefaultIcon", null, icon);
        Set($@"{client}\shell\open\command", null, $"\"{exePath}\"");
        Set($@"{client}\Capabilities", "ApplicationName", "Vireo");
        Set($@"{client}\Capabilities", "ApplicationDescription", "A fast, private browser with a download manager built in.");
        Set($@"{client}\Capabilities", "ApplicationIcon", icon);
        Set($@"{client}\Capabilities\URLAssociations", "http", ProgIdUrl);
        Set($@"{client}\Capabilities\URLAssociations", "https", ProgIdUrl);
        foreach (var ext in new[] { ".htm", ".html", ".xhtml", ".shtml", ".pdf" }) Set($@"{client}\Capabilities\FileAssociations", ext, ProgIdPage);
        Set($@"{client}\Capabilities\StartMenu", "StartMenuInternet", ClientName);
        Set($@"{software}\RegisteredApplications", ClientName, $@"{software}\Clients\StartMenuInternet\{ClientName}\Capabilities");
    }

    public static void Unregister(string software = "Software")
    {
        using var root = Registry.CurrentUser;
        foreach (var path in new[] { $@"{software}\Classes\{ProgIdPage}", $@"{software}\Classes\{ProgIdUrl}", $@"{software}\Clients\StartMenuInternet\{ClientName}" })
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        using var apps = root.OpenSubKey($@"{software}\RegisteredApplications", writable: true);
        apps?.DeleteValue(ClientName, throwOnMissingValue: false);
    }

    /// <summary>Whether Vireo is registered (so Windows lists it).</summary>
    public static bool IsRegistered(string software = "Software")
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{software}\RegisteredApplications");
        return key?.GetValue(ClientName) is string;
    }

    /// <summary>Whether the person has chosen Vireo for web links.</summary>
    public static bool IsDefault()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
        return key?.GetValue("ProgId") is string id && id.Equals(ProgIdUrl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens the Windows page where the choice is made.</summary>
    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=Vireo") { UseShellExecute = true }); }
        catch { try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { /* no Settings app */ } }
    }
}
