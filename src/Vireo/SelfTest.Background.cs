using System.Diagnostics;
using System.IO;
using System.Windows;
using Vireo.Controls;
using Vireo.Downloads;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>
    /// Closing Vireo while a download runs, choosing "Keep downloading": the browser and its page engine go away entirely, the
    /// download carries on and finishes with only the downloader running, and opening Vireo again brings the browser back with
    /// the download in its list. Runs last: it closes the test's window.
    /// </summary>
    private async Task RunBackgroundStepsAsync()
    {
        var folder = Path.Combine(outDir, "background-downloads");
        Directory.CreateDirectory(folder);
        foreach (var other in Application.Current.Windows.OfType<MainWindow>().Where(x => x != w).ToList()) other.Close();
        await Task.Delay(500);

        var item = w.Downloads.Add(new DownloadRequest(baseUrl + "/generated/30000000.bin?throttle=150", "background.bin", Folder: folder));
        await WaitFor(() => item.State == DownloadState.Downloading && item.Downloaded > 0, 15000);
        var engineProcesses = await w.EngineProcessIdsAsync();
        GC.Collect();
        var before = Process.GetCurrentProcess().WorkingSet64;

        var asked = 0;
        MainWindow.AnswerCloseQuestion = () => { asked++; return CloseChoice.Background; };
        w.Close();
        await WaitFor(() => MainWindow.InBackground, 5000);
        Check("closing with a download running asks what to do", asked == 1, $"asked {asked} times");
        Check("keep downloading: the browser closes and only the downloader stays", MainWindow.InBackground && !Application.Current.Windows.OfType<MainWindow>().Any(),
            $"{Application.Current.Windows.OfType<MainWindow>().Count()} windows");
        Check("and the download carries on", item.State is DownloadState.Downloading or DownloadState.Queued, item.State.ToString());

        // The page engine and all its processes are gone.
        bool Alive(int id) { try { using var p = Process.GetProcessById(id); return !p.HasExited; } catch { return false; } }
        await WaitFor(() => engineProcesses.All(id => !Alive(id)), 20000);
        Check("the page engine's processes have all ended", engineProcesses.Count > 0 && engineProcesses.All(id => !Alive(id)),
            $"{engineProcesses.Count(Alive)} of {engineProcesses.Count} still running");
        await Task.Delay(6000); // the memory is given back a few seconds after the windows close
        var gc = GC.GetGCMemoryInfo();
        Console.WriteLine($"MEMORY  .NET objects alive {GC.GetTotalMemory(true) / 1048576} MB, .NET heap holds {gc.HeapSizeBytes / 1048576} MB, committed {gc.TotalCommittedBytes / 1048576} MB, working set {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB");
        // What Task Manager shows: the memory really in use (working set), not memory merely reserved.
        var inUse = Process.GetCurrentProcess().WorkingSet64;
        Console.WriteLine($"MEMORY  downloader alone: {inUse / 1048576} MB in use (browser before: {before / 1048576} MB in use, plus {engineProcesses.Count} engine processes)");
        Check("the downloader alone is light: under 120 MB in use", inUse < 120L * 1024 * 1024, $"{inUse / 1048576} MB");
        Check("the tray icon says how the download is going", MainWindow.TrayTip?.StartsWith("Vireo: 1 downloading") == true, MainWindow.TrayTip);

        // Pictures of the two new windows: the tray's downloads window (while the download runs) and the closing question.
        MainWindow.ShowTrayWindowForTest();
        await Task.Delay(1200);
        SavePicture(MainWindow.TrayWindowForTest, "62-tray-downloads.png");
        MainWindow.HideTrayWindowForTest();
        var dialog = new CloseDownloadsDialog(null, 2) { WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, Left = -4000, Top = 0 };
        dialog.Show();
        await Task.Delay(700);
        SavePicture(dialog, "61-close-question.png");
        dialog.Close();

        await WaitFor(() => item.State == DownloadState.Completed, 90000);
        Check("the download finishes with only the downloader running", item.State == DownloadState.Completed && new FileInfo(item.FinalPath).Length == 30_000_000, $"{item.State} {item.Error}");
        await WaitFor(() => MainWindow.TrayTip == "Vireo: downloads finished", 5000);
        Check("and the tray icon says it is done", MainWindow.TrayTip == "Vireo: downloads finished", MainWindow.TrayTip);

        MainWindow.OpenBrowserFromBackground(null);
        await WaitFor(() => Application.Current.Windows.OfType<MainWindow>().Any(x => x.IsLoaded), 15000);
        var again = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        Check("opening Vireo again brings the browser back", again is not null && !MainWindow.InBackground);
        Check("with the finished download in its list", again is not null && again.Downloads.Items.Any(i => i.Id == item.Id && i.State == DownloadState.Completed));
        MainWindow.AnswerCloseQuestion = null;
    }

    /// <summary>Draws a window's contents to a PNG in the out folder (works for windows that are off screen).</summary>
    private void SavePicture(Window? window, string name)
    {
        // The whole window (its own background and margins included), not only its content.
        if (window is not FrameworkElement root || root.ActualWidth < 1) { Console.WriteLine($"SKIP  picture {name}"); return; }
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var f = File.Create(Path.Combine(outDir, name));
        png.Save(f);
    }
}
