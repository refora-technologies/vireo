using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vireo.Services;
using Vireo.Services.Import;

namespace Vireo.Controls;

/// <summary>Bring bookmarks, history and passwords from another browser, or from the files browsers export.</summary>
public sealed class ImportPanel : Grid
{
    private readonly MainWindow _window;
    private readonly StackPanel _list = new() { Margin = new Thickness(20, 0, 20, 24) };
    private readonly StackPanel _results = new() { Margin = new Thickness(0, 8, 0, 0) };

    public ImportPanel(MainWindow window)
    {
        _window = window;
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Children.Add(scroll);
        Build();
    }

    private void Build()
    {
        _list.Children.Clear();
        _list.Children.Add(Note("Nothing leaves this computer. Close the other browser first if it is open, or its files may be locked."));

        _list.Children.Add(UiKit.Heading("Browsers on this computer"));
        var sources = OtherBrowsers.Detect();
        if (sources.Count == 0) _list.Children.Add(UiKit.Empty(0xE774, "No other browser found", "Chrome, Edge, Brave, Vivaldi, Opera and Firefox are looked for. You can still import from a file below."));
        foreach (var source in sources) _list.Children.Add(SourceCard(source));

        _list.Children.Add(UiKit.Heading("From a file"));
        var files = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        files.Children.Add(FileButton("Bookmarks (.html)…", "Bookmark files (*.html;*.htm)|*.html;*.htm", ImportBookmarkFile));
        _list.Children.Add(files);
        _list.Children.Add(Note("Saved passwords are not copied: Vireo's engine (the same one Microsoft Edge uses) offers to save each one the first time you sign in to a site."));
        _list.Children.Add(_results);
    }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 12, LineHeight = 17, Foreground = UiKit.Brush("Text3"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 10) };

    private Button FileButton(string text, string filter, Func<string, ImportReport> work)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 6) };
        b.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter };
            if (dialog.ShowDialog(_window) != true) return;
            Show(text.Split(' ')[0], Try(() => work(dialog.FileName)));
        };
        return b;
    }

    private static ImportReport Try(Func<ImportReport> work)
    {
        try { return work(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var r = new ImportReport();
            r.Notes.Add("The file could not be read: " + e.Message);
            return r;
        }
    }

    private ImportReport ImportBookmarkFile(string path)
    {
        var r = new ImportReport();
        r.Bookmarks = _window.Library.Import(OtherBrowsers.BookmarksFromHtml(File.ReadAllText(path)), []).Bookmarks;
        return r;
    }

    private UIElement SourceCard(ImportSource source)
    {
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), Background = UiKit.Brush("Surface2"), BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock { Text = source.Kind.ToString()[..1], FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("Text2"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = source.Kind.ToString(), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("Text") });
        names.Children.Add(new TextBlock { Text = source.Name, FontSize = 11.5, Foreground = UiKit.Brush("Text3") });
        head.Children.Add(names);
        stack.Children.Add(head);

        var checks = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        CheckBox Check(string text, bool on, bool enabled = true) =>
            new() { Content = text, IsChecked = on, IsEnabled = enabled, Foreground = UiKit.Brush("Text"), Margin = new Thickness(0, 0, 16, 6), VerticalContentAlignment = VerticalAlignment.Center };
        var bookmarks = Check("Bookmarks", true);
        var history = Check("History", true);
        checks.Children.Add(bookmarks);
        checks.Children.Add(history);
        stack.Children.Add(checks);

        var go = new Button { Content = "Import", Style = (Style)Application.Current.FindResource("PrimaryButton"), Padding = new Thickness(18, 6, 18, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        go.Click += async (_, _) =>
        {
            go.IsEnabled = false;
            go.Content = "Importing…";
            var b = bookmarks.IsChecked == true; var h = history.IsChecked == true; const bool p = true; // adds the note on how to bring passwords
            var report = await Task.Run(() =>
            {
                try { return OtherBrowsers.Import(source, b, h, p, _window.Library); }
                catch (ImportException e) { var r = new ImportReport(); r.Notes.Add(e.Message); return r; }
            });
            go.IsEnabled = true;
            go.Content = "Import";
            Show(source.Title, report);
        };
        stack.Children.Add(go);
        return new Border { BorderBrush = UiKit.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 8), Child = stack };
    }

    private void Show(string what, ImportReport report)
    {
        var card = new StackPanel();
        card.Children.Add(new TextBlock { Text = what, FontSize = 11, Foreground = UiKit.Brush("Text3") });
        card.Children.Add(new TextBlock { Text = report.Summary, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        foreach (var note in report.Notes)
            card.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = UiKit.Brush("Text2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        _results.Children.Insert(0, new Border { BorderBrush = UiKit.Brush("Info"), BorderThickness = new Thickness(3, 0, 0, 0), Background = UiKit.Brush("Surface2"), CornerRadius = new CornerRadius(4), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 8), Child = card });
        _window.ShowToast("Import finished", report.Summary, null);
    }
}
