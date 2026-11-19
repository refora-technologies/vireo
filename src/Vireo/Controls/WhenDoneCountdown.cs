using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Vireo.Services;

namespace Vireo.Controls;

/// <summary>
/// "Downloads finished. Shutting down in 60 s": the last word before the computer sleeps or shuts down, with Cancel in front.
/// It stays above other windows on purpose (like Windows' own shutdown warning): whoever is at the computer must see it
/// and be able to stop it. It gives way by itself if a download starts again.
/// </summary>
public sealed class WhenDoneCountdown : Window
{
    private readonly TaskCompletionSource<bool> _answer = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _title;
    private readonly AfterDownloads _what;
    private int _left;

    /// <summary>Shows the warning and counts down; true to go ahead, false when cancelled.</summary>
    public Task<bool> Run()
    {
        Show();
        Update();
        _timer.Start();
        return _answer.Task;
    }

    /// <summary>For tests: answer without a person.</summary>
    internal void Press(bool go) => Finish(go);

    public WhenDoneCountdown(AfterDownloads what, TimeSpan warning)
    {
        _what = what;
        _left = (int)Math.Max(1, warning.TotalSeconds);
        Title = "Vireo";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = true; // findable from the taskbar too
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        var font = (FontFamily)Application.Current.FindResource("UiFont");

        var stack = new StackPanel { Margin = new Thickness(24, 22, 24, 20), Width = 380 };
        stack.Children.Add(new TextBlock { Text = "Downloads finished", FontSize = 12, Foreground = UiKit.Brush("Text3"), FontFamily = font });
        _title = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), Foreground = UiKit.Brush("Text"), FontFamily = font };
        stack.Children.Add(_title);
        stack.Children.Add(new TextBlock
        {
            Text = what == AfterDownloads.ShutDown
                ? "As you asked, the computer shuts down now that everything is downloaded. Programs close normally and save their work."
                : "As you asked, the computer goes to sleep now that everything is downloaded.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 18, Margin = new Thickness(0, 8, 0, 0), Foreground = UiKit.Brush("Text2"), FontFamily = font,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var now = new Button { Content = what == AfterDownloads.ShutDown ? "Shut down now" : "Sleep now", Style = (Style)Application.Current.FindResource("SecondaryButton"), Padding = new Thickness(14, 7, 14, 7), FontSize = 12.5, Margin = new Thickness(0, 0, 8, 0) };
        now.Click += (_, _) => Finish(true);
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("PrimaryButton"), Padding = new Thickness(20, 7, 20, 7), FontSize = 12.5, IsDefault = true, IsCancel = true };
        cancel.Click += (_, _) => Finish(false);
        row.Children.Add(now);
        row.Children.Add(cancel);
        stack.Children.Add(row);

        Content = new Border
        {
            Margin = new Thickness(24), CornerRadius = new CornerRadius(12), Background = UiKit.Brush("Surface2"),
            BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black },
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(false); };
        Closed += (_, _) => { _timer.Stop(); _answer.TrySetResult(false); }; // closed any other way: nothing happens
        Loaded += (_, _) => cancel.Focus();
        _timer.Tick += (_, _) => Tick();
    }

    private void Tick()
    {
        if (WhenDone.SomethingRunning) { Finish(false); return; } // a new download: not finished after all
        _left--;
        if (_left <= 0) { Finish(true); return; }
        Update();
    }

    private void Update() => _title.Text = _what == AfterDownloads.ShutDown ? $"Shutting down in {_left} s" : $"Going to sleep in {_left} s";

    private void Finish(bool go)
    {
        if (_answer.Task.IsCompleted) return;
        _timer.Stop();
        _answer.TrySetResult(go);
        Close();
    }
}
