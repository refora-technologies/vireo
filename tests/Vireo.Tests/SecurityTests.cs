using System.IO;
using Vireo.Downloads;
using Xunit;

namespace Vireo.Tests;

public class SecurityTests
{
    [Fact]
    public void A_downloaded_file_is_marked_as_from_the_internet_without_any_password_in_the_note()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vireo-motw-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        try
        {
            MarkOfTheWeb.Apply(path, "https://user:secret@files.example.com/setup.exe", "https://example.com/download");
            Assert.True(MarkOfTheWeb.IsMarked(path));
            var note = File.ReadAllText(path + ":Zone.Identifier");
            Assert.Contains("ZoneId=3", note);
            Assert.Contains("HostUrl=https://files.example.com/setup.exe", note);
            Assert.Contains("ReferrerUrl=https://example.com/download", note);
            Assert.DoesNotContain("secret", note);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_missing_file_or_odd_addresses_never_throw()
    {
        MarkOfTheWeb.Apply(Path.Combine(Path.GetTempPath(), "does-not-exist.bin"), "https://x", null);
        var path = Path.GetTempFileName();
        try
        {
            MarkOfTheWeb.Apply(path, "javascript:alert(1)", "not an address");
            Assert.True(MarkOfTheWeb.IsMarked(path));
            Assert.DoesNotContain("javascript", File.ReadAllText(path + ":Zone.Identifier"));
        }
        finally { File.Delete(path); }
    }
}
