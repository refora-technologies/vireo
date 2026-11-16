using System.IO;
using Microsoft.Web.WebView2.Core;

namespace Vireo;

internal sealed partial class SelfTest
{
    private async Task RunPrivacyStepsAsync()
    {
        using var site = new AdSite();
        w.Settings.BlockAds = false; // keep this part about links and connections only
        w.Settings.StripTrackingParams = true;
        // Downloads in this step land in the test's folder (a private window reads the settings when it opens).
        Directory.CreateDirectory(Path.Combine(outDir, "private-downloads"));
        w.Settings.DownloadFolder = Path.GetFullPath(Path.Combine(outDir, "private-downloads"));
        w.Settings.HttpsOnly = false;
        w.Settings.Commit();

        // Links are cleaned before the page is asked for.
        var tab = await w.OpenTabAsync(site.Base + "/ads.html?utm_source=mail&id=5&fbclid=zz#top");
        await WaitFor(() => !tab.IsLoading && tab.Title == "Shop");
        Check("tracking tags are removed from a link", tab.Url == site.Base + "/ads.html?id=5#top", tab.Url);

        // The browser's own tracker list follows the setting.
        w.Settings.Tracking = Services.TrackingLevel.Strict;
        w.Settings.Commit();
        await Task.Delay(500);
        Check("tracking prevention level reaches the engine", w.TrackingLevelNow == CoreWebView2TrackingPreventionLevel.Strict, w.TrackingLevelNow.ToString());
        w.Settings.Tracking = Services.TrackingLevel.Balanced;
        w.Settings.Commit();

        // Secure connections: a name that cannot be reached over https gets the explanation page.
        w.Settings.HttpsOnly = true;
        w.Settings.Commit();
        var local = await w.OpenTabAsync(site.Base + "/ads.html");
        await WaitFor(() => !local.IsLoading && local.Title == "Shop");
        Check("local addresses are never upgraded", local.Url.StartsWith("http://127.0.0.1"), local.Url);

        var secure = await w.OpenTabAsync("http://no-such-host.vireo-test.invalid/page");
        await WaitFor(() => secure.Title == "Not secure", 20000);
        Check("an http site with no secure version asks first", secure.Title == "Not secure", $"'{secure.Title}'");
        await Shot("16-not-secure");
        await w.RunScriptAsync(secure, "document.querySelector('button').click()");
        await Task.Delay(1500);
        Check("choosing to continue is remembered for that site", w.HttpAllowedFor("no-such-host.vireo-test.invalid"));
        w.Settings.HttpsOnly = false;
        w.Settings.Commit();

        // A private window shares nothing with the normal one.
        await w.CloseTabAsync(secure);
        var normal = await w.OpenTabAsync(site.Base + "/ads.html");
        await WaitFor(() => !normal.IsLoading && normal.Title == "Shop");
        await w.RunScriptAsync(normal, "document.cookie='who=normal; path=/'; localStorage.setItem('who','normal')");

        var win = w.OpenPrivateWindowForTest();
        await win.Ready;
        var priv = await win.OpenTabAsync(site.Base + "/ads.html");
        await WaitFor(() => !priv.IsLoading && priv.Title == "Shop");
        var seen = await win.EvalAsync(priv, "JSON.stringify([document.cookie, localStorage.getItem('who')])");
        Check("a private window starts with none of your cookies or site data", System.Text.Json.JsonSerializer.Deserialize<string>(seen) == "[\"\",null]", seen);
        Check("and says it is private", win.IsPrivate && win.Title.Contains("Private"), win.Title);
        await win.RunScriptAsync(priv, "document.cookie='who=private; path=/'");
        await Task.Delay(300);
        await Shot("17-private-window");

        // A private window's download: the file is saved, but nothing else about it is written anywhere.
        // The first private download asks where private downloads go; that place is kept and used from then on.
        var chosen = Path.GetFullPath(Path.Combine(outDir, "private-chosen"));
        w.Settings.PrivateDownloadFolder = "";
        w.Settings.Commit();
        MainWindow.AnswerPrivateFolderQuestion = () => chosen;
        using (var files = new Site())
        {
            var dlTab = await win.OpenTabAsync(files.Base + "/page.html");
            await WaitFor(() => !dlTab.IsLoading && dlTab.Title == "Reports");
            await win.RunScriptAsync(dlTab, "document.getElementById('dl').click()");
            await WaitFor(() => win.Downloads.Items.Any(i => i.State is Downloads.DownloadState.Completed or Downloads.DownloadState.Failed), 20000);
            var got = win.Downloads.Items.FirstOrDefault();
            Check("a private window downloads like any other", got is { State: Downloads.DownloadState.Completed } && File.Exists(got.FinalPath), $"{got?.State} {got?.Error}");
            Check("its download list is kept in memory only", win.Downloads.IsPrivate && !Directory.Exists(Path.Combine(Path.GetTempPath(), "VireoPrivate")));
            Check("and never shows in the normal window's list", got is not null && w.Downloads.Items.All(i => i.Id != got.Id));
            Check("the first private download asks where private downloads go, and goes there", win.PrivateFolderQuestionsAsked == 1 && got is not null && string.Equals(Path.GetFullPath(got.Folder), chosen, StringComparison.OrdinalIgnoreCase), $"asked {win.PrivateFolderQuestionsAsked}, saved in {got?.Folder}");
            Check("the answer is kept for every private window", w.Settings.PrivateDownloadFolder == chosen, w.Settings.PrivateDownloadFolder);
            await win.RunScriptAsync(dlTab, "document.getElementById('dl').click()");
            await WaitFor(() => win.Downloads.Items.Count(i => i.State is Downloads.DownloadState.Completed or Downloads.DownloadState.Failed) >= 2, 20000);
            Check("and is not asked again", win.PrivateFolderQuestionsAsked == 1 && win.Downloads.Items.All(i => string.Equals(Path.GetFullPath(i.Folder), chosen, StringComparison.OrdinalIgnoreCase)), $"asked {win.PrivateFolderQuestionsAsked}");
            foreach (var i in win.Downloads.Items.Where(i => got is null || i.Id != got.Id)) try { File.Delete(i.FinalPath); } catch { }
            if (got is { State: Downloads.DownloadState.Completed })
            {
                var mark = File.ReadAllText(got.FinalPath + ":Zone.Identifier");
                Check("the file is marked as from the internet, without naming the site", mark.Contains("ZoneId=3") && !mark.Contains("Url="), mark.Replace("\r\n", " "));
                File.Delete(got.FinalPath);
            }
        }
        MainWindow.AnswerPrivateFolderQuestion = null;
        win.Close();
        await Task.Delay(700);
        var after = await w.EvalAsync(normal, "document.cookie");
        Check("private cookies never reach the normal window", after.Contains("who=normal") && !after.Contains("private"), after);
    }
}
