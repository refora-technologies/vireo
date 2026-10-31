using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vireo;
using Vireo.Models;
using Vireo.Services;

namespace Vireo.Controls;

public partial class TabHeader : UserControl
{
    private static readonly Duration Quick = new(TimeSpan.FromMilliseconds(140));
    private bool _hover;
    private bool _active;
    private Storyboard? _spin;
    private TabSide _side = TabSide.Top;
    private bool _compact;

    public BrowserTab Tab { get; }

    public event Action<BrowserTab>? Selected;
    public event Action<BrowserTab>? CloseRequested;
    public event Action<BrowserTab>? DuplicateRequested;
    public event Action<BrowserTab>? PinToggled;
    public event Action<BrowserTab>? CloseOthersRequested;
    public event Action<BrowserTab>? CloseRightRequested;
    /// <summary>The tab is being dragged; the point is where the mouse is, relative to the tab strip.</summary>
    public event Action<BrowserTab, Point>? Dragged;
    /// <summary>The tab is being dragged; the point is on the screen (pixels); "outside" when it has left its tab strip.</summary>
    public event Action<BrowserTab, Point, bool>? DragMoving;
    /// <summary>The tab was let go after a drag; the point is on the screen (pixels); "outside" when away from its tab strip.</summary>
    public event Action<BrowserTab, Point, bool>? DragEnded;
    public event Action<BrowserTab>? MoveToWindowRequested;
    /// <summary>Whether "Move to new window" makes sense (not for the only tab, not in a private window).</summary>
    public Func<bool>? CanMoveToWindow { get; set; }

    private Point? _press;
    private bool _dragging;

    public TabHeader(BrowserTab tab)
    {
        InitializeComponent();
        Tab = tab;
        Root.Background = new SolidColorBrush(Colors.Transparent);
        Tab.PropertyChanged += OnTabChanged;
        Refresh();
        SetupContextMenu();
        Unloaded += (_, _) => Tab.PropertyChanged -= OnTabChanged;
    }

    /// <summary>
    /// Shapes the tab for where the tab strip sits. Top and bottom tabs are rounded on the outside edge;
    /// side tabs are rounded rows, and in compact form show only their icon.
    /// </summary>
    public void ApplyLayout(TabSide side, bool compact)
    {
        _side = side;
        _compact = compact;
        var vertical = side is TabSide.Left or TabSide.Right;
        (Height, Margin, Root.CornerRadius) = side switch
        {
            // Pills floating on the frame, in a row or in a list.
            TabSide.Top or TabSide.Bottom => (32.0, new Thickness(0, 6, 0, 6), new CornerRadius(10)),
            _ => (double.NaN, new Thickness(6, 1, 6, 1), new CornerRadius(10)),
        };
        AccentBar.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
        AccentBar.HorizontalAlignment = side == TabSide.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        AccentBar.Margin = side == TabSide.Right ? new Thickness(0, 0, -8, 0) : new Thickness(-8, 0, 0, 0);
        var small = vertical && compact;
        IconCol.Width = small ? new GridLength(1, GridUnitType.Star) : new GridLength(18);
        TitleCol.Width = small ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        CloseCol.Width = small ? new GridLength(0) : new GridLength(22);
        IconHost.HorizontalAlignment = small ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        Root.Padding = small ? new Thickness(0) : new Thickness(10, 0, 6, 0);
        Title.Visibility = Close.Visibility = small ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    /// <summary>Marks this as the tab being shown. The change fades rather than snaps.</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        UpdateVisibilityForWidth(ActualWidth);
        ApplyLook();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        UpdateVisibilityForWidth(info.NewSize.Width);
    }

    private void UpdateVisibilityForWidth(double w)
    {
        if (_side is TabSide.Top or TabSide.Bottom && w > 0)
        {
            if (w < 42)
            {
                Title.Visibility = Visibility.Collapsed;
                Close.Visibility = Visibility.Collapsed;
                IconHost.HorizontalAlignment = HorizontalAlignment.Center;
                Root.Padding = new Thickness(0);
            }
            else if (w < 68)
            {
                Title.Visibility = Visibility.Visible;
                Close.Visibility = _active ? Visibility.Visible : Visibility.Collapsed;
                IconHost.HorizontalAlignment = HorizontalAlignment.Left;
                Root.Padding = new Thickness(8, 0, 4, 0);
            }
            else
            {
                Title.Visibility = Visibility.Visible;
                Close.Visibility = Visibility.Visible;
                IconHost.HorizontalAlignment = HorizontalAlignment.Left;
                Root.Padding = new Thickness(10, 0, 6, 0);
            }
        }
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        Title.Text = Tab.DisplayTitle;
        Favicon.Source = Tab.Favicon;
        Favicon.Visibility = Tab.Favicon is not null && !Tab.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        FallbackIcon.Visibility = Tab.Favicon is null && !Tab.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        SetSpinning(Tab.IsLoading);

        var asleep = Tab.Life is TabLife.Suspended or TabLife.Discarded;
        // E767: speaker. E708: moon, for a tab that is asleep.
        Badge.Text = Tab.IsPlayingAudio ? Glyph.Speaker : Tab.IsPinned ? ((char)0xE718).ToString() : asleep ? Glyph.Moon : "";
        Badge.Visibility = Badge.Text.Length > 0 && !(_compact && _side is TabSide.Left or TabSide.Right) ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = asleep ? $"{Tab.DisplayTitle}\nAsleep to save memory. Click to wake it." : Tab.IsPinned ? $"{Tab.DisplayTitle} (Pinned)" : Tab.DisplayTitle;
        UpdateVisibilityForWidth(ActualWidth);
        ApplyLook();
    }

    private void SetSpinning(bool on)
    {
        Spinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on && _spin is null)
        {
            var turn = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(900))) { RepeatBehavior = RepeatBehavior.Forever };
            _spin = new Storyboard();
            Storyboard.SetTarget(turn, Spinner);
            Storyboard.SetTargetProperty(turn, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
            _spin.Children.Add(turn);
            _spin.Begin(this, true);
        }
        else if (!on && _spin is not null)
        {
            _spin.Stop(this);
            _spin = null;
        }
    }

    private void ApplyLook()
    {
        var asleep = Tab.Life is TabLife.Suspended or TabLife.Discarded;
        // The shown tab is a lifted pill of light on the frame; others only glow faintly under the mouse.
        var back = _active ? System.Windows.Media.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : _hover ? Color("HoverColor") : Colors.Transparent;
        Root.BorderBrush = new SolidColorBrush(_active ? System.Windows.Media.Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Colors.Transparent);
        Animate(Root.Background, SolidColorBrush.ColorProperty, back);
        Title.Foreground = _active ? (Brush)FindResource("Text") : (Brush)FindResource("Text2");
        Title.FontWeight = _active ? FontWeights.SemiBold : FontWeights.Normal;
        Title.Opacity = asleep && !_active ? 0.55 : 1;
        Favicon.Opacity = asleep && !_active ? 0.55 : 1;
        Animate(AccentBar, OpacityProperty, _active ? 1.0 : 0.0);
        Animate(Close, OpacityProperty, (_active || _hover) && Close.Visibility == Visibility.Visible ? 1.0 : 0.0);
    }

    private static Color Color(string key) => (Color)Application.Current.FindResource(key);

    private static void Animate(Animatable target, DependencyProperty property, object to)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (to is Color c) target.BeginAnimation(property, new ColorAnimation(c, Quick) { EasingFunction = ease });
        else target.BeginAnimation(property, new DoubleAnimation((double)to, Quick) { EasingFunction = ease });
    }

    private static void Animate(UIElement target, DependencyProperty property, double to)
    {
        var anim = new DoubleAnimation(to, Quick) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        target.BeginAnimation(property, anim);
    }

    private void OnEnter(object sender, MouseEventArgs e) { _hover = true; ApplyLook(); }
    private void OnLeave(object sender, MouseEventArgs e) { _hover = false; ApplyLook(); }
    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        Selected?.Invoke(Tab);
        if (Parent is IInputElement strip) { _press = e.GetPosition(strip); _dragging = false; CaptureMouse(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_press is not { } start || e.LeftButton != MouseButtonState.Pressed || Parent is not IInputElement strip) return;
        var at = e.GetPosition(strip);
        // A small wobble while clicking is not a drag.
        if (!_dragging && Math.Abs(at.X - start.X) < 6 && Math.Abs(at.Y - start.Y) < 6) return;
        _dragging = true;
        var outside = IsOutside(strip, at);
        if (!outside) Dragged?.Invoke(Tab, at);
        Opacity = outside ? 0.45 : 1;
        DragMoving?.Invoke(Tab, PointToScreen(e.GetPosition(this)), outside);
    }

    /// <summary>Well away from the strip (below it into the page, or outside the window).</summary>
    private static bool IsOutside(IInputElement strip, Point at)
    {
        if (strip is not FrameworkElement f) return false;
        var zone = new Rect(f.RenderSize);
        zone.Inflate(40, 40);
        return !zone.Contains(at);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var wasDragging = _dragging;
        _press = null;
        _dragging = false;
        Opacity = 1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (wasDragging && Parent is IInputElement strip)
            DragEnded?.Invoke(Tab, PointToScreen(e.GetPosition(this)), IsOutside(strip, e.GetPosition(strip)));
    }

    /// <summary>Moving the tab in the strip takes it out of the tree for a moment, which drops the mouse; this takes it back.</summary>
    internal void KeepDragging()
    {
        if (_press is not null && Mouse.LeftButton == MouseButtonState.Pressed) CaptureMouse();
    }
    private void OnUp(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Middle) CloseRequested?.Invoke(Tab); }
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(Tab);

    private void SetupContextMenu()
    {
        ContextMenuOpening += (_, _) =>
        {
            var menu = new ContextMenu();
            var reload = new MenuItem { Header = "Reload", InputGestureText = "F5" };
            reload.Click += (_, _) => { if (Tab.View?.CoreWebView2 is { } c) c.Reload(); };
            var duplicate = new MenuItem { Header = "Duplicate tab" };
            duplicate.Click += (_, _) => DuplicateRequested?.Invoke(Tab);
            var toWindow = new MenuItem { Header = "Move to new window", IsEnabled = CanMoveToWindow?.Invoke() == true };
            toWindow.Click += (_, _) => MoveToWindowRequested?.Invoke(Tab);
            var pin = new MenuItem { Header = Tab.IsPinned ? "Unpin tab" : "Pin tab" };
            pin.Click += (_, _) => { PinToggled?.Invoke(Tab); Refresh(); };
            var isMuted = Tab.View?.CoreWebView2?.IsMuted == true;
            var mute = new MenuItem { Header = isMuted ? "Unmute tab" : "Mute tab" };
            mute.Click += (_, _) =>
            {
                if (Tab.View?.CoreWebView2 is { } c) { c.IsMuted = !c.IsMuted; Refresh(); }
            };
            var close = new MenuItem { Header = "Close", InputGestureText = "Ctrl+W" };
            close.Click += (_, _) => CloseRequested?.Invoke(Tab);
            var closeOthers = new MenuItem { Header = "Close other tabs" };
            closeOthers.Click += (_, _) => CloseOthersRequested?.Invoke(Tab);
            var closeRight = new MenuItem { Header = "Close tabs to the right" };
            closeRight.Click += (_, _) => CloseRightRequested?.Invoke(Tab);

            menu.Items.Add(reload);
            menu.Items.Add(duplicate);
            menu.Items.Add(toWindow);
            menu.Items.Add(pin);
            menu.Items.Add(mute);
            menu.Items.Add(new Separator());
            menu.Items.Add(close);
            menu.Items.Add(closeOthers);
            menu.Items.Add(closeRight);
            ContextMenu = menu;
        };
    }
}
