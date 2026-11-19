using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Vireo.Controls;

/// <summary>What to do with downloads still running when the last window closes.</summary>
public enum CloseChoice { Background, Stop, Cancel }

/// <summary>
/// Asked when Vireo is closed while downloads are running: keep downloading in the background (the browser closes, only the
/// downloader stays, in the notification area), stop them and close, or stay open.
/// </summary>
public sealed class CloseDownloadsDialog : Window
{
    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;
    /// <summary>"Don't ask again" was ticked: the choice should become the setting.</summary>
    public bool Remember { get; private set; }

    public CloseDownloadsDialog(Window? owner, int running)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        var font = (FontFamily)Application.Current.FindResource("UiFont");

        var stack = new StackPanel { Margin = new Thickness(24, 22, 24, 20), Width = 420 };
        stack.Children.Add(new TextBlock { Text = "Downloads", FontSize = 12, Foreground = UiKit.Brush("Text3"), FontFamily = font });
        stack.Children.Add(new TextBlock
        {
            Text = running == 1 ? "A download is still running" : $"{running} downloads are still running",
            FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), Foreground = UiKit.Brush("Text"), FontFamily = font,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Vireo can close the browser and keep downloading in the background, using very little memory. Its progress stays in the notification area, by the clock.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 18, Margin = new Thickness(0, 8, 0, 4), Foreground = UiKit.Brush("Text2"), FontFamily = font,
        });

        Button Option(string title, string detail, bool main, CloseChoice choice)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("Text"), FontFamily = font });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = UiKit.Brush("Text3"), FontFamily = font, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            var b = new Button
            {
                Style = (Style)Application.Current.FindResource("GhostButton"), Content = text, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand,
                Background = UiKit.Brush(main ? "AccentSoft" : "Bg"), BorderBrush = UiKit.Brush(main ? "Accent" : "Line2"), BorderThickness = new Thickness(1),
            };
            b.Click += (_, _) => Finish(choice);
            return b;
        }
        var keep = Option("Keep downloading in the background", "The browser closes; only the downloader keeps running until they are done.", true, CloseChoice.Background);
        keep.Name = "KeepOption";
        stack.Children.Add(keep);
        stack.Children.Add(Option("Stop downloads and close", "They carry on from where they stopped the next time you open Vireo.", false, CloseChoice.Stop));

        var remember = new CheckBox { Content = "Don't ask again (Settings can change this)", Margin = new Thickness(2, 16, 0, 0), Foreground = UiKit.Brush("Text2"), FontFamily = font, FontSize = 12 };
        remember.Checked += (_, _) => Remember = true;
        remember.Unchecked += (_, _) => Remember = false;
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("SecondaryButton"), Padding = new Thickness(16, 7, 16, 7), FontSize = 12.5, Margin = new Thickness(0, 12, 0, 0) };
        cancel.Click += (_, _) => Finish(CloseChoice.Cancel);
        DockPanel.SetDock(cancel, Dock.Right);
        row.Children.Add(cancel);
        row.Children.Add(remember);
        stack.Children.Add(row);

        Content = new Border
        {
            Margin = new Thickness(24), CornerRadius = new CornerRadius(12), Background = UiKit.Brush("Surface2"),
            BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black },
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(CloseChoice.Cancel); };
        Loaded += (_, _) => keep.Focus();
    }

    private void Finish(CloseChoice choice)
    {
        Choice = choice;
        if (choice == CloseChoice.Cancel) Remember = false;
        Close();
    }
}
