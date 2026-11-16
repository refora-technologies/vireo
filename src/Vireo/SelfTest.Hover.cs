using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vireo;

internal sealed partial class SelfTest
{
    /// <summary>VIREO_STEPS=hostile,extensions runs only those parts of the self-test (the quick start-up checks always run).</summary>
    private static readonly HashSet<string>? Only = Environment.GetEnvironmentVariable("VIREO_STEPS")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task Step(string name, Func<Task> run)
    {
        if (Only is not null && !Only.Contains(name)) return;
        await run();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>
    /// Puts every button in a panel into its "mouse over" and "pressed" looks, the way WPF does when the mouse arrives. A button style
    /// that breaks then (a colour given where a brush belongs) crashed the app once. The real mouse is not moved: driving the
    /// mouse is the kind of thing remote-control malware does, and security software rightly watches for it.
    /// </summary>
    private async Task HoverEveryButtonAsync(string what)
    {
        await Task.Delay(300);
        var errors = App.Errors;
        var problems = new List<string>();
        var buttons = Descendants<Button>(w.DrawerBodyForTest).Where(b => b.IsVisible).Take(60).ToList();
        foreach (var b in buttons)
        {
            foreach (var trigger in (b.Template?.Triggers.Cast<TriggerBase>() ?? []).Concat(b.Style?.Triggers.Cast<TriggerBase>() ?? []).OfType<Trigger>())
            {
                if (trigger.Property != UIElement.IsMouseOverProperty && trigger.Property != System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty) continue;
                foreach (var setter in trigger.Setters.OfType<Setter>())
                {
                    var target = setter.TargetName is null ? b : b.Template?.FindName(setter.TargetName, b) as DependencyObject;
                    if (target is null) continue;
                    try
                    {
                        var old = target.ReadLocalValue(setter.Property);
                        target.SetValue(setter.Property, setter.Value is DynamicResourceExtension d ? b.FindResource(d.ResourceKey) : setter.Value);
                        (target as UIElement)?.UpdateLayout();
                        if (old == DependencyProperty.UnsetValue) target.ClearValue(setter.Property); else target.SetValue(setter.Property, old);
                    }
                    catch (Exception e) { problems.Add($"{b.Content}: {setter.Property.Name} = {setter.Value} ({e.GetType().Name})"); }
                }
            }
        }
        await Task.Delay(100);
        Check($"every button in the {what} ({buttons.Count}) can show its hover and pressed look", buttons.Count > 0 && problems.Count == 0 && App.Errors == errors, string.Join(" | ", problems.Take(3)));
    }
}
