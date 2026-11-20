using Vireo.Services;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>
    /// A page's sound carries Vireo's name and icon (what the Windows Volume mixer shows), not the engine helper's. The page plays
    /// the test song at a whisper so the test is close to silent. Skipped on a PC with no sound output.
    /// </summary>
    private async Task RunSoundNameStepsAsync()
    {
        var tab = await w.OpenTabAsync(baseUrl + "/pages/file.html");
        await WaitFor(() => !tab.IsLoading && tab.Title.Length > 0);
        await w.EvalAsync(tab, $"(() => {{ const a = new Audio('{baseUrl}/song.mp3'); a.loop = true; a.volume = 0.003; window.__song = a; a.play().catch(e => window.__songError = String(e)); }})()");
        // Asked of the page: at a whisper the engine does not call the tab "playing sound", but the sound is there all the same.
        var playing = false;
        for (var i = 0; i < 50 && !playing; i++)
        {
            playing = await w.EvalAsync(tab, "!!window.__song && !window.__song.paused && window.__song.currentTime > 0") == "true";
            if (!playing) await Task.Delay(200);
        }
        if (!playing && SoundLabel.LastError.Length > 0)
        {
            Console.WriteLine($"SKIP  sound name (no sound output: {SoundLabel.LastError})");
            await w.CloseTabAsync(tab);
            return;
        }
        Check("a page plays sound", playing, await w.EvalAsync(tab, "JSON.stringify({ paused: window.__song && window.__song.paused, at: window.__song && window.__song.currentTime, error: window.__songError || '' })"));

        List<(int Pid, string Name, string Icon)> ours = [];
        var others = 0;
        var icon = (Environment.ProcessPath ?? "") + ",0";
        bool Labelled() => ours.Count > 0 && ours.All(e => e.Name == SoundLabel.Name && string.Equals(e.Icon, icon, StringComparison.OrdinalIgnoreCase));
        for (var i = 0; i < 50 && !Labelled(); i++)
        {
            (ours, others) = await Task.Run(SoundLabel.Survey);
            if (!Labelled()) await Task.Delay(100);
        }
        var seen = string.Join("; ", ours.Select(e => $"pid {e.Pid} '{e.Name}' '{e.Icon}'"));
        Check("its sound shows as Vireo, with Vireo's icon", Labelled(), $"{seen}{(SoundLabel.LastError.Length > 0 ? "  error: " + SoundLabel.LastError : "")}");
        Console.WriteLine($"INFO  sound entries labelled this run: {SoundLabel.Labelled}; other programs' entries named Vireo: {others}");

        await w.EvalAsync(tab, "window.__song && window.__song.pause()");
        await w.CloseTabAsync(tab);
    }
}
