using Vireo.Services;
using Xunit;

namespace Vireo.Tests;

public class SearchTests
{
    [Fact]
    public void Google_is_the_default_and_comes_first()
    {
        Assert.Equal("Google", SearchEngines.All[0].Name);
        Assert.Equal(UrlInput.DefaultSearch, SearchEngines.All[0].Template);
        Assert.Equal(SearchEngines.All[0].Template, new Settings().SearchTemplate);
    }

    [Theory]
    [InlineData("https://www.google.com/search?q={0}", "suggestqueries.google.com")]
    [InlineData("https://duckduckgo.com/?q={0}", "duckduckgo.com/ac")]
    [InlineData("https://www.bing.com/search?q={0}", "api.bing.com")]
    [InlineData("https://search.brave.com/search?q={0}", "search.brave.com/api/suggest")]
    public void Suggestions_are_always_asked_of_the_engine_that_was_chosen(string template, string expectedHost)
    {
        var url = SearchEngines.SuggestUrl(template, "blue whales");
        Assert.NotNull(url);
        Assert.Contains(expectedHost, url);
        Assert.Contains("blue%20whales", url);
    }

    [Fact]
    public void An_engine_that_is_not_known_gets_no_suggestions_rather_than_a_default_one()
    {
        Assert.Null(SearchEngines.SuggestUrl("https://example.org/find?q={0}", "cats"));
        Assert.Null(SearchEngines.SuggestUrl(SearchEngines.All[0].Template, "  "));
    }

    [Theory]
    [InlineData("""["cats",["cats and dogs","cat food","Cat Food","cats"],[],{"google:suggestsubtypes":[[3],[3]]}]""", 3)]
    [InlineData("""["cats",["a","b"]]""", 2)]
    [InlineData("""["cats",[]]""", 0)]
    [InlineData("""{"error":"no"}""", 0)]
    [InlineData("not json", 0)]
    [InlineData("""["cats"]""", 0)]
    public void The_usual_answer_is_read_and_anything_else_is_ignored(string json, int expected) =>
        Assert.Equal(expected, SearchEngines.ParseSuggestions(json).Count);

    [Fact]
    public void Suggestions_are_capped_and_trimmed()
    {
        var json = """["x",[" one ","two","three","four","five","six","seven"]]""";
        var list = SearchEngines.ParseSuggestions(json, 5);
        Assert.Equal(5, list.Count);
        Assert.Equal("one", list[0]);
    }

    [Fact]
    public void The_saved_engine_is_recognised_from_its_address()
    {
        Assert.Equal("Bing", SearchEngines.For("https://www.bing.com/search?q={0}")?.Name);
        Assert.Null(SearchEngines.For(null));
    }
}
