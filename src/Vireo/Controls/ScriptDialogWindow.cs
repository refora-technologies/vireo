using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Web.WebView2.Core;

namespace Vireo.Controls;

/// <summary>The message box a page gets for alert(), confirm(), prompt() and "leave this page?", drawn by Vireo so it looks like part of it.</summary>
public sealed class ScriptDialogWindow : Window
{
    private readonly TextBox? _input;
    public bool Accepted { get; private set; }
    public string Text => _input?.Text ?? "";

    public ScriptDialogWindow(Window owner, string origin, CoreWebView2ScriptDialogKind kind, string message, string defaultText)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var leave = kind == CoreWebView2ScriptDialogKind.Beforeunload;
        var stack = new StackPanel { Margin = new Thickness(22, 20, 22, 18), MinWidth = 340, MaxWidth = 460 };
        stack.Children.Add(new TextBlock { Text = leave ? "Leave this page?" : origin + " says", FontSize = 12, Foreground = UiKit.Brush("Text3"), FontFamily = (FontFamily)Application.Current.FindResource("UiFont") });
        stack.Children.Add(new TextBlock
        {
            Text = leave ? "Changes you made may not be saved." : message, TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 8, 0, 0),
            Foreground = UiKit.Brush("Text"), FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), MaxHeight = 220,
        });

        if (kind == CoreWebView2ScriptDialogKind.Prompt)
        {
            _input = new TextBox
            {
                Text = defaultText, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(8, 7, 8, 7), FontSize = 13.5,
                Background = UiKit.Brush("Bg"), Foreground = UiKit.Brush("Text"), CaretBrush = UiKit.Brush("Accent"),
                BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            };
            stack.Children.Add(_input);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        Button Make(string text, string style, Action click)
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.FindResource(style), MinWidth = 80, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0), FontSize = 12.5 };
            b.Click += (_, _) => click();
            return b;
        }
        if (kind != CoreWebView2ScriptDialogKind.Alert) buttons.Children.Add(Make(leave ? "Stay" : "Cancel", "SecondaryButton", () => Finish(false)));
        buttons.Children.Add(Make(leave ? "Leave" : "OK", "PrimaryButton", () => Finish(true)));
        stack.Children.Add(buttons);

        Content = new Border
        {
            Margin = new Thickness(24), CornerRadius = new CornerRadius(10), Background = UiKit.Brush("Surface2"),
            BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black },
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish(false);
            else if (e.Key == Key.Enter && _input is null) Finish(true);
        };
        Loaded += (_, _) => { if (_input is not null) { _input.Focus(); _input.SelectAll(); } else Focus(); };
        _input?.KeyDown += (_, e) => { if (e.Key == Key.Enter) Finish(true); };
    }

    private void Finish(bool accepted)
    {
        Accepted = accepted;
        Close();
    }
}
