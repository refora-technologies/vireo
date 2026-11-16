namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>A public ad-blocking test page (adblock.turtlecute.org): its score, as a person would see it. Needs the internet.</summary>
    private async Task RunAdTestStepsAsync()
    {
        w.Settings.BlockAds = true;
        w.Settings.UseCommunityLists = true;
        w.Settings.Commit();
        await WaitFor(() => w.CommunityListsReady, 90000);
        Console.WriteLine($"ADTEST-LISTS  ready={w.CommunityListsReady}");
        // How a refused request looks to the page: it must fail like a dead connection, not answer.
        var probe = await w.OpenTabAsync(baseUrl + "/pages/file.html");
        await WaitFor(() => !probe.IsLoading);
        await w.EvalAsync(probe, "window.__r = 'pending'; fetch('https://ad.doubleclick.net/ddm/trackclk/x', { mode: 'no-cors' }).then(r => window.__r = 'answered ' + r.type + ' ' + r.status).catch(e => window.__r = 'failed ' + e)");
        await Task.Delay(4000);
        Console.WriteLine("ADTEST-PROBE  " + await w.EvalAsync(probe, "window.__r"));
        await w.EvalAsync(probe, "window.__s = 'pending'; var s = document.createElement('script'); s.src = 'https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js'; s.onload = () => window.__s = 'loaded'; s.onerror = () => window.__s = 'error'; document.head.appendChild(s)");
        await Task.Delay(3000);
        Console.WriteLine("ADTEST-SCRIPT  " + await w.EvalAsync(probe, "window.__s"));
        var tab = await w.OpenTabAsync("https://adblock.turtlecute.org/");
        await WaitFor(() => !tab.IsLoading, 30000);
        await Task.Delay(25000); // the page runs its checks one after another
        var text = await w.EvalAsync(tab, "document.body ? document.body.innerText.replace(/\\s+/g, ' ').slice(0, 4000) : ''");
        Console.WriteLine("ADTEST  " + text);
        await Shot("50-adblock-test");
        var failed = await w.EvalAsync(tab, "JSON.stringify([...document.querySelectorAll('*')].filter(e => e.children.length === 0 && /not blocked/i.test(e.textContent)).map(e => (e.closest('[id]') || e.parentElement).id || e.parentElement.textContent.trim().slice(0, 120)).slice(0, 80))");
        Console.WriteLine("ADTEST-MISSED  " + failed);
        Console.WriteLine("ADTEST-RED  " + await w.EvalAsync(tab, "JSON.stringify([...document.querySelectorAll('._bg-red')].map(e => e.textContent.trim().slice(0, 60)))"));
        var total = await w.EvalAsync(tab, "(document.body.innerText.match(/Total:\\s*\\d+\\s*(\\d+)\\s*blocked/) || [])[1] || ''");
        Console.WriteLine("ADTEST-SCORE  " + total);
    }
}
