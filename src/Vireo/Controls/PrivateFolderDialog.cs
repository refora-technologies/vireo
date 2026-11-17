using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Vireo.Controls;

/// <summary>
/// Asked once, at the first download in a private window: where private downloads go from now on. The answer is
/// <see cref="SameAsNormal"/>, a folder, or null when the box was closed without choosing.
/// </summary>
public sealed class PrivateFolderDialog : Window
{
    public const string SameAsNormal = "same";
    public string? Choice { get; private set; }

    public PrivateFolderDialog(Window owner, string normalFolder, string? current = null)
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
        stack.Children.Add(new TextBlock { Text = "Private downloads", FontSize = 12, Foreground = UiKit.Brush("Text3"), FontFamily = font });
        stack.Children.Add(new TextBlock
        {
            Text = "Where should private downloads go?", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0),
            Foreground = UiKit.Brush("Text"), FontFamily = font,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Asked once. Files from every private window will be saved there; Vireo keeps no record of what was downloaded. You can change it in Settings.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 18, Margin = new Thickness(0, 8, 0, 0), Foreground = UiKit.Brush("Text2"), FontFamily = font,
        });

        Button Option(string title, string detail, bool chosen, Action click)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("Text"), FontFamily = font });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = UiKit.Brush("Text3"), FontFamily = UiKit.Mono, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
            var b = new Button
            {
                Style = (Style)Application.Current.FindResource("GhostButton"), Content = text, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand,
                Background = UiKit.Brush(chosen ? "AccentSoft" : "Bg"), BorderBrush = UiKit.Brush(chosen ? "Accent" : "Line2"), BorderThickness = new Thickness(1),
            };
            b.Click += (_, _) => click();
            return b;
        }
        var spacer = new Border { Height = 6 };
        stack.Children.Add(spacer);
        var same = Option("Same as normal downloads", normalFolder, current == SameAsNormal, () => Finish(SameAsNormal));
        same.Name = "SameOption";
        stack.Children.Add(same);
        var pickedOther = current is { Length: > 0 } c && c != SameAsNormal;
        stack.Children.Add(Option("Choose another folder…", pickedOther ? current! : "A USB stick, an encrypted drive, a folder only you use", pickedOther, () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Where should private downloads go?", InitialDirectory = pickedOther ? current! : normalFolder };
            if (dialog.ShowDialog(this) == true) Finish(dialog.FolderName);
        }));

        var cancel = new Button { Content = "Not now", Style = (Style)Application.Current.FindResource("SecondaryButton"), Padding = new Thickness(16, 7, 16, 7), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), ToolTip = "This download goes to your normal folder; you are asked again next time." };
        cancel.Click += (_, _) => Finish(null);
        stack.Children.Add(cancel);

        Content = new Border
        {
            Margin = new Thickness(24), CornerRadius = new CornerRadius(12), Background = UiKit.Brush("Surface2"),
            BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black },
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        Loaded += (_, _) => same.Focus();
    }

    private void Finish(string? choice)
    {
        Choice = choice;
        Close();
    }
}
