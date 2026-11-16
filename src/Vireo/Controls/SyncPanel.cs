using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vireo.Services;

namespace Vireo.Controls;

/// <summary>Setting up sync, how it is going, and the tabs open on the person's other computers.</summary>
public sealed class SyncPanel : Grid
{
    private readonly MainWindow _window;
    private readonly SyncService? _sync;
    private readonly StackPanel _page = new() { Margin = new Thickness(20, 0, 20, 24) };
    private string? _folder;
    private bool _confirmOff;

    public SyncPanel(MainWindow window, SyncService? sync)
    {
        _window = window;
        _sync = sync;
        Children.Add(new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        if (sync is not null)
        {
            void Changed() => Dispatcher.BeginInvoke(Build);
            sync.Changed += Changed;
            Unloaded += (_, _) => sync.Changed -= Changed;
        }
        Build();
    }

    private static TextBox Field(string text = "") => new()
    {
        Text = text, FontSize = 13.5, Padding = new Thickness(8, 7, 8, 7), Background = UiKit.Brush("Bg"), Foreground = UiKit.Brush("Text"),
        CaretBrush = UiKit.Brush("Accent"), BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1),
    };

    private static PasswordBox Secret() => new()
    {
        FontSize = 13.5, Padding = new Thickness(8, 7, 8, 7), Background = UiKit.Brush("Bg"), Foreground = UiKit.Brush("Text"),
        CaretBrush = UiKit.Brush("Accent"), BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1),
    };

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = UiKit.Brush("Text2"), Margin = new Thickness(0, 12, 0, 6) };

    private void Build()
    {
        _page.Children.Clear();
        if (_sync is null)
        {
            _page.Children.Add(UiKit.Empty(0xE895, "Not in a private window", "Private windows keep nothing, so there is nothing to sync."));
            return;
        }
        if (_sync.IsOn) BuildOn(_sync);
        else BuildOff(_sync);
    }

    private void BuildOff(SyncService sync)
    {
        _page.Children.Add(UiKit.Muted("Keep your bookmarks, history, start-page sites and open tabs the same on all your computers. Vireo has no server: choose a folder that OneDrive, Google Drive or Dropbox already keeps in step, and set a passphrase. Everything is locked with it before it is written there, so only your computers can read it. Passwords are never synced."));

        var form = new StackPanel { Margin = new Thickness(14, 2, 14, 14) };
        var folderText = new TextBlock { Text = _folder ?? "No folder chosen", FontSize = 12.5, Foreground = UiKit.Brush(_folder is null ? "Text3" : "Text"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var pick = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Choose folder…" };
        var folderRow = new DockPanel();
        DockPanel.SetDock(pick, Dock.Right);
        folderRow.Children.Add(pick);
        folderRow.Children.Add(folderText);
        form.Children.Add(Label("Cloud folder"));
        form.Children.Add(folderRow);

        var joining = _folder is not null && SyncVault.Exists(SyncVault.FolderIn(_folder));
        var note = UiKit.Muted(joining
            ? "This folder already holds Vireo sync from another computer: enter the passphrase you chose there."
            : "Choose a passphrase of at least 8 characters. You will type it once on each of your computers; if it is lost, sync starts again from a new folder.", 12);
        note.Margin = new Thickness(0, 10, 0, 0);
        form.Children.Add(note);
        var pass = Secret();
        var again = Secret();
        form.Children.Add(Label("Passphrase"));
        form.Children.Add(pass);
        if (!joining)
        {
            form.Children.Add(Label("Passphrase again"));
            form.Children.Add(again);
        }
        var name = Field(Environment.MachineName);
        form.Children.Add(Label("This computer's name"));
        form.Children.Add(name);
        var error = new TextBlock { FontSize = 12, Foreground = UiKit.Brush("Danger"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        form.Children.Add(error);
        var on = new Button { Style = (Style)Application.Current.FindResource("PrimaryButton"), Content = "Turn on sync", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        form.Children.Add(on);
        _page.Children.Add(UiKit.Heading("Set up"));
        _page.Children.Add(UiKit.Card(form));

        pick.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder your cloud service keeps in sync (OneDrive, Google Drive, Dropbox)" };
            var onedrive = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrEmpty(onedrive)) dialog.InitialDirectory = onedrive;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            _folder = dialog.FolderName;
            Build();
        };
        on.Click += async (_, _) =>
        {
            void Say(string text) { error.Text = text; error.Visibility = Visibility.Visible; }
            if (_folder is null) { Say("Choose the cloud folder first."); return; }
            if (!joining && pass.Password != again.Password) { Say("The two passphrases are not the same."); return; }
            on.IsEnabled = false;
            on.Content = "Turning on…";
            var problem = await sync.TurnOnAsync(_folder, pass.Password, name.Text);
            on.IsEnabled = true;
            on.Content = "Turn on sync";
            if (problem is not null && !sync.IsOn) { Say(problem); return; }
            _window.ShowToast("Sync is on", "This computer is now in step with your others", null);
            Build();
        };
    }

    private void BuildOn(SyncService sync)
    {
        var status = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        status.Children.Add(new TextBlock { Text = sync.LastSync is { } at ? $"Synced {Format.When(at)}" : "Syncing…", FontSize = 15, FontWeight = FontWeights.SemiBold });
        status.Children.Add(UiKit.Muted($"{sync.Devices.Count + 1} computer{(sync.Devices.Count == 0 ? "" : "s")} · every two minutes while Vireo is open", 12));
        if (sync.LastError is { } err)
        {
            status.Children.Add(new TextBlock { Text = err, FontSize = 12, Foreground = UiKit.Brush("Warn"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var now = new Button { Style = (Style)Application.Current.FindResource("PrimaryButton"), Content = "Sync now", Margin = new Thickness(0, 0, 8, 0) };
        now.Click += async (_, _) => { now.IsEnabled = false; now.Content = "Syncing…"; await sync.SyncNowAsync(); };
        var off = new Button { Style = (Style)Application.Current.FindResource("GhostButton"), Content = _confirmOff ? "Turn off, and take this computer out of the folder?" : "Turn off", Foreground = UiKit.Brush(_confirmOff ? "Danger" : "Text2") };
        off.Click += (_, _) =>
        {
            if (!_confirmOff) { _confirmOff = true; Build(); return; }
            sync.TurnOff(forget: true);
            _confirmOff = false;
            Build();
        };
        buttons.Children.Add(now);
        buttons.Children.Add(off);
        status.Children.Add(buttons);
        _page.Children.Add(UiKit.Heading("Sync"));
        _page.Children.Add(UiKit.Card(status,
            UiKit.Row("Folder", sync.Folder, UiKit.Muted("")),
            UiKit.Row("What is synced", "Bookmarks, history, start-page sites and the tabs you have open. Passwords never.", UiKit.Muted(""))));

        _page.Children.Add(UiKit.Heading("Tabs on your other computers"));
        if (sync.Devices.Count == 0)
        {
            _page.Children.Add(UiKit.Muted("Nothing yet. Turn on sync on your other computer with the same folder and passphrase; its tabs appear here.", 12));
            return;
        }
        foreach (var device in sync.Devices)
        {
            var rows = new List<UIElement>
            {
                new TextBlock { Text = $"{device.Name} · {Format.When(device.UpdatedUtc.ToLocalTime())}", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("Text2"), Margin = new Thickness(14, 10, 14, 8) },
            };
            foreach (var t in device.Tabs.Take(30)) rows.Add(TabRow(t));
            if (device.Tabs.Count == 0) rows.Add(new TextBlock { Text = "No tabs open", FontSize = 12, Foreground = UiKit.Brush("Text3"), Margin = new Thickness(14, 8, 14, 10) });
            var card = UiKit.Card([.. rows]);
            card.Margin = new Thickness(0, 0, 0, 10);
            _page.Children.Add(card);
        }
    }

    private UIElement TabRow(SyncTab t)
    {
        var host = Uri.TryCreate(t.Url, UriKind.Absolute, out var u) ? u.Host : t.Url;
        var text = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        text.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(t.Title) ? host : t.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = host, FontSize = 11.5, Foreground = UiKit.Brush("Text3"), TextTrimming = TextTrimming.CharacterEllipsis });
        var row = new Border { Padding = new Thickness(14, 7, 14, 7), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Child = text, ToolTip = t.Url };
        row.MouseEnter += (_, _) => row.Background = UiKit.Brush("Hover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => { if (t.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _window.OpenLink(t.Url); };
        return row;
    }
}
