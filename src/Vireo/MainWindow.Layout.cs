using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vireo.Services;

namespace Vireo;

// Where the tabs live (top, bottom, left, right) and the drawer that docks beside the page.
public partial class MainWindow
{
    private Settings _settings = new();
    private bool _drawerOpen;

    public Settings Settings => _settings;

    private bool Compact => _settings.VerticalCollapsed && _settings.TabSide is TabSide.Left or TabSide.Right;

    private static void Detach(FrameworkElement e)
    {
        switch (e.Parent)
        {
            case Panel p: p.Children.Remove(e); break;
            case Decorator d: d.Child = null; break;
            case ContentControl c: c.Content = null; break;
        }
    }

    public void SetTabSide(TabSide side)
    {
        if (_settings.TabSide == side) return;
        _settings.TabSide = side;
        _settings.Commit();
        ApplyTabSide(side, animate: true);
    }

    private void ApplyTabSide(TabSide side, bool animate)
    {
        var vertical = side is TabSide.Left or TabSide.Right;
        var merged = side != TabSide.Top; // the toolbar moves up into the title row
        Detach(Tabs);
        Detach(Toolbar);
        Detach(SideTabs);
        TopHost.Child = ToolbarHost.Child = LeftHost.Child = RightHost.Child = BottomHost.Child = null;
        SideScroll.Content = null;

        Tabs.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        Tabs.Visibility = Toolbar.Visibility = SideTabs.Visibility = Visibility.Visible;
        TopRow.Height = new GridLength(merged ? 52 : 44);
        ToolbarRow.Height = new GridLength(merged ? 0 : 50);
        BottomRow.Height = new GridLength(side == TabSide.Bottom ? 44 : 0);
        // A private window's frame glows violet instead of green, so it is never mistaken for a normal one.
        if (_private) Background = (Brush)FindResource("PrivateFrameBrush");
        Brand.Visibility = merged ? Visibility.Visible : Visibility.Collapsed;
        TabRowMark.Visibility = side == TabSide.Top ? Visibility.Visible : Visibility.Collapsed;
        Body.Margin = side == TabSide.Bottom ? new Thickness(8, 0, 8, 0) : new Thickness(8, 0, 8, 8);

        switch (side)
        {
            case TabSide.Top:
                Tabs.Margin = new Thickness(0);
                TopHost.Child = Tabs;
                ToolbarHost.Child = Toolbar;
                break;
            case TabSide.Bottom:
                Tabs.Margin = new Thickness(8, 0, 8, 0);
                TopHost.Child = Toolbar;
                BottomHost.Child = Tabs;
                BottomHost.Background = Brushes.Transparent;
                break;
            default:
                Tabs.Margin = new Thickness(0);
                TopHost.Child = Toolbar;
                SideScroll.Content = Tabs;
                SideTabs.BorderThickness = new Thickness(0);
                SideTabs.Margin = side == TabSide.Left ? new Thickness(0, 0, 8, 0) : new Thickness(8, 0, 0, 0);
                (side == TabSide.Left ? LeftHost : RightHost).Child = SideTabs;
                break;
        }

        StyleNewTabButton();
        foreach (var h in _headers.Values) h.ApplyLayout(side, Compact);
        SideTabs.BeginAnimation(WidthProperty, null);
        SideTabs.Width = Compact ? 56 : 248;
        SideTitle.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.HorizontalAlignment = Compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        foreach (var r in SideChooser.Children.OfType<RadioButton>()) r.IsChecked = (TabSide)r.Tag == side;

        if (!animate) return;
        foreach (var moved in new UIElement[] { Tabs, Toolbar })
        {
            moved.Opacity = 0;
            moved.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        if (vertical)
        {
            var from = side == TabSide.Left ? -24.0 : 24.0;
            var slide = new TranslateTransform(from, 0);
            SideTabs.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    /// <summary>The new-tab button is a round "+" in a row of tabs, and a wide "+ New tab" row in a side list.</summary>
    private void StyleNewTabButton()
    {
        var vertical = _settings.TabSide is TabSide.Left or TabSide.Right;
        if (!vertical)
        {
            _newTabButton.Content = Glyph.Add;
            _newTabButton.Width = _newTabButton.Height = 30;
            _newTabButton.Margin = new Thickness(4, 0, 0, 0);
            _newTabButton.VerticalAlignment = VerticalAlignment.Center;
            _newTabButton.HorizontalAlignment = HorizontalAlignment.Left;
            return;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(new TextBlock { Text = Glyph.Add, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Text2") });
        if (!Compact) row.Children.Add(new TextBlock { Text = "New tab", FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Text2") });
        _newTabButton.Content = row;
        _newTabButton.Width = double.NaN;
        _newTabButton.Height = 34;
        _newTabButton.Margin = new Thickness(6, 4, 6, 6);
        _newTabButton.VerticalAlignment = VerticalAlignment.Top;
        _newTabButton.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void OnCollapseTabs(object sender, RoutedEventArgs e)
    {
        var collapsing = !_settings.VerticalCollapsed;
        _settings.VerticalCollapsed = collapsing;
        _settings.Commit();
        // The list takes its new width at once (the page beside it resizes once, not on every frame), and its contents fade and
        // slide into their new shape: a move the graphics card makes from a picture, cheap on any laptop.
        SideTabs.BeginAnimation(WidthProperty, null);
        SideTabs.Width = collapsing ? 56 : 248;
        FinishCompact();
        var slide = SideTabs.RenderTransform as TranslateTransform ?? new TranslateTransform();
        SideTabs.RenderTransform = slide;
        var away = _settings.TabSide == TabSide.Left ? -1 : 1;
        Motion.Run(SideTabs, slide, fromX: away * (collapsing ? 6 : 14), toX: 0, fromOpacity: 0.35, toOpacity: 1, Motion.Ms(240), new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    private void FinishCompact()
    {
        foreach (var h in _headers.Values) h.ApplyLayout(_settings.TabSide, Compact);
        SideTitle.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.HorizontalAlignment = Compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        StyleNewTabButton();
    }

    private void BuildSideChooser()
    {
        foreach (var (side, label) in new[] { (TabSide.Top, "Top"), (TabSide.Bottom, "Bottom"), (TabSide.Left, "Left"), (TabSide.Right, "Right") })
        {
            var r = new RadioButton { Style = (Style)FindResource("Segment"), Content = label, Tag = side, GroupName = "tabside", ToolTip = $"Tabs on the {label.ToLowerInvariant()}", FontSize = 11.5 };
            var chosen = side;
            r.Checked += (_, _) => SetTabSide(chosen);
            SideChooser.Children.Add(r);
        }
    }

    // ---- Drawer: one panel at a time, docked beside the page ----

    internal double TargetDrawerWidth => ActualWidth > 0 ? Math.Min(400, Math.Max(240, ActualWidth - 280)) : 400;

    internal void OpenDrawer(string title, UIElement body)
    {
        DrawerTitle.Text = title;
        DrawerBody.Content = body;
        MenuPopup.IsOpen = false;
        if (_toast is { IsOpen: true }) _toast.IsOpen = false; // it would hang over the panel; the panel says the same
        if (_drawerOpen) return;
        _drawerOpen = true;
        // Its room is made once (the page beside it resizes once), then the card slides a little and fades in.
        Drawer.Width = TargetDrawerWidth;
        Motion.Run(DrawerCard, DrawerSlide, fromX: 48, toX: 0, fromOpacity: 0, toOpacity: 1, Motion.Ms(280), new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    internal void CloseDrawer()
    {
        if (!_drawerOpen) return;
        _drawerOpen = false;
        Motion.Run(DrawerCard, DrawerSlide, fromX: 0, toX: 48, fromOpacity: 1, toOpacity: 0, Motion.Ms(200), new CubicEase { EasingMode = EasingMode.EaseIn }, done: () =>
        {
            if (_drawerOpen) return; // opened again meanwhile
            Drawer.Width = 0; // the page takes the room back once, at the end
            DrawerBody.Content = null;
        });
        Active?.View?.Focus();
    }

    /// <summary>What the side panel shows (for tests); nothing while it is closed.</summary>
    internal object? DrawerContent => _drawerOpen ? DrawerBody.Content : null;

    internal bool IsDrawerShowing(string title) => _drawerOpen && DrawerTitle.Text == title;

    private void ToggleDrawer(string title, Action show)
    {
        if (IsDrawerShowing(title)) CloseDrawer();
        else show();
    }

    private void OnDrawerClose(object sender, RoutedEventArgs e) => CloseDrawer();
    private void OnDownloads(object sender, RoutedEventArgs e) => ToggleDrawer("Downloads", ShowDownloads);
    private void OnMenuDownloads(object sender, RoutedEventArgs e) => ShowDownloads();
    private void OnMenuBookmarks(object sender, RoutedEventArgs e) => ShowBookmarks();
    private void OnMenuExtensions(object sender, RoutedEventArgs e) => ShowExtensions();
    private void OnMenuImport(object sender, RoutedEventArgs e) => ShowImport();
    internal void ShowImport() => OpenDrawer("Import", new Controls.ImportPanel(this));
    private void OnMenuHistory(object sender, RoutedEventArgs e) => ShowHistory();
    private void OnMenuSettings(object sender, RoutedEventArgs e) => ShowSettings();
    private void OnMenuPrivate(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; OpenPrivateWindow(); }
    private void OnBookmark(object sender, RoutedEventArgs e) => BookmarkActive();
    private void OnShield(object sender, RoutedEventArgs e) => ToggleDrawer("Privacy", ShowShield);

    // ---- For the self-test ----

    internal string TabsPlacement =>
        Tabs.Parent == TopHost ? "top" : Tabs.Parent == BottomHost ? "bottom" : SideTabs.Parent == LeftHost ? "left" : SideTabs.Parent == RightHost ? "right" : "nowhere";

    internal Size PagesSize => new(Pages.ActualWidth, Pages.ActualHeight);
    internal double SideWidth => SideTabs.ActualWidth;
    internal double DrawerWidth => Drawer.ActualWidth;
    internal string DataFolder => _dataFolder;
    internal int VisibleTabHeaders => Tabs.Children.OfType<Controls.TabHeader>().Count(h => h.ActualWidth > 20 && h.ActualHeight > 20);
    internal void ToggleCollapse() => OnCollapseTabs(this, new RoutedEventArgs());
    internal bool DownloadBadgeShown => DownloadBadgeHost.Visibility == Visibility.Visible;
    internal string DownloadBadgeText => DownloadBadge.Text;

    internal async Task RunScriptAsync(Models.BrowserTab tab, string script)
    {
        if (tab.View?.CoreWebView2 is { } core) await core.ExecuteScriptAsync(script);
    }
}
