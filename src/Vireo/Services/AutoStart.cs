using Microsoft.Win32;

namespace Vireo.Services;

/// <summary>
/// "Start with Windows" for the downloader: one value in the current user's Run key (no administrator rights, no service,
/// no scheduled task), which starts Vireo with only its downloader in the notification area, quietly. Windows' own
/// Startup apps page (Task Manager, Settings) shows it as "Vireo Downloads" and can switch it off there too.
/// </summary>
public static class AutoStart
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The value's name; a profile other than the default one gets its own.</summary>
    public static string ValueName(string profile) => profile.Length == 0 ? "Vireo Downloads" : $"Vireo Downloads ({profile})";

    /// <summary>What Windows runs at sign-in.</summary>
    public static string Command(string exePath, string profile) =>
        $"\"{exePath}\" --downloads --quiet" + (profile.Length > 0 ? $" --profile {profile}" : "");

    public static bool IsOn(string profile, string runKey = DefaultRunKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKey);
        return key?.GetValue(ValueName(profile)) is string;
    }

    /// <summary>Turns it on or off. False when the registry could not be written (a locked-down computer).</summary>
    public static bool Set(bool on, string exePath, string profile, string runKey = DefaultRunKey)
    {
        try
        {
            if (on)
            {
                using var key = Registry.CurrentUser.CreateSubKey(runKey);
                key.SetValue(ValueName(profile), Command(exePath, profile));
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
                key?.DeleteValue(ValueName(profile), throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { return false; }
    }

    /// <summary>Every profile's entry goes (uninstalling).</summary>
    public static void RemoveAll(string runKey = DefaultRunKey)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key is null) return;
            foreach (var name in key.GetValueNames().Where(n => n == ValueName("") || n.StartsWith(ValueName("") + " (", StringComparison.Ordinal)))
                key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { /* nothing more to do */ }
    }

    /// <summary>
    /// Vireo moved (installed somewhere else): an entry whose program no longer exists is pointed at this copy. An entry for
    /// another copy that still exists is left alone (a second copy run for a moment must not take over). Never creates one.
    /// </summary>
    public static void Refresh(string exePath, string profile, string runKey = DefaultRunKey)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key?.GetValue(ValueName(profile)) is string old && old != Command(exePath, profile)
                && old.StartsWith('"') && old.IndexOf('"', 1) is var end and > 1 && !System.IO.File.Exists(old[1..end]))
                key.SetValue(ValueName(profile), Command(exePath, profile));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { /* left as it is */ }
    }
}
