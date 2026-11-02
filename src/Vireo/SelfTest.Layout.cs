using System.IO;
using System.Text.Json;
using System.Windows;
using Vireo.Services;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>Tabs on every side, the folding side list, the drawer, and saved settings.</summary>
    private async Task RunLayoutStepsAsync()
    {
        var pagesTop = w.PagesSize;

        foreach (var side in new[] { TabSide.Bottom, TabSide.Left, TabSide.Right, TabSide.Top })
        {
            w.SetTabSide(side);
            await Task.Delay(700);
            var where = w.TabsPlacement;
            Check($"tabs move to the {side.ToString().ToLowerInvariant()}", where == side.ToString().ToLowerInvariant(), where);
            var size = w.PagesSize;
            if (side is TabSide.Left or TabSide.Right)
                Check($"page makes room for the {side.ToString().ToLowerInvariant()} list", Math.Abs(pagesTop.Width - size.Width - (248 + 8)) < 3, $"{pagesTop.Width:0} -> {size.Width:0}");
            else
                Check($"page uses the full width with tabs {side.ToString().ToLowerInvariant()}", Math.Abs(pagesTop.Width - size.Width) < 3, $"{size.Width:0}");
            Check($"every tab is shown with tabs {side.ToString().ToLowerInvariant()}", w.VisibleTabHeaders == w.AllTabs.Count, $"{w.VisibleTabHeaders} of {w.AllTabs.Count}");
            await Shot($"10-tabs-{side.ToString().ToLowerInvariant()}");
        }

        // Folding the side list down to icons
        w.SetTabSide(TabSide.Left);
        await Task.Delay(500);
        w.ToggleCollapse();
        await Task.Delay(700);
        Check("the side list folds to icons", Math.Abs(w.SideWidth - 56) < 2, $"{w.SideWidth:0}px");
        await Shot("11-tabs-left-folded");
        w.ToggleCollapse();
        await Task.Delay(700);
        Check("and opens again", Math.Abs(w.SideWidth - 248) < 2, $"{w.SideWidth:0}px");
        w.SetTabSide(TabSide.Top);
        await Task.Delay(500);

        // Settings are saved
        var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(w.DataFolder, "settings.json")));
        Check("the tab position is saved", saved.RootElement.GetProperty("TabSide").GetString() == "Top");

        // Drawer
        var before = w.PagesSize.Width;
        w.ShowSettings();
        await Task.Delay(700);
        Check("settings open beside the page", Math.Abs(w.DrawerWidth - 400) < 2 && Math.Abs(before - w.PagesSize.Width - 400) < 3, $"drawer {w.DrawerWidth:0}px, page {before:0} -> {w.PagesSize.Width:0}");
        await Shot("12-settings");
        w.CloseDrawer();
        await Task.Delay(500);
        Check("and close again", w.DrawerWidth < 1 && Math.Abs(before - w.PagesSize.Width) < 3, $"drawer {w.DrawerWidth:0}px, page {before:0} -> {w.PagesSize.Width:0}");
    }
}
