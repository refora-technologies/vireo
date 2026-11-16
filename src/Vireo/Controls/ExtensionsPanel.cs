using System.Windows;
using System.Windows.Controls;
using Vireo.Services;

namespace Vireo.Controls;

/// <summary>The list of installed extensions, with the ways to add one.</summary>
public sealed class ExtensionsPanel : Grid
{
    private readonly MainWindow _window;
    private readonly StackPanel _list = new() { Margin = new Thickness(10, 4, 10, 20) };
    private readonly TextBlock _message = new() { FontSize = 12, Foreground = UiKit.Brush("Danger"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 20, 8), Visibility = Visibility.Collapsed };

    public ExtensionsPanel(MainWindow window)
    {
        _window = window;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var head = new StackPanel { Margin = new Thickness(20, 0, 20, 10) };
        head.Children.Add(new TextBlock { Text = "Add an extension from a folder or a downloaded .zip or .crx file. Store pages cannot install into Vireo directly: download the extension's file first.", TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 17, Foreground = UiKit.Brush("Text3") });
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button Make(string text, bool primary, Func<Task<string>> install)
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.FindResource(primary ? "PrimaryButton" : "SecondaryButton"), Margin = new Thickness(0, 0, 8, 6) };
            b.Click += async (_, _) => { Say(await install()); await Fill(); };
            return b;
        }
        buttons.Children.Add(Make("Load unpacked…", true, _window.InstallExtensionFolderAsync));
        buttons.Children.Add(Make("Install from a file…", false, _window.InstallExtensionFileAsync));
        head.Children.Add(buttons);
        SetRow(head, 0);
        Children.Add(head);

        SetRow(_message, 1);
        Children.Add(_message);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        SetRow(scroll, 2);
        Children.Add(scroll);
        Loaded += async (_, _) => await Fill();
    }

    private void Say(string text)
    {
        _message.Text = text;
        _message.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task Fill()
    {
        await _window.RefreshExtensionsAsync();
        _list.Children.Clear();
        if (!_window.ExtensionsAvailable)
        {
            _list.Children.Add(UiKit.Empty(0xEA86, "Not in a private window", "Extensions only run in normal windows."));
            return;
        }
        if (_window.InstalledExtensions.Count == 0)
        {
            _list.Children.Add(UiKit.Empty(0xEA86, "No extensions yet", "Load one to see its button next to the address bar."));
            return;
        }
        var first = true;
        foreach (var ext in _window.InstalledExtensions)
        {
            if (!first) _list.Children.Add(UiKit.Rule());
            first = false;
            _list.Children.Add(Card(ext));
        }
    }

    private UIElement Card(ExtensionInfo ext)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = MainWindow.ExtensionIcon(ext, 36);
        ((FrameworkElement)icon).Margin = new Thickness(0, 2, 12, 0);
        ((FrameworkElement)icon).VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = ext.Manifest.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (ext.Manifest.Version.Length > 0) text.Children.Add(new TextBlock { Text = ext.Manifest.Version, FontSize = 11, FontFamily = UiKit.Mono, Foreground = UiKit.Brush("Text3"), Margin = new Thickness(0, 1, 0, 0) });
        if (ext.Manifest.Description.Length > 0) text.Children.Add(new TextBlock { Text = ext.Manifest.Description, FontSize = 12, LineHeight = 17, Foreground = UiKit.Brush("Text2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), MaxHeight = 52 });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-9, 6, 0, 0) };
        if (ext.OptionsUrl is { } options)
        {
            var open = new Button { Content = "Options", Style = (Style)Application.Current.FindResource("GhostButton"), Margin = new Thickness(0, 0, 2, 0) };
            open.Click += (_, _) => _ = _window.OpenTabAsync(options);
            actions.Children.Add(open);
        }
        var remove = new Button { Content = "Remove", Style = (Style)Application.Current.FindResource("GhostButton") };
        remove.Click += async (_, _) => { await _window.Extensions.RemoveAsync(_window.CurrentProfile!, ext.Id); await Fill(); };
        actions.Children.Add(remove);
        text.Children.Add(actions);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var toggle = UiKit.Switch(ext.Enabled, async on =>
        {
            await _window.Extensions.SetEnabledAsync(_window.CurrentProfile!, ext.Id, on);
            await _window.RefreshExtensionsAsync();
        });
        toggle.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        return new Border { Padding = new Thickness(10, 14, 10, 12), Child = grid };
    }
}
