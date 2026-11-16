using System.Diagnostics;
using System.IO;
using Vireo.Services;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>Sync through a shared folder with a second "computer", and a second profile in its own Vireo.</summary>
    private async Task RunProfileAndSyncStepsAsync()
    {
        // ---- Sync: this browser and a pretend laptop share one folder ----
        var cloud = Path.Combine(Path.GetTempPath(), "vireo-selftest-cloud-" + Guid.NewGuid().ToString("N"));
        var laptopProfile = Path.Combine(Path.GetTempPath(), "vireo-selftest-laptop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cloud);
        try
        {
            w.ShowSync();
            await Task.Delay(600);
            var sync = MainWindow.Sync!;
            var problem = await sync.TurnOnAsync(cloud, "a long passphrase", "Desk PC");
            Check("sync turns on with a cloud folder and a passphrase", problem is null && sync.IsOn, problem);

            var laptopSettings = Settings.Load(laptopProfile);
            using var laptopLibrary = new Library(null);
            using var laptop = new SyncService(laptopProfile, laptopSettings, laptopLibrary, () => [new SyncTab("https://laptop.example/reading", "Reading on the laptop")], a => a());
            var wrong = await laptop.TurnOnAsync(cloud, "not the passphrase", "Laptop");
            Check("another computer with the wrong passphrase is refused", wrong is not null && !laptop.IsOn, wrong);
            var joined = await laptop.TurnOnAsync(cloud, "a long passphrase", "Laptop");
            Check("and with the right one joins", joined is null && laptop.IsOn, joined);

            laptopLibrary.ToggleBookmark("https://synced.example/from-laptop", "Saved on the laptop");
            await laptop.SyncNowAsync();
            await sync.SyncNowAsync();
            Check("a bookmark made on the other computer arrives here", w.Library.IsBookmarked("https://synced.example/from-laptop"));
            Check("and its open tabs are listed", sync.Devices.Any(d => d.Name == "Laptop" && d.Tabs.Any(t => t.Url == "https://laptop.example/reading")),
                string.Join(",", sync.Devices.Select(d => d.Name)));
            var files = Directory.GetFiles(SyncVault.FolderIn(cloud), "*.vsync");
            Check("each computer writes one sealed file, unreadable without the passphrase", files.Length == 2 && files.All(f => !File.ReadAllText(f).Contains("synced.example")), $"{files.Length} files");

            w.Library.RemoveBookmark("https://synced.example/from-laptop");
            await sync.SyncNowAsync();
            await laptop.SyncNowAsync();
            Check("deleting it here deletes it there", !laptopLibrary.IsBookmarked("https://synced.example/from-laptop"));
            w.ShowSync();
            await Task.Delay(800);
            await Shot("44-sync");
            sync.TurnOff(forget: true);
            w.CloseDrawer();
        }
        finally
        {
            try { Directory.Delete(cloud, true); } catch { }
            try { Directory.Delete(laptopProfile, true); } catch { }
        }

        // ---- Profiles: a second one runs as its own Vireo with its own data ----
        var root = Path.Combine(Path.GetTempPath(), "vireo-selftest-profiles-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("VIREO_PROFILES_ROOT", root);
        Process? other = null;
        try
        {
            var work = Profiles.Add("Work", Profiles.Colors[1]);
            w.ShowProfiles();
            await Task.Delay(800);
            await Shot("45-profiles");
            var before = Process.GetProcessesByName("Vireo").Select(p => p.Id).ToHashSet();
            Profiles.Launch(work.Id);
            for (var i = 0; i < 60 && other is null; i++)
            {
                await Task.Delay(500);
                other = Process.GetProcessesByName("Vireo").FirstOrDefault(p => !before.Contains(p.Id));
            }
            string title = "";
            for (var i = 0; i < 40 && other is not null && !title.Contains("Work"); i++) { await Task.Delay(500); other.Refresh(); title = other.MainWindowTitle; }
            Check("a second profile opens in its own Vireo", other is not null && title.Contains("Work"), title);
            Check("with a data folder of its own", Directory.Exists(Profiles.FolderOf(work.Id)) && Profiles.FolderOf(work.Id) != w.DataFolder, Profiles.FolderOf(work.Id));
            w.CloseDrawer();
        }
        finally
        {
            try { if (other is { HasExited: false }) { other.Kill(entireProcessTree: true); other.WaitForExit(5000); } } catch { }
            Environment.SetEnvironmentVariable("VIREO_PROFILES_ROOT", null);
            await Task.Delay(1500);
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
