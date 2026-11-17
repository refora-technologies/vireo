using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vireo.Services;

/// <summary>A published release, as GitHub describes it.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string ReleaseUrl, string? InstallerUrl, string? InstallerName, long InstallerSize, string? ChecksumUrl, string Notes)
{
    public string Label => Updater.Label(Version);
}

public enum UpdateState
{
    /// <summary>Nothing checked yet in this run.</summary>
    Idle,
    Checking,
    UpToDate,
    /// <summary>A newer version exists and is not downloaded (automatic downloads are off, or it failed).</summary>
    Available,
    Downloading,
    /// <summary>Downloaded and verified: it installs when Vireo closes, or now with "Restart to update".</summary>
    Ready,
    /// <summary>The check or the download did not work; tried again later.</summary>
    Failed,
}

/// <summary>
/// Keeps Vireo up to date from its GitHub releases: checks quietly, downloads in the background, checks the download against
/// the SHA-256 published with the release, and installs either when Vireo closes or when the person chooses "Restart to update".
/// Vireo installs for the current user only, so updating never asks for administrator rights.
/// </summary>
/// <remarks>
/// Only published releases are seen (GitHub's "latest" never returns drafts or pre-releases), so nothing reaches anyone until a
/// release is published by hand. An installer whose checksum is missing or wrong is never run: until Vireo is code-signed, the
/// published hash is what ties the download to the release.
/// </remarks>
public sealed class Updater
{
    public const string Owner = "refora-technologies";
    public const string Repo = "vireo";
    public const string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases";
    private const string InstallerPrefix = "VireoSetup";

    /// <summary>Where releases are read from. VIREO_UPDATE_FEED points it elsewhere (a local test server).</summary>
    internal static string Feed => Environment.GetEnvironmentVariable("VIREO_UPDATE_FEED") is { Length: > 0 } f
        ? f : $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>A download that has delivered nothing for this long is not coming back. (No limit on the whole transfer: slow is fine.)</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private static readonly HttpClient Api = MakeClient(TimeSpan.FromSeconds(15));
    private static readonly HttpClient Files = MakeClient(Timeout.InfiniteTimeSpan);

    private static HttpClient MakeClient(TimeSpan timeout)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10, ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = timeout };
        client.DefaultRequestHeaders.Add("User-Agent", $"Vireo/{Label(Current)}");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    private readonly string _folder;
    private readonly object _gate = new();
    private Task? _running;

    public Updater(string? folder = null)
    {
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vireo", "updates");
    }

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public UpdateInfo? Latest { get; private set; }
    /// <summary>0–100 while downloading.</summary>
    public int Progress { get; private set; }
    /// <summary>The verified installer, once <see cref="State"/> is <see cref="UpdateState.Ready"/>.</summary>
    public string? ReadyInstaller { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LastChecked { get; private set; }
    /// <summary>Checks in a row that could not reach the server: sets how soon the next one is.</summary>
    public int FailuresInARow { get; private set; }

    /// <summary>Raised (on any thread) whenever the state, the progress or the latest release changes.</summary>
    public event Action? Changed;

    public static Version Current
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string Label(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    /// <summary>Checks for a newer release and, when <paramref name="download"/>, fetches and verifies it. One run at a time.</summary>
    public Task RunAsync(bool download, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false }) return _running;
            return _running = Task.Run(() => RunCoreAsync(download, ct), ct);
        }
    }

    private async Task RunCoreAsync(bool download, CancellationToken ct)
    {
        if (State is UpdateState.Ready && ReadyInstaller is not null && File.Exists(ReadyInstaller)) return;
        Set(UpdateState.Checking);
        UpdateInfo? latest;
        try
        {
            var json = await Api.GetStringAsync(Feed, ct);
            latest = ParseRelease(json);
            LastChecked = DateTime.Now;
            FailuresInARow = 0;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            FailuresInARow++;
            Error = e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                ? "No releases have been published yet."
                : "Could not reach the update server.";
            Set(UpdateState.Failed);
            return;
        }

        if (latest is null || latest.Version <= Current)
        {
            Latest = latest;
            Error = null;
            Set(UpdateState.UpToDate);
            Tidy(keep: null);
            return;
        }
        Latest = latest;
        Error = null;
        // Already downloaded and still good (an earlier run fetched it): ready at once.
        var target = Path.Combine(_folder, SafeInstallerName(latest));
        if (File.Exists(target) && await ExpectedHashAsync(latest, ct) is { } known && await HashOfAsync(target) == known)
        {
            ReadyInstaller = target;
            Set(UpdateState.Ready);
            return;
        }
        if (!download) { Set(UpdateState.Available); return; }
        await DownloadAsync(latest, ct);
    }

    /// <summary>Fetches the installer, then keeps it only if it matches the release's published SHA-256.</summary>
    private async Task DownloadAsync(UpdateInfo info, CancellationToken ct)
    {
        if (info.InstallerUrl is null) { Fail("This release has no installer for Windows."); return; }
        var expected = await ExpectedHashAsync(info, ct);
        if (expected is null) { Fail("This release has no checksum, so it was not downloaded."); return; }

        Directory.CreateDirectory(_folder);
        Tidy(keep: null);
        var target = Path.Combine(_folder, SafeInstallerName(info));
        var part = target + ".part";
        Progress = 0;
        Set(UpdateState.Downloading);

        using var stall = new CancellationTokenSource(StallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stall.Token);
        try
        {
            await using (var src = await Files.GetStreamAsync(info.InstallerUrl, linked.Token))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long got = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, linked.Token)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                    got += read;
                    stall.CancelAfter(StallTimeout); // still arriving: the watchdog waits again
                    if (info.InstallerSize > 0)
                    {
                        var p = (int)Math.Min(99, got * 100 / info.InstallerSize);
                        if (p != Progress) { Progress = p; Changed?.Invoke(); }
                    }
                }
            }
            if (await HashOfAsync(part) != expected)
            {
                TryDelete(part);
                Fail("The download did not match the release's checksum, so it was thrown away.");
                return;
            }
            File.Move(part, target, overwrite: true);
            ReadyInstaller = target;
            Progress = 100;
            Set(UpdateState.Ready);
        }
        catch (Exception) when (linked.IsCancellationRequested)
        {
            TryDelete(part);
            if (ct.IsCancellationRequested) { Set(UpdateState.Available); return; }
            Fail("The download stopped. It is tried again later.");
        }
        catch (Exception)
        {
            TryDelete(part);
            Fail("The update could not be downloaded. It is tried again later.");
        }
    }

    /// <summary>
    /// Runs the downloaded installer silently. With <paramref name="reopen"/> it starts Vireo again when done (and the session
    /// brings the tabs back); without, the update simply waits for the next start. Vireo must exit right after this.
    /// </summary>
    public bool Install(bool reopen, string? profile = null)
    {
        if (State != UpdateState.Ready || ReadyInstaller is not { } file || !File.Exists(file)) return false;
        try
        {
            var args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS" + (reopen ? " /REOPEN=1" : "");
            // Reopened in the profile it was closed in (a profile name is letters, digits and dashes: safe on a command line).
            if (reopen && profile is { Length: > 0 } && Profiles.ValidId(profile)) args += $" \"/REOPENARGS=--profile {profile}\"";
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, WorkingDirectory = _folder });
            return true;
        }
        catch { return false; }
    }

    /// <summary>The release notes page (only ever a web address; anything else opens the releases list).</summary>
    public void OpenReleasePage()
    {
        var url = Latest?.ReleaseUrl is { } u && Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme is "https" or "http" ? u : ReleasesPage;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>How long until the next automatic check: soon while it keeps failing (no network yet), then daily.</summary>
    internal static TimeSpan NextCheckIn(int failuresInARow) => failuresInARow switch
    {
        <= 0 => TimeSpan.FromHours(24),
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromHours(1),
    };

    /// <summary>Reads GitHub's release description: the version from its tag, and Vireo's installer and checksum from its files.</summary>
    internal static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!TryParseVersion(tag, out var version)) return null;
        string? installerUrl = null, installerName = null, checksumUrl = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var list = assets.EnumerateArray().Select(a => (
                Name: a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Url: a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null,
                Size: a.TryGetProperty("size", out var s) && s.TryGetInt64(out var z) ? z : 0)).ToList();
            // Vireo's own installer by name: a release that also carries some other .exe must never have that one run.
            var setup = list.FirstOrDefault(a => a.Name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (setup.Url is not null)
            {
                installerUrl = setup.Url; installerName = setup.Name; size = setup.Size;
                checksumUrl = list.FirstOrDefault(a => string.Equals(a.Name, setup.Name + ".sha256", StringComparison.OrdinalIgnoreCase)).Url;
            }
        }
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new UpdateInfo(version, tag, page, installerUrl, installerName, size, checksumUrl, notes);
    }

    /// <summary>"v1.4.2", "1.4", "v2.0.0-beta" (its numbers) into a version; anything else is not one.</summary>
    internal static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var core = new string(tag.Trim().TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (core.Length == 0) return false;
        var parts = core.Split('.');
        int Part(int i) => parts.Length > i && int.TryParse(parts[i], out var n) ? n : 0;
        version = new Version(Part(0), Part(1), Part(2));
        return true;
    }

    /// <summary>The hex SHA-256 at the start of a checksum file ("abc… VireoSetup.exe" or just the hash).</summary>
    internal static string? ParseSha256(string text)
    {
        var first = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is { Length: 64 } && first.All(Uri.IsHexDigit) ? first.ToLowerInvariant() : null;
    }

    /// <summary>The release's file name reduced to a plain file name of the expected kind (it comes from the server).</summary>
    internal static string SafeInstallerName(UpdateInfo info)
    {
        var fallback = $"{InstallerPrefix}-{Label(info.Version)}.exe";
        if (string.IsNullOrWhiteSpace(info.InstallerName)) return fallback;
        string name;
        try { name = Path.GetFileName(info.InstallerName); } catch { return fallback; }
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return fallback;
        // Versioned on disk, so a download for one release is never mistaken for another's.
        return $"{Path.GetFileNameWithoutExtension(name)}-{Label(info.Version)}.exe";
    }

    private async Task<string?> ExpectedHashAsync(UpdateInfo info, CancellationToken ct)
    {
        if (info.ChecksumUrl is null) return null;
        try { return ParseSha256(await Api.GetStringAsync(info.ChecksumUrl, ct)); }
        catch { return null; }
    }

    private static async Task<string> HashOfAsync(string path)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(s)).ToLowerInvariant();
    }

    /// <summary>Removes older downloads (and any half-finished one), keeping <paramref name="keep"/>.</summary>
    private void Tidy(string? keep)
    {
        try
        {
            if (!Directory.Exists(_folder)) return;
            foreach (var f in Directory.EnumerateFiles(_folder))
                if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
        }
        catch { /* tidied next time */ }
    }

    /// <summary>After an update has installed: the installer it came from is not needed any more.</summary>
    public void ForgetInstalled() => Tidy(keep: null);

    private void Fail(string why)
    {
        Error = why;
        Set(Latest is not null && Latest.Version > Current ? UpdateState.Available : UpdateState.Failed);
        if (State == UpdateState.Available) FailuresInARow++;
    }

    private void Set(UpdateState state)
    {
        State = state;
        Changed?.Invoke();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
