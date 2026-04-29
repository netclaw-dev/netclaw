// -----------------------------------------------------------------------
// <copyright file="SearXngBackendTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Search;
using Xunit;

namespace Netclaw.Search.Tests;

public class SearXngBackendTests
{
    [Fact]
    public void ParseResults_extracts_results_from_fixture()
    {
        var json = LoadFixture("searxng-akka-dotnet.json");
        var results = SearXngBackend.ParseResults(json, 30);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void ParseResults_extracts_titles_and_urls()
    {
        var json = LoadFixture("searxng-akka-dotnet.json");
        var results = SearXngBackend.ParseResults(json, 30);

        var first = results[0];
        Assert.Contains("akka.net", first.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("akkadotnet", first.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseResults_extracts_content_as_snippet()
    {
        var json = LoadFixture("searxng-akka-dotnet.json");
        var results = SearXngBackend.ParseResults(json, 30);

        var first = results[0];
        Assert.NotEmpty(first.Snippet);
        Assert.Contains("Akka", first.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseResults_respects_max_results()
    {
        var json = LoadFixture("searxng-akka-dotnet.json");
        var results = SearXngBackend.ParseResults(json, 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void ParseResults_returns_empty_for_missing_results_array()
    {
        var json = """{"query":"test","number_of_results":0}""";
        var results = SearXngBackend.ParseResults(json, 10);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseResults_returns_empty_for_empty_results()
    {
        var json = """{"query":"test","number_of_results":0,"results":[]}""";
        var results = SearXngBackend.ParseResults(json, 10);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseResults_skips_entries_missing_url()
    {
        var json = """
        {
          "results": [
            {"title": "No URL", "content": "Missing url field"},
            {"title": "Has URL", "url": "https://example.com", "content": "Valid"}
          ]
        }
        """;
        var results = SearXngBackend.ParseResults(json, 10);

        Assert.Single(results);
        Assert.Equal("https://example.com", results[0].Url);
    }

    private static string LoadFixture(string filename)
    {
        var assembly = typeof(SearXngBackendTests).Assembly;
        var resourceName = $"Netclaw.Search.Tests.Fixtures.{filename}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Fixture not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
