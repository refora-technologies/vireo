using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vireo.Downloads;

namespace Vireo.Controls;

/// <summary>
/// The small window the tray icon opens while Vireo downloads in the background: every download with its progress, pause,
/// resume and cancel (the same panel as in the browser), and the way back to the browser. Closes when it loses focus, the way
/// the notification area's own pop-ups do, so it never stays over another program.
/// </summary>
public sealed class TrayDownloadsWindow : Window
{
    public event Action? OpenBrowserRequested;
    public event Action? QuitRequested;
    private readonly TextBlock _note = new() { FontSize = 11.5, Foreground = UiKit.Brush("Text3"), TextWrapping = TextWrapping.Wrap };
    private readonly Button _open = new() { Content = "Open Vireo", Padding = new Thickness(16, 7, 16, 7) };

    /// <summary>Says whether the browser is closed (only the downloader runs) or open.</summary>
    public void Describe(bool browserClosed)
    {
        _note.Text = browserClosed ? "The browser is closed. Downloads carry on here, using very little." : "Your downloads, from the notification area.";
        _open.Content = browserClosed ? "Open Vireo" : "Go to Vireo";
    }

    public TrayDownloadsWindow(DownloadEngine engine)
    {
        Title = "Vireo downloads";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true; // only while it is open; it closes the moment another window is used
        Width = 400;
        Height = 560;
        Background = UiKit.Brush("Surface1");
        BorderBrush = UiKit.Brush("Line2");
        BorderThickness = new Thickness(1);
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont");
        Foreground = UiKit.Brush("Text");
        UseLayoutRounding = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new DockPanel { Margin = new Thickness(18, 16, 12, 6) };
        var mark = new Image { Source = (System.Windows.Media.ImageSource)Application.Current.FindResource("AppMark"), Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) };
        head.Children.Add(mark);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Vireo Downloads", FontSize = 15, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(_note);
        head.Children.Add(titles);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        var panel = new DownloadsPanel(engine);
        Grid.SetRow(panel, 1);
        root.Children.Add(panel);

        var foot = new Grid { Margin = new Thickness(16, 8, 16, 14) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var quit = new Button { Style = (Style)Application.Current.FindResource("GhostButton"), Content = "Quit", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 10, 6), ToolTip = "Stop downloading and close Vireo (unfinished downloads carry on next time)" };
        quit.Click += (_, _) => QuitRequested?.Invoke();
        foot.Children.Add(quit);
        var open = _open;
        open.Style = (Style)Application.Current.FindResource("PrimaryButton");
        open.Click += (_, _) => OpenBrowserRequested?.Invoke();
        Grid.SetColumn(open, 1);
        foot.Children.Add(open);
        Grid.SetRow(foot, 2);
        root.Children.Add(foot);
        Content = root;
        Describe(true);

        Deactivated += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        SourceInitialized += (_, _) => Native.StyleWindow(this); // dark frame and rounded corners, like the browser
    }

    /// <summary>For tests: shown off screen, without taking the focus from whatever the person is using.</summary>
    internal void ShowOffScreenForTest()
    {
        Left = -4000;
        Top = 0;
        ShowActivated = false;
        Show();
    }

    /// <summary>Opens just above the notification area, at the bottom right of the screen (where the tray icon is).</summary>
    public void ShowNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
        Show();
        Activate();
    }
}
