using Netclaw.Search;
using Xunit;

namespace Netclaw.Search.Tests;

public class BraveSearchBackendTests
{
    [Fact]
    public void ParseResults_extracts_results_from_fixture()
    {
        var json = LoadFixture("brave-search-akka-dotnet.json");
        var results = BraveSearchBackend.ParseResults(json, 30);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void ParseResults_extracts_titles_and_urls()
    {
        var json = LoadFixture("brave-search-akka-dotnet.json");
        var results = BraveSearchBackend.ParseResults(json, 30);

        var first = results[0];
        Assert.Contains("akka.net", first.Url);
        Assert.Contains("akkadotnet", first.Title.ToLowerInvariant());
    }

    [Fact]
    public void ParseResults_extracts_descriptions()
    {
        var json = LoadFixture("brave-search-akka-dotnet.json");
        var results = BraveSearchBackend.ParseResults(json, 30);

        var first = results[0];
        Assert.NotEmpty(first.Snippet);
        Assert.Contains("Akka", first.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseResults_respects_max_results()
    {
        var json = LoadFixture("brave-search-akka-dotnet.json");
        var results = BraveSearchBackend.ParseResults(json, 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void ParseResults_returns_empty_for_missing_web_section()
    {
        var json = """{"query":{"original":"test"}}""";
        var results = BraveSearchBackend.ParseResults(json, 10);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseResults_returns_empty_for_empty_results()
    {
        var json = """{"web":{"type":"search","results":[]}}""";
        var results = BraveSearchBackend.ParseResults(json, 10);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseResults_skips_entries_missing_url()
    {
        var json = """
        {
          "web": {
            "results": [
              {"title": "No URL", "description": "Missing url field"},
              {"title": "Has URL", "url": "https://example.com", "description": "Valid"}
            ]
          }
        }
        """;
        var results = BraveSearchBackend.ParseResults(json, 10);

        Assert.Single(results);
        Assert.Equal("https://example.com", results[0].Url);
    }

    private static string LoadFixture(string filename)
    {
        var assembly = typeof(BraveSearchBackendTests).Assembly;
        var resourceName = $"Netclaw.Search.Tests.Fixtures.{filename}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Fixture not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
