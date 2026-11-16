using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vireo.Services;

namespace Vireo.Controls;

/// <summary>Profiles: this one (rename, colour), the others (open, shortcut, remove), and a new one.</summary>
public sealed class ProfilesPanel : Grid
{
    private readonly MainWindow _window;
    private readonly StackPanel _page = new() { Margin = new Thickness(20, 0, 20, 24) };
    private string? _confirmRemove;

    public ProfilesPanel(MainWindow window)
    {
        _window = window;
        Children.Add(new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Build();
    }

    private static Brush ColorBrush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    private static Border Badge(Profile p, double size) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = ColorBrush(p.Color),
        Child = new TextBlock { Text = p.Name[..1].ToUpperInvariant(), FontSize = size * 0.45, FontWeight = FontWeights.Bold, Foreground = UiKit.Brush("AccentInk"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    private void Build()
    {
        _page.Children.Clear();
        _page.Children.Add(UiKit.Muted("Each profile is a separate browser: its own sign-ins and cookies, history, bookmarks, settings and extensions. Nothing is shared between them, so work and personal never mix."));

        var all = Profiles.All();
        var me = all.First(p => p.Id == App.ProfileId) ;

        // This profile
        _page.Children.Add(UiKit.Heading("This window"));
        var name = new TextBox { Text = me.Name, MaxLength = 30, FontSize = 13.5, Padding = new Thickness(8, 6, 8, 6), Background = UiKit.Brush("Bg"), Foreground = UiKit.Brush("Text"), CaretBrush = UiKit.Brush("Accent"), BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center };
        name.LostKeyboardFocus += (_, _) => Rename(me, name.Text);
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Rename(me, name.Text); };
        var head = new DockPanel { Margin = new Thickness(14, 12, 14, 6) };
        var badge = Badge(me, 40);
        badge.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(badge, Dock.Left);
        head.Children.Add(badge);
        head.Children.Add(name);
        _page.Children.Add(UiKit.Card(
            new StackPanel { Children = { head, Swatches(me.Color, c => { Profiles.Update(me with { Color = c }); Refresh(); }) } },
            UiKit.Row("Desktop shortcut", "Opens this profile directly.", ShortcutButton(me))));

        // The others
        var others = all.Where(p => p.Id != App.ProfileId).ToList();
        if (others.Count > 0)
        {
            _page.Children.Add(UiKit.Heading("Other profiles"));
            _page.Children.Add(UiKit.Card([.. others.Select(OtherRow)]));
        }

        // A new one
        _page.Children.Add(UiKit.Heading("Add a profile"));
        var newName = new TextBox { MaxLength = 30, FontSize = 13.5, Padding = new Thickness(8, 6, 8, 6), Background = UiKit.Brush("Bg"), Foreground = UiKit.Brush("Text"), CaretBrush = UiKit.Brush("Accent"), BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1) };
        var color = Profiles.Colors[all.Count % Profiles.Colors.Length];
        var swatches = Swatches(color, c => color = c);
        var add = new Button { Style = (Style)Application.Current.FindResource("PrimaryButton"), Content = "Add and open", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        add.Click += (_, _) =>
        {
            var text = newName.Text.Trim();
            if (text.Length == 0) { newName.Focus(); return; }
            var p = Profiles.Add(text, color);
            Profiles.Launch(p.Id);
            Refresh();
        };
        newName.KeyDown += (_, e) => { if (e.Key == Key.Enter) add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); };
        var form = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
        form.Children.Add(new TextBlock { Text = "Name", FontSize = 12, Foreground = UiKit.Brush("Text2"), Margin = new Thickness(0, 0, 0, 6) });
        form.Children.Add(newName);
        swatches.Margin = new Thickness(0, 12, 0, 0);
        form.Children.Add(swatches);
        form.Children.Add(add);
        _page.Children.Add(UiKit.Card(form));
    }

    private UIElement OtherRow(Profile p)
    {
        var grid = new Grid { Margin = new Thickness(14, 10, 10, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = Badge(p, 30);
        badge.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(badge);
        var text = new TextBlock { Text = p.Name, FontSize = 13.5, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var open = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Open", Margin = new Thickness(0, 0, 4, 0) };
        open.Click += (_, _) => Profiles.Launch(p.Id);
        actions.Children.Add(open);
        actions.Children.Add(ShortcutButton(p, icon: true));
        if (p.Id.Length > 0)
        {
            // Removing deletes the profile's data: a second click confirms.
            var confirming = _confirmRemove == p.Id;
            var remove = new Button { Style = (Style)Application.Current.FindResource("GhostButton"), Content = confirming ? "Delete its data?" : "Remove", Foreground = UiKit.Brush(confirming ? "Danger" : "Text2") };
            remove.Click += (_, _) =>
            {
                if (_confirmRemove != p.Id) { _confirmRemove = p.Id; Build(); return; }
                Profiles.Remove(p.Id);
                _confirmRemove = null;
                Refresh();
            };
            actions.Children.Add(remove);
        }
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return grid;
    }

    private Button ShortcutButton(Profile p, bool icon = false)
    {
        var b = icon
            ? new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = ((char)0xE8A7).ToString(), FontSize = 13, ToolTip = "Desktop shortcut" }
            : new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Content = "Create" };
        b.Click += (_, _) =>
        {
            var made = Profiles.CreateShortcut(p);
            _window.ShowToast(made is null ? "Shortcut" : "Shortcut on the desktop", made is null ? "Windows would not make one here." : $"Vireo - {p.Name}", null, isError: made is null);
        };
        return b;
    }

    private static StackPanel Swatches(string current, Action<string> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 2, 14, 12) };
        var dots = new List<Border>();
        foreach (var c in Profiles.Colors)
        {
            var color = c;
            var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = ColorBrush(c), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, BorderThickness = new Thickness(2), BorderBrush = c == current ? UiKit.Brush("Text") : Brushes.Transparent, ToolTip = "Colour" };
            dot.MouseLeftButtonUp += (_, _) =>
            {
                foreach (var d in dots) d.BorderBrush = Brushes.Transparent;
                dot.BorderBrush = UiKit.Brush("Text");
                pick(color);
            };
            dots.Add(dot);
            row.Children.Add(dot);
        }
        return row;
    }

    private void Rename(Profile me, string text)
    {
        if (text.Trim().Length == 0 || text.Trim() == me.Name) return;
        Profiles.Update(me with { Name = text });
        _window.ShowProfile();
    }

    private void Refresh()
    {
        _window.ShowProfile();
        Build();
    }
}
