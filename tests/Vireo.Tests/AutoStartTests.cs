using System.IO;
using Microsoft.Win32;
using Vireo.Services;
using Xunit;

namespace Vireo.Tests;

/// <summary>"Start with Windows", written into a test key of its own: the real Run key is never touched.</summary>
public sealed class AutoStartTests : IDisposable
{
    private readonly string _key = $@"Software\VireoTests\Run-{Guid.NewGuid():N}";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);

    private string? Value(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_key);
        return key?.GetValue(name) as string;
    }

    [Fact]
    public void Turning_it_on_starts_only_the_downloader_quietly_and_off_removes_it()
    {
        Assert.False(AutoStart.IsOn("", _key));
        Assert.True(AutoStart.Set(true, @"C:\Apps\Vireo\Vireo.exe", "", _key));
        Assert.True(AutoStart.IsOn("", _key));
        Assert.Equal("\"C:\\Apps\\Vireo\\Vireo.exe\" --downloads --quiet", Value("Vireo Downloads"));

        Assert.True(AutoStart.Set(false, @"C:\Apps\Vireo\Vireo.exe", "", _key));
        Assert.False(AutoStart.IsOn("", _key));
        Assert.True(AutoStart.Set(false, @"C:\Apps\Vireo\Vireo.exe", "", _key)); // already off: fine
    }

    [Fact]
    public void Each_profile_has_its_own_entry()
    {
        AutoStart.Set(true, @"C:\V\Vireo.exe", "work", _key);
        Assert.Equal("\"C:\\V\\Vireo.exe\" --downloads --quiet --profile work", Value("Vireo Downloads (work)"));
        Assert.False(AutoStart.IsOn("", _key));
    }

    [Fact]
    public void An_entry_for_a_copy_that_is_gone_is_pointed_here_but_another_copy_keeps_its_entry()
    {
        var real = Path.Combine(AppContext.BaseDirectory, "testhost.exe");
        AutoStart.Set(true, @"C:\Nowhere\Old\Vireo.exe", "", _key);
        AutoStart.Refresh(real, "", _key);
        Assert.Equal(AutoStart.Command(real, ""), Value("Vireo Downloads"));

        // A copy that still exists (the installed Vireo) is not taken over by a second copy run for a moment.
        var other = Path.Combine(AppContext.BaseDirectory, "Vireo.dll");
        AutoStart.Refresh(other, "", _key);
        Assert.Equal(AutoStart.Command(real, ""), Value("Vireo Downloads"));
    }

    [Fact]
    public void Uninstalling_removes_every_profiles_entry_and_nothing_else()
    {
        AutoStart.Set(true, @"C:\V\Vireo.exe", "", _key);
        AutoStart.Set(true, @"C:\V\Vireo.exe", "work", _key);
        using (var key = Registry.CurrentUser.CreateSubKey(_key)) key.SetValue("Other program", "x.exe");
        AutoStart.RemoveAll(_key);
        Assert.False(AutoStart.IsOn("", _key));
        Assert.False(AutoStart.IsOn("work", _key));
        Assert.Equal("x.exe", Value("Other program"));
    }

    [Fact]
    public void Refresh_never_creates_an_entry()
    {
        AutoStart.Refresh(@"C:\V\Vireo.exe", "", _key);
        Assert.False(AutoStart.IsOn("", _key));
    }
}
