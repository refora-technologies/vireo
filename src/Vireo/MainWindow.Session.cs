using System.IO;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Vireo.Models;
using Vireo.Services;

namespace Vireo;

// Starting where you left off, and recovering when a page or the whole browser engine crashes.
public partial class MainWindow
{
    private DispatcherTimer? _sessionTimer;
    private string _lastSession = "";
    private bool _noSessionSave;

    private string SessionPath => Path.Combine(_dataFolder, "session.json");

    /// <summary>The tabs as they are now (also used by the self-test).</summary>
    internal SessionState CurrentSession() =>
        SessionStore.Snapshot(_tabs.Select(t => (t.Url, t.Title, t.IsPinned)), Active is null ? -1 : _tabs.IndexOf(Active));

    private void StartSession()
    {
        if (_private) return;
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _sessionTimer.Tick += (_, _) => SaveSession();
        _sessionTimer.Start();
        Closed += (_, _) => _sessionTimer?.Stop();
    }

    /// <summary>Writes the open tabs to disk, but only when they changed.</summary>
    internal void SaveSession()
    {
        using var _crumb = Breadcrumbs.Scope("saving the session");
        if (_private || SelfTesting || _noSessionSave || !_settings.RestoreTabs || !IsFirstWindow) return;
        var others = s_windows.Skip(1).Select(w => w.CurrentSession()).Where(s => s.Tabs.Count > 0).Select(s => new SessionWindow(s.Tabs, s.Active)).ToList();
        var state = CurrentSession() with { More = others.Count > 0 ? others : null };
        var json = SessionStore.ToJson(state);
        if (json == _lastSession) return;
        _lastSession = json;
        SessionStore.Save(SessionPath, state);
    }

    /// <summary>Brings back the tabs of the last visit: the one you were on loads now, the rest wait until you click them (no memory used).</summary>
    internal async Task<bool> RestoreSessionAsync(SessionState? state = null)
    {
        state ??= SessionStore.Load(SessionPath);
        if (state is null || state.Tabs.Count == 0) return false;
        // The other windows of the last visit come back as windows of their own.
        foreach (var more in state.More ?? [])
        {
            var tabs = more;
            OpenWindow(w => w.RestoreSessionAsync(new SessionState(tabs.Tabs, tabs.Active)));
        }
        for (var i = 0; i < state.Tabs.Count; i++)
        {
            var t = state.Tabs[i];
            var current = i == state.Active;
            await OpenTabAsync(t.Url, activate: current, lazy: !current, title: t.Title, pinned: t.Pinned);
        }
        return true;
    }

    // ---- Links from other programs ----

    /// <summary>Another start of Vireo (or Windows, for a clicked link) asked this window to open something.</summary>
    internal async void OpenFromOutside(string link)
    {
        await _ready.Task;
        if (base.WindowState == System.Windows.WindowState.Minimized) base.WindowState = System.Windows.WindowState.Normal;
        // Windows will not let a program take the foreground by itself; a brief "topmost" moves it in front.
        var top = Topmost;
        Topmost = true;
        Activate();
        Topmost = top;
        if (string.IsNullOrWhiteSpace(link)) return;
        var target = UrlInput.Resolve(link.Trim('"'), _settings.SearchTemplate);
        if (target is null) return;
        // A page that is just the start page is used rather than leaving it behind an extra tab.
        if (Active is { IsStartPage: true, } blank && blank.View is not null) Navigate(blank, target);
        else await OpenTabAsync(target);
    }

    // ---- Crashes ----

    private int _renderRecoveries;
    internal int RendererRecoveries => _renderRecoveries;
    internal int BrowserRecoveries { get; private set; }
    private bool _recovering;

    private void OnProcessFailed(BrowserTab tab, CoreWebView2 core, CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                _ = Dispatcher.InvokeAsync(RecoverBrowserAsync);
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                _ = Dispatcher.InvokeAsync(() => RecoverPage(tab, core));
                break;
            // Frames of a page, helper processes and a page that is merely slow are left alone: the engine restarts what it can.
        }
    }

    /// <summary>The page's process died: load it again once. A page that crashes again right away is left for the person to decide.</summary>
    private void RecoverPage(BrowserTab tab, CoreWebView2 core)
    {
        if (tab.View?.CoreWebView2 != core) return;
        var again = DateTime.UtcNow - tab.LastCrash < TimeSpan.FromSeconds(20);
        tab.LastCrash = DateTime.UtcNow;
        if (again)
        {
            ShowToast("This page keeps crashing", "Reload it to try again", () => { try { core.Reload(); } catch { /* gone */ } }, isError: true);
            return;
        }
        _renderRecoveries++;
        try { core.Reload(); } catch { /* the tab was closed meanwhile */ }
    }

    /// <summary>
    /// The whole engine stopped (killed, updated, crashed). Start a new one; the tab being looked at reloads, the others
    /// become sleeping tabs that load when clicked. Without this every tab would stay dead until Vireo was restarted.
    /// </summary>
    private async Task RecoverBrowserAsync()
    {
        if (_recovering) return;
        _recovering = true;
        try
        {
            var active = Active;
            foreach (var t in _tabs.ToList())
            {
                DisposeView(t);
                t.IsLoading = false;
                t.IsPlayingAudio = false;
                t.Life = TabLife.Discarded;
            }
            Active = null;
            // Every window lost the same engine: the first to notice starts the new one, the others join it (same options, or it refuses).
            var dead = _env;
            if (s_env is null || await s_env == dead) s_env = null;
            _env = await EnvironmentFor(_dataFolder, _lowEnd);
            if (active is not null && _tabs.Contains(active)) await ActivateAsync(active);
            BrowserRecoveries++;
            ShowToast("Vireo recovered", "The browser engine restarted", null);
        }
        catch { /* nothing more to try; the person can restart */ }
        finally { _recovering = false; }
    }
}
