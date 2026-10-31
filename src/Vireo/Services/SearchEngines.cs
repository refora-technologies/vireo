using System.Text.Json;

namespace Vireo.Services;

/// <summary>A search engine Vireo knows how to talk to: where a search goes and where it asks for suggestions.</summary>
public sealed record SearchEngine(string Name, string Template, string SuggestTemplate);

public static class SearchEngines
{
    public static readonly IReadOnlyList<SearchEngine> All =
    [
        new("Google", "https://www.google.com/search?q={0}", "https://suggestqueries.google.com/complete/search?client=chrome&q={0}"),
        new("DuckDuckGo", "https://duckduckgo.com/?q={0}", "https://duckduckgo.com/ac/?q={0}&type=list"),
        new("Bing", "https://www.bing.com/search?q={0}", "https://api.bing.com/osjson.aspx?query={0}"),
        new("Brave", "https://search.brave.com/search?q={0}", "https://search.brave.com/api/suggest?q={0}"),
    ];

    /// <summary>The engine a saved search address belongs to, or null for one Vireo does not know.</summary>
    public static SearchEngine? For(string? template) =>
        All.FirstOrDefault(e => string.Equals(e.Template, template, StringComparison.OrdinalIgnoreCase));

    /// <summary>The address to ask for suggestions about <paramref name="query"/>, or null when the engine has none.
    /// Suggestions always come from the engine the person chose, never from another one.</summary>
    public static string? SuggestUrl(string? template, string query) =>
        For(template) is { } e && !string.IsNullOrWhiteSpace(query) ? string.Format(e.SuggestTemplate, Uri.EscapeDataString(query)) : null;

    /// <summary>Reads the usual answer, <c>["query", ["suggestion", ...], ...]</c>. Anything else gives an empty list.</summary>
    public static List<string> ParseSuggestions(string json, int max = 5)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2 || root[1].ValueKind != JsonValueKind.Array) return list;
            foreach (var item in root[1].EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (string.IsNullOrWhiteSpace(text) || list.Contains(text, StringComparer.OrdinalIgnoreCase)) continue;
                list.Add(text.Trim());
                if (list.Count >= max) break;
            }
        }
        catch (JsonException) { /* not suggestions */ }
        return list;
    }
}
