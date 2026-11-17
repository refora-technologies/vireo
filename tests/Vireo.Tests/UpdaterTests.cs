using System.Net.Http;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Vireo.Services;
using Xunit;

namespace Vireo.Tests;

/// <summary>The updater: reading releases, and a whole download against a local stand-in for GitHub.</summary>
[Collection("Updater feed")] // the feed address is process-wide: these never run alongside each other
public class UpdaterTests
{
    [Theory]
    [InlineData("v1.4.2", 1, 4, 2)]
    [InlineData("1.4", 1, 4, 0)]
    [InlineData("v2.0.0-beta.1", 2, 0, 0)]
    [InlineData(" V3.1.7 ", 3, 1, 7)]
    public void Tags_are_read_as_versions(string tag, int major, int minor, int build)
    {
        Assert.True(Updater.TryParseVersion(tag, out var v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData("release-candidate")]
    public void Tags_without_a_version_are_not_releases(string tag) => Assert.False(Updater.TryParseVersion(tag, out _));

    [Fact]
    public void A_release_names_its_installer_and_checksum_by_name_never_any_exe()
    {
        var info = Updater.ParseRelease(Release("v1.2.0", ("Helper.exe", "https://x/helper.exe"), ("VireoSetup.exe", "https://x/setup.exe"), ("VireoSetup.exe.sha256", "https://x/setup.sha256")));
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 2, 0), info!.Version);
        Assert.Equal("https://x/setup.exe", info.InstallerUrl);
        Assert.Equal("https://x/setup.sha256", info.ChecksumUrl);

        var other = Updater.ParseRelease(Release("v1.2.0", ("Helper.exe", "https://x/helper.exe")));
        Assert.Null(other!.InstallerUrl);
    }

    [Fact]
    public void Drafts_and_prereleases_are_never_offered()
    {
        Assert.Null(Updater.ParseRelease("""{"tag_name":"v9.0.0","draft":true,"assets":[]}"""));
        Assert.Null(Updater.ParseRelease("""{"tag_name":"v9.0.0","prerelease":true,"assets":[]}"""));
    }

    [Fact]
    public void Checksum_files_are_read_whatever_their_layout()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, Updater.ParseSha256(hash));
        Assert.Equal(hash, Updater.ParseSha256(hash.ToUpperInvariant() + "  VireoSetup.exe\n"));
        Assert.Null(Updater.ParseSha256("not a hash"));
        Assert.Null(Updater.ParseSha256(""));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\evil.exe", "evil-1.2.0.exe")]
    [InlineData(@"..\..\VireoSetup.exe", "VireoSetup-1.2.0.exe")]
    [InlineData("VireoSetup.exe", "VireoSetup-1.2.0.exe")]
    [InlineData("notes.txt", "VireoSetup-1.2.0.exe")]
    [InlineData(null, "VireoSetup-1.2.0.exe")]
    public void A_file_name_from_the_server_stays_a_plain_file_name(string? name, string expected) =>
        Assert.Equal(expected, Updater.SafeInstallerName(new UpdateInfo(new Version(1, 2, 0), "v1.2.0", "", "https://x", name, 0, null, "")));

    [Fact]
    public void Checks_come_soon_while_failing_and_daily_once_working()
    {
        Assert.Equal(TimeSpan.FromHours(24), Updater.NextCheckIn(0));
        Assert.True(Updater.NextCheckIn(1) < Updater.NextCheckIn(3));
        Assert.True(Updater.NextCheckIn(10) <= TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task A_newer_release_is_downloaded_and_verified_then_ready()
    {
        var installer = Encoding.UTF8.GetBytes(new string('x', 200_000));
        using var server = new FakeGitHub("v99.1.0", installer, Hash(installer));
        var folder = TempFolder();
        try
        {
            Environment.SetEnvironmentVariable("VIREO_UPDATE_FEED", server.Feed);
            var u = new Updater(folder);
            await u.RunAsync(download: true);
            Assert.Equal(UpdateState.Ready, u.State);
            Assert.Equal(new Version(99, 1, 0), u.Latest!.Version);
            Assert.True(File.Exists(u.ReadyInstaller));
            Assert.Equal(installer, await File.ReadAllBytesAsync(u.ReadyInstaller!));
            Assert.Empty(Directory.GetFiles(folder, "*.part"));

            // A later run finds the verified download already there and does not fetch it again.
            var again = new Updater(folder);
            var before = server.InstallerRequests;
            await again.RunAsync(download: true);
            Assert.Equal(UpdateState.Ready, again.State);
            Assert.Equal(before, server.InstallerRequests);
        }
        finally { Environment.SetEnvironmentVariable("VIREO_UPDATE_FEED", null); Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_checksum_is_thrown_away()
    {
        var installer = Encoding.UTF8.GetBytes("pretend installer");
        using var server = new FakeGitHub("v99.1.0", installer, new string('0', 64));
        var folder = TempFolder();
        try
        {
            Environment.SetEnvironmentVariable("VIREO_UPDATE_FEED", server.Feed);
            var u = new Updater(folder);
            await u.RunAsync(download: true);
            Assert.NotEqual(UpdateState.Ready, u.State);
            Assert.Null(u.ReadyInstaller);
            Assert.Contains("checksum", u.Error);
            Assert.Empty(Directory.GetFiles(folder));
            Assert.False(u.Install(reopen: false));
        }
        finally { Environment.SetEnvironmentVariable("VIREO_UPDATE_FEED", null); Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task The_same_or_an_older_version_is_up_to_date()
    {
        using var server = new FakeGitHub("v0.0.1", [1, 2, 3], Hash([1, 2, 3]));
        var folder = TempFolder();
        try
        {
            Environment.SetEnvironmentVariable("VIREO_UPDATE_FEED", server.Feed);
            var u = new Updater(folder);
            await u.RunAsync(download: true);
            Assert.Equal(UpdateState.UpToDate, u.State);
            Assert.Equal(0, server.InstallerRequests);
        }
        finally { Environment.SetEnvironmentVariable("VIREO_UPDATE_FEED", null); Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task No_server_is_a_failed_check_tried_again_soon()
    {
        var folder = TempFolder();
        try
        {
            Environment.SetEnvironmentVariable("VIREO_UPDATE_FEED", $"http://127.0.0.1:{FreePort()}/latest");
            var u = new Updater(folder);
            await u.RunAsync(download: true);
            Assert.Equal(UpdateState.Failed, u.State);
            Assert.Equal(1, u.FailuresInARow);
            Assert.True(Updater.NextCheckIn(u.FailuresInARow) < TimeSpan.FromHours(1));
        }
        finally { Environment.SetEnvironmentVariable("VIREO_UPDATE_FEED", null); Directory.Delete(folder, true); }
    }

    /// <summary>
    /// The real published release, read exactly as an installed Vireo reads it (no sign-in): its version, its installer and
    /// checksum found by name, and the installer matching the checksum. Run after publishing: VIREO_LIVE=1 dotnet test --filter Published.
    /// </summary>
    [Fact]
    public async Task Published_release_is_one_the_updater_can_install()
    {
        if (Environment.GetEnvironmentVariable("VIREO_LIVE") != "1") return;
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Vireo-release-check");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Updater.Owner}/{Updater.Repo}/releases/latest");
        var info = Updater.ParseRelease(json);
        Assert.NotNull(info);
        Assert.Matches(@"^v\d+\.\d+\.\d+$", info!.Tag);
        Assert.NotNull(info.InstallerUrl);
        Assert.NotNull(info.ChecksumUrl);
        Assert.Equal($"VireoSetup-{Updater.Label(info.Version)}.exe", Updater.SafeInstallerName(info));
        var expected = Updater.ParseSha256(await http.GetStringAsync(info.ChecksumUrl));
        Assert.NotNull(expected);
        var installer = await http.GetByteArrayAsync(info.InstallerUrl);
        Assert.Equal(info.InstallerSize, installer.Length);
        Assert.Equal(expected, Hash(installer));
    }

    private static string Release(string tag, params (string Name, string Url)[] assets) =>
        $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/refora-technologies/vireo/releases/tag/{{tag}}","body":"notes","assets":[{{string.Join(",", assets.Select(a => $$"""{"name":"{{a.Name}}","browser_download_url":"{{a.Url}}","size":10}"""))}}]}""";

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static string TempFolder() { var f = Path.Combine(Path.GetTempPath(), "vireo-upd-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(f); return f; }
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    /// <summary>Answers like GitHub's "latest release", with an installer and its checksum file.</summary>
    private sealed class FakeGitHub : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Feed { get; }
        public int InstallerRequests;

        public FakeGitHub(string tag, byte[] installer, string sha)
        {
            var port = FreePort();
            var root = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(root);
            _listener.Start();
            Feed = root + "latest";
            var json = Encoding.UTF8.GetBytes($$"""{"tag_name":"{{tag}}","html_url":"https://github.com/refora-technologies/vireo/releases/tag/{{tag}}","body":"","assets":[{"name":"VireoSetup.exe","browser_download_url":"{{root}}VireoSetup.exe","size":{{installer.Length}}},{"name":"VireoSetup.exe.sha256","browser_download_url":"{{root}}VireoSetup.exe.sha256","size":64}]}""");
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _listener.GetContextAsync(); } catch { return; }
                    var body = c.Request.Url!.AbsolutePath switch
                    {
                        "/latest" => json,
                        "/VireoSetup.exe" => Count(installer),
                        "/VireoSetup.exe.sha256" => Encoding.ASCII.GetBytes(sha + "  VireoSetup.exe"),
                        _ => null,
                    };
                    if (body is null) c.Response.StatusCode = 404;
                    else await c.Response.OutputStream.WriteAsync(body);
                    c.Response.Close();
                }
            });
        }

        private byte[] Count(byte[] b) { Interlocked.Increment(ref InstallerRequests); return b; }
        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
    }
}
