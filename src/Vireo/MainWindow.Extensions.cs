using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Vireo.Controls;
using Vireo.Services;

namespace Vireo;

// Extensions: the engine runs them, Vireo draws their buttons and popups.
public partial class MainWindow
{
    private ExtensionManager? _extensions;
    private Popup? _extensionPopup;
    private List<ExtensionInfo> _extensionList = [];

    internal ExtensionManager Extensions => _extensions ??= new ExtensionManager(_dataFolder);
    internal IReadOnlyList<ExtensionInfo> InstalledExtensions => _extensionList;
    internal CoreWebView2Profile? CurrentProfile => _tabs.Select(t => t.View?.CoreWebView2?.Profile).FirstOrDefault(p => p is not null);
    internal int ExtensionStripCount => ExtensionStrip.Children.Count;
    internal FrameworkElement? ExtensionStripFirst => ExtensionStrip.Children.OfType<FrameworkElement>().FirstOrDefault();
    internal bool ExtensionsAvailable => !_private && CurrentProfile is not null;

    internal void ShowExtensions() => OpenDrawer("Extensions", new ExtensionsPanel(this));

    /// <summary>Reads what is installed and redraws the buttons next to the address bar.</summary>
    internal async Task RefreshExtensionsAsync()
    {
        if (!ExtensionsAvailable) { ExtensionStrip.Children.Clear(); return; }
        try { _extensionList = await Extensions.ListAsync(CurrentProfile!); }
        catch { _extensionList = []; }
        ExtensionStrip.Children.Clear();
        foreach (var ext in _extensionList.Where(e => e.Enabled && (e.PopupUrl is not null || e.OptionsUrl is not null)))
        {
            var info = ext;
            var button = new Button { Style = (Style)FindResource("IconButton"), ToolTip = info.Manifest.Name, Tag = info.Id };
            button.Content = ExtensionIcon(info, 18);
            button.Click += async (_, _) => await OpenExtensionAsync(info, button);
            ExtensionStrip.Children.Add(button);
        }
    }

    /// <summary>The extension's own picture, or its first letter on a coloured tile.</summary>
    internal static UIElement ExtensionIcon(ExtensionInfo info, double size)
    {
        try
        {
            if (info.IconFile is { } file && File.Exists(file) && !file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(file);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = (int)(size * 2);
                image.EndInit();
                image.Freeze();
                return new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
            }
        }
        catch { /* a broken picture: use the letter */ }
        var hue = Math.Abs(info.Manifest.Name.GetHashCode()) % 360;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 4), Background = new SolidColorBrush(Hsl(hue, 0.45, 0.42)),
            Child = new TextBlock { Text = info.Manifest.Name[..1].ToUpperInvariant(), Foreground = Brushes.White, FontSize = size * 0.55, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    private static Color Hsl(double h, double s, double l)
    {
        double C(double n) { var k = (n + h / 30) % 12; var a = s * Math.Min(l, 1 - l); return l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1)); }
        return Color.FromRgb((byte)(C(0) * 255), (byte)(C(8) * 255), (byte)(C(4) * 255));
    }

    /// <summary>A click on an extension's button: its popup (a small page of its own) opens under it.</summary>
    internal async Task OpenExtensionAsync(ExtensionInfo info, FrameworkElement anchor)
    {
        // The same button again closes its popup.
        if (_extensionPopup?.PlacementTarget == anchor) { CloseExtensionPopup(); return; }
        CloseExtensionPopup();
        var url = info.PopupUrl ?? info.OptionsUrl;
        if (url is null || _env is null) return;

        var view = new WebView2 { Width = 360, Height = 300, DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 22, 29, 28) };
        var card = new Border
        {
            Background = UiKit.Brush("Surface1"), BorderBrush = UiKit.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Child = new Border { CornerRadius = new CornerRadius(9), ClipToBounds = true, Child = view }, Margin = new Thickness(0, 6, 0, 18),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black },
        };
        var popup = new Popup { AllowsTransparency = true, StaysOpen = true, PopupAnimation = PopupAnimation.None, Placement = PlacementMode.Bottom, PlacementTarget = anchor, HorizontalOffset = -300, Child = card };
        _extensionPopup = popup;
        popup.IsOpen = true;
        try
        {
            await view.EnsureCoreWebView2Async(_env);
            var core = view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.NewWindowRequested += (_, e) => { e.Handled = true; _ = Dispatcher.InvokeAsync(() => OpenTabAsync(e.Uri)); CloseExtensionPopup(); };
            core.WindowCloseRequested += (_, _) => CloseExtensionPopup();
            core.NavigationCompleted += async (_, _) =>
            {
                // fit the popup to what it shows, as other browsers do
                try
                {
                    var json = await core.ExecuteScriptAsync("JSON.stringify([Math.ceil(Math.max(document.documentElement.scrollWidth, document.body ? document.body.scrollWidth : 0)), Math.ceil(Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0))])");
                    var sizes = System.Text.Json.JsonSerializer.Deserialize<int[]>(System.Text.Json.JsonSerializer.Deserialize<string>(json)!);
                    if (sizes is { Length: 2 } && popup.IsOpen)
                    {
                        view.Width = Math.Clamp(sizes[0], 280, 800);
                        view.Height = Math.Clamp(sizes[1], 120, 600);
                    }
                }
                catch { /* keep the default size */ }
            };
            core.Navigate(url);
        }
        catch { CloseExtensionPopup(); }
    }

    internal void CloseExtensionPopup()
    {
        if (_extensionPopup is null) return;
        var popup = _extensionPopup;
        _extensionPopup = null;
        popup.IsOpen = false;
        if (popup.Child is Border { Child: Border { Child: WebView2 view } }) { try { view.Dispose(); } catch { /* gone */ } }
    }

    private static bool IsWithin(DependencyObject? node, UIElement? ancestor)
    {
        for (var d = node; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == ancestor) return true;
        return false;
    }

    internal WebView2? ExtensionPopupView => _extensionPopup?.Child is Border { Child: Border { Child: WebView2 view } } ? view : null;

    // ---- Install from a person's files ----

    internal async Task<string> InstallExtensionFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the extension's folder (the one with manifest.json)" };
        if (dialog.ShowDialog(this) != true) return "";
        return await InstallExtensionAsync(() => Extensions.InstallFolderAsync(CurrentProfile!, dialog.FolderName));
    }

    internal async Task<string> InstallExtensionFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose an extension file", Filter = "Extension files (*.zip;*.crx)|*.zip;*.crx" };
        if (dialog.ShowDialog(this) != true) return "";
        return await InstallExtensionAsync(() => Extensions.InstallFileAsync(CurrentProfile!, dialog.FileName));
    }

    internal async Task<string> InstallExtensionAsync(Func<Task<ExtensionInfo>> install)
    {
        if (!ExtensionsAvailable) return "Extensions are not available in a private window.";
        try
        {
            var info = await install();
            await RefreshExtensionsAsync();
            ShowToast("Extension added", info.Manifest.Name, null);
            return "";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or System.Runtime.InteropServices.COMException or ArgumentException or UnauthorizedAccessException)
        {
            return e is InvalidDataException ? e.Message : $"The browser engine could not load that extension. {e.Message} ({e.GetType().Name} 0x{e.HResult:X8})";
        }
    }
}
