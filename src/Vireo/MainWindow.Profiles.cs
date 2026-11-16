using System.Windows;
using System.Windows.Media;
using Vireo.Services;

namespace Vireo;

// Profiles: separate browsers for separate parts of life (work, personal, a shared family PC...).
public partial class MainWindow
{
    internal Profile CurrentProfileInfo => Profiles.Find(App.ProfileId);

    /// <summary>The badge on the toolbar and the window's title say whose browser this is.</summary>
    internal void ShowProfile()
    {
        var p = CurrentProfileInfo;
        ProfileInitial.Text = p.Name.Length > 0 ? p.Name[..1].ToUpperInvariant() : "?";
        ProfileBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Color));
        ProfileButton.ToolTip = $"Profile: {p.Name}";
        if (!_private) Title = Profiles.All().Count > 1 || App.ProfileId.Length > 0 ? $"Vireo — {p.Name}" : "Vireo";
    }

    private void OnProfiles(object sender, RoutedEventArgs e) => ToggleDrawer("Profiles", ShowProfiles);
    internal void ShowProfiles() => OpenDrawer("Profiles", new Controls.ProfilesPanel(this));
}
