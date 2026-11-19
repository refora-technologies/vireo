using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vireo.Services;

namespace Vireo.Controls;

/// <summary>
/// Under a finished download: paste the checksum the site publishes and Vireo says whether the file is the one it meant.
/// With nothing pasted, it shows the file's SHA-256 to compare by eye or copy.
/// </summary>
public sealed class ChecksumBox : StackPanel
{
    private readonly string _path;
    private readonly TextBox _input = new() { FontSize = 12, FontFamily = UiKit.Mono, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = UiKit.Brush("Text"), CaretBrush = UiKit.Brush("Accent"), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
    private readonly TextBlock _hint = new() { Text = "Paste the checksum from the site (SHA-256, SHA-1 or MD5)", FontSize = 12, Foreground = UiKit.Brush("Text3"), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _result = new() { FontSize = 11.5, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0), Margin = new Thickness(2, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _go;
    private CancellationTokenSource? _work;

    public ChecksumBox(string path)
    {
        _path = path;
        Margin = new Thickness(0, 8, 0, 2);
        var field = new Grid();
        field.ColumnDefinitions.Add(new ColumnDefinition());
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var inner = new Grid { Margin = new Thickness(10, 0, 4, 0) };
        inner.Children.Add(_hint);
        inner.Children.Add(_input);
        field.Children.Add(inner);
        _go = new Button { Content = "Check", Style = (Style)Application.Current.FindResource("SecondaryButton"), FontSize = 11.5, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 3, 3, 3) };
        _go.Click += async (_, _) => await CheckAsync();
        Grid.SetColumn(_go, 1);
        field.Children.Add(_go);
        Children.Add(new Border { Background = UiKit.Brush("Bg"), BorderBrush = UiKit.Brush("Line2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Height = 32, Child = field });
        Children.Add(_result);
        _input.TextChanged += (_, _) => _hint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _input.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await CheckAsync(); };
        _result.Foreground = UiKit.Brush("Text2");
        Unloaded += (_, _) => _work?.Cancel();
        Loaded += (_, _) => _input.Focus();
    }

    private async Task CheckAsync()
    {
        var pasted = _input.Text;
        var kind = pasted.Trim().Length == 0 ? System.Security.Cryptography.HashAlgorithmName.SHA256 : Checksum.KindOf(pasted);
        if (kind is null) { Say("That is not a checksum. Sites list it as 64 letters and digits (SHA-256), or 40 (SHA-1), or 32 (MD5).", "Danger"); return; }
        if (!File.Exists(_path)) { Say("The file is no longer where it was saved.", "Danger"); return; }
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        _go.IsEnabled = false;
        Say("Reading the file…", "Text3");
        try
        {
            // Progress reports arrive a little later than they are made: one arriving after the answer must not hide it.
            var finished = false;
            var progress = new Progress<double>(f => { if (!finished) Say($"Reading the file… {f * 100:0}%", "Text3"); });
            var computed = await Task.Run(() => Checksum.OfFileAsync(_path, kind.Value, progress, ct), ct);
            finished = true;
            if (pasted.Trim().Length == 0) Say($"SHA-256: {computed}", "Text2");
            else if (Checksum.Matches(computed, pasted)) Say($"It matches. This is the file the site published ({kind.Value.Name}).", "Info");
            else Say($"It does not match. The file may be damaged or not the one the site meant: better not open it. Its {kind.Value.Name} is {computed}.", "Danger");
        }
        catch (OperationCanceledException) { /* closed */ }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say("The file could not be read: " + e.Message, "Danger"); }
        finally { _go.IsEnabled = true; }
    }

    private void Say(string text, string tone)
    {
        _result.Text = text;
        _result.Foreground = UiKit.Brush(tone);
        _result.Visibility = Visibility.Visible;
    }

    /// <summary>For tests: what the box says now.</summary>
    internal string Said => _result.Text;
    internal void PasteForTest(string text) => _input.Text = text;
    internal Task CheckForTestAsync() => CheckAsync();
}
