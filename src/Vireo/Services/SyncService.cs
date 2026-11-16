using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vireo.Services;

public sealed record SyncTab(string Url, string Title);

/// <summary>What one computer shares: its bookmarks, history and deletions, start-page sites and the tabs it has open.</summary>
public sealed record SyncPayload(int V, string DeviceId, string DeviceName, DateTime UpdatedUtc, SyncLibrary Library,
    List<PinnedSite> Pinned, DateTime? PinnedChangedUtc, List<SyncTab> Tabs);

/// <summary>Another computer, as last heard from.</summary>
public sealed record SyncDevice(string Id, string Name, DateTime UpdatedUtc, List<SyncTab> Tabs);

/// <summary>
/// The lock on the sync folder. A passphrase becomes a key (PBKDF2-SHA256, 310,000 rounds, with a random salt kept in the folder);
/// every file is sealed with AES-GCM under that key, so the cloud service (or anyone who gets the folder) sees only noise, and
/// a file that was changed is refused rather than read.
/// </summary>
public static class SyncVault
{
    public const string FolderName = "Vireo Sync";
    private static readonly byte[] Magic = "VSY1"u8.ToArray();
    private const string CheckText = "vireo-sync-ok";
    private sealed record VaultFile(int V, string Salt, string Check);

    /// <summary>The folder Vireo writes into: a "Vireo Sync" folder inside the one chosen (or that one, if it is already it).</summary>
    public static string FolderIn(string chosen) =>
        string.Equals(Path.GetFileName(chosen.TrimEnd('\\', '/')), FolderName, StringComparison.OrdinalIgnoreCase) ? chosen : Path.Combine(chosen, FolderName);

    public static bool Exists(string folder) => File.Exists(Path.Combine(folder, "vault.json"));

    public static byte[] DeriveKey(string passphrase, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 310_000, HashAlgorithmName.SHA256, 32);

    /// <summary>
    /// The key for this folder: made (with a new salt) when the folder is new, else checked against it. Null when the passphrase
    /// is not the one the folder was set up with.
    /// </summary>
    public static byte[]? Open(string folder, string passphrase)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "vault.json");
        if (!File.Exists(path))
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var key = DeriveKey(passphrase, salt);
            var vault = new VaultFile(1, Convert.ToBase64String(salt), Convert.ToBase64String(Seal(key, Encoding.UTF8.GetBytes(CheckText))));
            File.WriteAllText(path, JsonSerializer.Serialize(vault));
            return key;
        }
        var existing = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(path)) ?? throw new InvalidDataException("The sync folder's lock file is damaged.");
        var k = DeriveKey(passphrase, Convert.FromBase64String(existing.Salt));
        var check = Unseal(k, Convert.FromBase64String(existing.Check));
        return check is not null && Encoding.UTF8.GetString(check) == CheckText ? k : null;
    }

    public static byte[] Seal(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Magic);
        return [.. Magic, .. nonce, .. tag, .. cipher];
    }

    /// <summary>The opened contents, or null when the key is wrong or the file was changed.</summary>
    public static byte[]? Unseal(byte[] key, byte[] data)
    {
        if (data.Length < 32 || !data.AsSpan(0, 4).SequenceEqual(Magic)) return null;
        var plain = new byte[data.Length - 32];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(4, 12), data.AsSpan(32), data.AsSpan(16, 16), plain, Magic);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }
}

/// <summary>
/// Sync between computers without a server of ours: the person picks a folder that OneDrive, Google Drive or Dropbox already
/// keeps in step, and each computer writes one sealed file there (its own, so the cloud never has to merge two writes) and reads
/// the others'. Bookmarks, history (with deletions), start-page sites and open tabs travel; passwords never do.
/// </summary>
public sealed class SyncService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _profile;
    private readonly Settings _settings;
    private readonly Library _library;
    private readonly Func<List<SyncTab>> _openTabs;
    private readonly Action<Action> _onUi;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;

    public DateTime? LastSync { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<SyncDevice> Devices { get; private set; } = [];
    public event Action? Changed;

    /// <param name="onUi">Runs a change to the settings where the windows can see it.</param>
    public SyncService(string profileFolder, Settings settings, Library library, Func<List<SyncTab>> openTabs, Action<Action> onUi)
    {
        _profile = profileFolder;
        _settings = settings;
        _library = library;
        _openTabs = openTabs;
        _onUi = onUi;
    }

    private string KeyPath => Path.Combine(_profile, "sync.key");
    public bool IsOn => !string.IsNullOrEmpty(_settings.SyncFolder) && File.Exists(KeyPath);
    public string? Folder => _settings.SyncFolder;

    /// <summary>Syncs a few seconds after start, then every two minutes while Vireo is open.</summary>
    public void Start()
    {
        _timer = new Timer(_ => _ = SyncNowAsync(), null, TimeSpan.FromSeconds(8), TimeSpan.FromMinutes(2));
    }

    /// <summary>Sets up sync in a folder. Returns a sentence saying what is wrong, or null when it is on.</summary>
    public async Task<string?> TurnOnAsync(string chosenFolder, string passphrase, string deviceName)
    {
        if (passphrase.Length < 8) return "Use a passphrase of at least 8 characters.";
        var folder = SyncVault.FolderIn(chosenFolder);
        byte[]? key;
        try { key = await Task.Run(() => SyncVault.Open(folder, passphrase)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or JsonException)
        { return "That folder cannot be used: " + e.Message; }
        if (key is null) return "That is not the passphrase this sync folder was set up with.";
        Directory.CreateDirectory(_profile);
        // The key stays on this computer, in the profile's own folder (like other browsers' own keys); the passphrase is not kept.
        await File.WriteAllTextAsync(KeyPath, Convert.ToBase64String(key));
        _onUi(() =>
        {
            _settings.SyncFolder = folder;
            if (string.IsNullOrEmpty(_settings.SyncDeviceId)) _settings.SyncDeviceId = Guid.NewGuid().ToString("N");
            _settings.SyncDeviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName.Trim();
            _settings.Commit();
        });
        await SyncNowAsync();
        return LastError;
    }

    /// <summary>Stops syncing here. With <paramref name="forget"/>, this computer's file is taken out of the folder too.</summary>
    public void TurnOff(bool forget)
    {
        try
        {
            if (forget && Folder is { } f && !string.IsNullOrEmpty(_settings.SyncDeviceId)) File.Delete(Path.Combine(f, _settings.SyncDeviceId + ".vsync"));
            File.Delete(KeyPath);
        }
        catch { /* gone already */ }
        _settings.SyncFolder = null;
        _settings.Commit();
        Devices = [];
        LastSync = null;
        LastError = null;
        Changed?.Invoke();
    }

    /// <summary>Writes this computer's file and takes in everyone else's.</summary>
    public async Task SyncNowAsync()
    {
        if (!IsOn || !await _gate.WaitAsync(0)) return;
        try
        {
            var folder = _settings.SyncFolder!;
            var key = Convert.FromBase64String(await File.ReadAllTextAsync(KeyPath));
            var me = _settings.SyncDeviceId;
            if (string.IsNullOrEmpty(me)) { me = Guid.NewGuid().ToString("N"); _onUi(() => { _settings.SyncDeviceId = me; _settings.Commit(); }); }

            // Read the others first, so what they deleted is not written back out.
            var payloads = new List<SyncPayload>();
            var unreadable = 0;
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.vsync"))
                {
                    if (Path.GetFileNameWithoutExtension(file).StartsWith(me, StringComparison.Ordinal)) continue;
                    try
                    {
                        if (Unpack(key, File.ReadAllBytes(file)) is { } p) payloads.Add(p);
                        else unreadable++;
                    }
                    catch (IOException) { /* being written by the cloud client: next time */ }
                }
            });
            _library.MergeFromSync([.. payloads.Select(p => p.Library)]);
            var newestPinned = payloads.Where(p => p.PinnedChangedUtc is not null).OrderByDescending(p => p.PinnedChangedUtc).FirstOrDefault();
            if (newestPinned is not null && (_settings.PinnedSitesChangedAt is null || newestPinned.PinnedChangedUtc > _settings.PinnedSitesChangedAt))
                _onUi(() =>
                {
                    _settings.PinnedSites = [.. newestPinned.Pinned.Where(p => p.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)).Take(10)];
                    _settings.PinnedSitesChangedAt = newestPinned.PinnedChangedUtc;
                    _settings.Commit();
                });

            List<SyncTab> tabs = [];
            _onUi(() => tabs = _openTabs());
            var mine = new SyncPayload(1, me, _settings.SyncDeviceName ?? Environment.MachineName, DateTime.UtcNow, _library.ForSync(),
                [.. _settings.PinnedSites], _settings.PinnedSitesChangedAt, tabs);
            var sealedBytes = Pack(key, mine);
            await Task.Run(() =>
            {
                var path = Path.Combine(folder, me + ".vsync");
                File.WriteAllBytes(path + ".tmp", sealedBytes);
                File.Move(path + ".tmp", path, overwrite: true);
            });

            Devices = [.. payloads.GroupBy(p => p.DeviceId).Select(g => g.OrderByDescending(p => p.UpdatedUtc).First())
                .OrderByDescending(p => p.UpdatedUtc).Select(p => new SyncDevice(p.DeviceId, p.DeviceName, p.UpdatedUtc, p.Tabs))];
            LastSync = DateTime.Now;
            LastError = unreadable > 0 ? $"{unreadable} file{(unreadable == 1 ? "" : "s")} in the folder could not be opened with this passphrase." : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or JsonException or CryptographicException)
        {
            LastError = Directory.Exists(_settings.SyncFolder ?? "") ? "Sync could not finish: " + e.Message : "The sync folder is not there (is the drive or cloud folder available?).";
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public static byte[] Pack(byte[] key, SyncPayload payload)
    {
        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) JsonSerializer.Serialize(gz, payload, Json);
        return SyncVault.Seal(key, buffer.ToArray());
    }

    public static SyncPayload? Unpack(byte[] key, byte[] data)
    {
        if (SyncVault.Unseal(key, data) is not { } plain) return null;
        using var gz = new GZipStream(new MemoryStream(plain), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<SyncPayload>(gz, Json);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }
}
