using Netclaw.Search;
using Xunit;

namespace Netclaw.Search.Tests;

public class DuckDuckGoBackendTests
{
    [Fact]
    public void ParseResults_extracts_all_results_from_akka_fixture()
    {
        var html = LoadFixture("ddg-lite-akka-dotnet.html");
        var results = DuckDuckGoBackend.ParseResults(html, 30);

        Assert.Equal(10, results.Count);
    }

    [Fact]
    public void ParseResults_extracts_titles_and_urls()
    {
        var html = LoadFixture("ddg-lite-akka-dotnet.html");
        var results = DuckDuckGoBackend.ParseResults(html, 30);

        var first = results[0];
        Assert.Contains("akka.net", first.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GitHub", first.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseResults_extracts_snippets()
    {
        var html = LoadFixture("ddg-lite-akka-dotnet.html");
        var results = DuckDuckGoBackend.ParseResults(html, 30);

        var first = results[0];
        Assert.NotEmpty(first.Snippet);
        Assert.Contains("actor", first.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseResults_respects_max_results()
    {
        var html = LoadFixture("ddg-lite-akka-dotnet.html");
        var results = DuckDuckGoBackend.ParseResults(html, 3);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void ParseResults_handles_pizza_recipe_fixture()
    {
        var html = LoadFixture("ddg-lite-pizza-recipe.html");
        var results = DuckDuckGoBackend.ParseResults(html, 30);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.Url)));
        Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.Title)));
    }

    [Fact]
    public void ParseResults_returns_empty_for_no_results()
    {
        var html = "<html><body><table></table></body></html>";
        var results = DuckDuckGoBackend.ParseResults(html, 10);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseResults_decodes_html_entities_in_snippets()
    {
        var html = LoadFixture("ddg-lite-akka-dotnet.html");
        var results = DuckDuckGoBackend.ParseResults(html, 30);

        Assert.All(results, r =>
        {
            Assert.DoesNotContain("&amp;", r.Snippet, StringComparison.Ordinal);
            Assert.DoesNotContain("&#x27;", r.Snippet, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ParseResults_urls_are_absolute()
    {
        var html = LoadFixture("ddg-lite-akka-dotnet.html");
        var results = DuckDuckGoBackend.ParseResults(html, 30);

        Assert.All(results, r => Assert.StartsWith("http", r.Url, StringComparison.OrdinalIgnoreCase));
    }

    private static string LoadFixture(string filename)
    {
        var assembly = typeof(DuckDuckGoBackendTests).Assembly;
        var resourceName = $"Netclaw.Search.Tests.Fixtures.{filename}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Fixture not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
