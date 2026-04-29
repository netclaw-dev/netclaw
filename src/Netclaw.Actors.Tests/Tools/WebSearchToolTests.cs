// -----------------------------------------------------------------------
// <copyright file="WebSearchToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Search;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class WebSearchToolTests
{
    [Fact]
    public async Task ExecuteAsync_returns_formatted_results_from_backend()
    {
        var backend = new FakeSearchBackend(new SearchBackendResult.Success(
        [
            new SearchResult("Akka.NET", "https://getakka.net", "Actor framework for .NET"),
            new SearchResult("GitHub", "https://github.com/akkadotnet/akka.net", "Source code"),
        ]));

        var tool = new WebSearchTool(backend);
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "akka.net" }, TestContext.Current.CancellationToken);

        Assert.Contains("Akka.NET", result);
        Assert.Contains("https://getakka.net", result);
        Assert.Contains("Actor framework", result);
    }

    [Fact]
    public async Task ExecuteAsync_returns_error_from_backend()
    {
        var backend = new FakeSearchBackend(
            new SearchBackendResult.Error("Bot detection triggered"));

        var tool = new WebSearchTool(backend);
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "test" }, TestContext.Current.CancellationToken);

        Assert.Contains("Error:", result);
        Assert.Contains("Bot detection triggered", result);
    }

    [Fact]
    public async Task ExecuteAsync_returns_no_results_message()
    {
        var backend = new FakeSearchBackend(
            new SearchBackendResult.Success([]));

        var tool = new WebSearchTool(backend);
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "xyzzy" }, TestContext.Current.CancellationToken);

        Assert.Contains("No results found", result);
    }

    private sealed class FakeSearchBackend(SearchBackendResult result) : ISearchBackend
    {
        public Task<SearchBackendResult> SearchAsync(string query, int maxResults, CancellationToken ct)
            => Task.FromResult(result);
    }
}
