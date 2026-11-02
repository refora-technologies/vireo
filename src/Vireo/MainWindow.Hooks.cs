using Microsoft.Web.WebView2.Core;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

// Small helpers the self-test uses to drive and inspect pages.
public partial class MainWindow
{
    internal async Task<string> EvalAsync(BrowserTab tab, string script) =>
        tab.View?.CoreWebView2 is { } core ? await core.ExecuteScriptAsync(script) : "null";

    internal async Task ReloadAsync(BrowserTab tab)
    {
        if (tab.View?.CoreWebView2 is not { } core) return;
        var done = new TaskCompletionSource();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= Handler;
            done.TrySetResult();
        }
        core.NavigationCompleted += Handler;
        core.Reload();
        await Task.WhenAny(done.Task, Task.Delay(10000));
    }

    internal CoreWebView2TrackingPreventionLevel TrackingLevelNow =>
        _tabs.Select(t => t.View?.CoreWebView2?.Profile).FirstOrDefault(p => p is not null)?.PreferredTrackingPreventionLevel ?? CoreWebView2TrackingPreventionLevel.None;

    /// <summary>Presses a choice in the open settings panel by its label.</summary>
    internal bool PickInPanel(string label)
    {
        System.Windows.Controls.RadioButton? Find(System.Windows.DependencyObject parent)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Controls.RadioButton r && Equals(r.Content, label)) return r;
                if (Find(child) is { } found) return found;
            }
            return null;
        }
        var radio = Find(DrawerBody);
        if (radio is null) return false;
        radio.IsChecked = true;
        return true;
    }

    internal System.Windows.DependencyObject DrawerBodyForTest => DrawerBody;

    internal Controls.BatchGrabberPanel? GrabberPanel => DrawerBody.Content as Controls.BatchGrabberPanel;

    internal bool HttpAllowedFor(string host) => _httpAllowed.Contains(host);

    internal MainWindow OpenPrivateWindowForTest()
    {
        var win = new MainWindow(true) { Left = Left + 40, Top = Top + 40, Width = Width, Height = Height, Topmost = true };
        win.Show();
        return win;
    }

    internal bool BookmarkFilled => (string?)BookmarkButton.Content == Glyph.StarFilled;
    internal int SuggestionCount => _suggestions.Count;
    internal bool SuggestionsOpen => _suggest is { IsOpen: true };
    internal List<Suggestion> Suggestions => [.. _suggestions];
    internal string FirstSuggestionUrl => _suggestions.FirstOrDefault()?.Url ?? "";
    internal bool FirstSuggestionIsBookmark => _suggestions.FirstOrDefault()?.IsBookmark ?? false;

    internal void TypeInAddress(string text)
    {
        Address.Focus();
        _typed = true;
        Address.Text = text;
    }

    internal void ClearAddressFocus() => Active?.View?.Focus();

    internal string MediaBadge => MediaCount.Text;

    internal async Task ShowMediaAndWait()
    {
        ShowMedia();
        await Task.Delay(900);
    }
}
