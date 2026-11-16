namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>Several windows: they share one Vireo, and a tab moves to a window of its own with its page alive.</summary>
    private async Task RunWindowStepsAsync()
    {
        using var site = new PageSite();
        site.Html("/one.html", "<!doctype html><title>One</title><body>one</body>");
        site.Html("/two.html", "<!doctype html><title>Two</title><body><input id=box></body>");

        var second = w.OpenWindow();
        await second.Ready;
        await WaitFor(() => second.AllTabs.Count == 1 && second.Active is not null);
        Check("Ctrl+N opens a second window", MainWindow.NormalWindows.Count == 2 && second.AllTabs.Count == 1, $"{MainWindow.NormalWindows.Count} windows");
        Check("which shares the downloads, history and settings", second.Downloads == w.Downloads && second.Library == w.Library && second.Settings == w.Settings);
        second.Close();
        await WaitFor(() => MainWindow.NormalWindows.Count == 1);
        Check("and closing it leaves the first", MainWindow.NormalWindows.Count == 1 && w.AllTabs.Count > 0);

        // A tab with history and something typed into it
        var tab = await w.OpenTabAsync(site.Base + "/one.html");
        await WaitFor(() => !tab.IsLoading && tab.Title == "One");
        w.NavigateActive(site.Base + "/two.html");
        await WaitFor(() => !tab.IsLoading && tab.Title == "Two");
        await w.EvalAsync(tab, "document.getElementById('box').value = 'typed before the move'");
        var before = w.AllTabs.Count;

        var moved = await w.MoveToNewWindowAsync(tab);
        Check("a tab moves to a window of its own", moved is not null && moved.AllTabs.Contains(tab) && !w.AllTabs.Contains(tab) && w.AllTabs.Count == before - 1,
            $"{moved?.AllTabs.Count} there, {w.AllTabs.Count} here");
        if (moved is null) return;
        await WaitFor(() => moved.Active == tab);
        Check("and is the one shown there", moved.Active == tab && tab.Owner == moved);
        var typed = await moved.EvalAsync(tab, "document.getElementById('box').value");
        Check("its page lives on (what was typed is still there)", typed == "\"typed before the move\"", typed);
        await Shot("43-tab-in-new-window");
        moved.NavigateActive(site.Base + "/one.html");
        await WaitFor(() => !tab.IsLoading && tab.Title == "One");
        tab.View!.CoreWebView2.GoBack();
        await WaitFor(() => tab.Title == "Two");
        Check("with its history, and it works in its new window", tab.Title == "Two" && moved.Active == tab, tab.Title);
        Check("the window it came from still works", w.Active is not null && w.AllTabs.All(t => t.Owner == w));
        // While a tab is dragged away from its strip, a picture of it follows the mouse.
        var screenAt = w.PointToScreen(new System.Windows.Point(520, 300));
        w.PreviewTabDrag(w.AllTabs[0], screenAt);
        await Task.Delay(1200);
        await Shot("46-tab-drag-preview");
        Check("a dragged tab shows a picture of itself under the mouse", w.DragPreviewShown);
        w.PreviewTabDrag(w.AllTabs[0], null);
        // Dropping a tab onto another window's tab strip joins it there; a window left with no tabs closes.
        var other = await w.OpenTabAsync(site.Base + "/one.html");
        await WaitFor(() => !other.IsLoading && other.Title == "One");
        var strip = moved.TabStripScreenRect()!.Value;
        var dropAt = new System.Windows.Point(strip.Left + 30, strip.Top + strip.Height / 2);
        Check("another window's tab strip is found under the mouse", w.WindowWithTabsAt(dropAt) == moved);
        await w.DropTabAsync(other, moved, dropAt);
        await WaitFor(() => moved.AllTabs.Contains(other));
        Check("a tab dropped onto another window's tabs joins that window, page alive", moved.AllTabs.Contains(other) && !w.AllTabs.Contains(other) && other.Title == "One" && other.Owner == moved,
            $"{moved.AllTabs.Count} there");
        Check("at the place it was dropped", moved.AllTabs.ToList().IndexOf(other) == 0, moved.AllTabs.ToList().IndexOf(other).ToString());
        var mainStrip = w.TabStripScreenRect()!.Value;
        var back = new System.Windows.Point(mainStrip.Right - 80, mainStrip.Top + mainStrip.Height / 2);
        await moved.DropTabAsync(other, w, back);
        await moved.DropTabAsync(tab, w, back);
        await WaitFor(() => MainWindow.NormalWindows.Count == 1);
        Check("dragging a window's last tab into another window closes the empty one", MainWindow.NormalWindows.Count == 1 && w.AllTabs.Contains(tab) && w.AllTabs.Contains(other));

        // Many tabs: the menu stays short (its list moved to the Tabs panel), and the panel finds, switches and closes tabs.
        for (var i = 0; i < 14; i++) await w.OpenTabAsync(site.Base + (i % 2 == 0 ? "/one.html" : "/two.html"));
        await WaitFor(() => w.AllTabs.All(t => !t.IsLoading));
        w.ShowMenu();
        await Task.Delay(500);
        var menuHeight = w.MenuHeight;
        await Shot("47-menu-many-tabs");
        Check("with many tabs open the menu still fits on the screen", menuHeight > 0 && menuHeight <= System.Windows.SystemParameters.WorkArea.Height - 100, $"{menuHeight:0} of {System.Windows.SystemParameters.WorkArea.Height:0}");
        w.HideMenu();
        w.ShowAllTabs();
        await Task.Delay(400);
        var panel = w.DrawerContent as Controls.TabsPanel;
        Check("Ctrl+Shift+A / See all tabs opens the Tabs panel listing every tab", panel is not null && panel.RowCount == w.AllTabs.Count, $"{panel?.RowCount} rows, {w.AllTabs.Count} tabs");
        await Shot("48-tabs-panel");
        panel?.Search("Two");
        await Task.Delay(200);
        var twos = w.AllTabs.Count(t => t.Title == "Two");
        Check("typing in it narrows the list", panel?.RowCount == twos, $"{panel?.RowCount} rows, {twos} named Two");
        var target = w.AllTabs.First(t => t.Title == "Two" && t != w.Active);
        if (panel is not null) await panel.OpenAsync(target);
        Check("and clicking one switches to it and closes the panel", w.Active == target && w.DrawerContent is null);
        var count = w.AllTabs.Count;
        foreach (var t in w.AllTabs.Skip(2).ToList()) await w.CloseTabAsync(t);
        Check("tabs close from it", w.AllTabs.Count == 2, $"{count} -> {w.AllTabs.Count}");

        // A notice (a download finished) while another program is in front: no popup over it; it waits for Vireo to come back.
        w.ForegroundForTest = false;
        w.ShowToast("Download finished", "report.pdf");
        Check("a notice never pops up over other programs", !w.ToastShowing && w.ToastWaiting);
        w.ForegroundForTest = true;
        w.ShowWaitingToastForTest();
        await WaitFor(() => w.ToastShowing, 3000);
        Check("it shows when Vireo is brought back", w.ToastShowing && !w.ToastWaiting);
        w.ForegroundForTest = null;
    }
}
