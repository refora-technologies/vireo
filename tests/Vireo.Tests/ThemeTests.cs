using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Vireo.Tests;

public class ThemeTests
{
    private static string? ThemeFile()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "src", "Vireo", "Themes", "Theme.xaml"))) d = d.Parent;
        return d is null ? null : Path.Combine(d.FullName, "src", "Vireo", "Themes", "Theme.xaml");
    }

    internal static List<string> ColourMisuse(string xaml) =>
        [.. Regex.Matches(xaml, @"<Setter [^>]*Property=""(Background|Foreground|BorderBrush|Fill|Stroke)""[^>]*Value=""\{StaticResource \w+Color\}""").Select(m => m.Value),
           .. Regex.Matches(xaml, @"(Background|Foreground|BorderBrush|Fill|Stroke)=""\{StaticResource \w+Color\}""").Select(m => m.Value)];

    [Fact]
    public void The_check_itself_catches_the_mistake_that_crashed_the_app()
    {
        Assert.Single(ColourMisuse("""<Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bg" Property="Background" Value="{StaticResource HoverColor}"/></Trigger>"""));
        Assert.Single(ColourMisuse("""<Border Background="{StaticResource HoverColor}"/>"""));
        Assert.Empty(ColourMisuse("""<Border Background="{StaticResource Surface3}"/><Setter Property="Background"><Setter.Value><SolidColorBrush Color="{StaticResource HoverColor}"/></Setter.Value></Setter>"""));
    }

    [Fact]
    public void Brush_properties_in_the_theme_are_never_given_a_colour()
    {
        // A "Color" resource set on a Background/Foreground/BorderBrush crashes the moment the style applies (for a hover trigger: when the mouse arrives).
        var file = ThemeFile();
        if (file is null) return;
        Assert.Empty(ColourMisuse(File.ReadAllText(file)));
    }
}
