// -----------------------------------------------------------------------
// <copyright file="WebSearchTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Netclaw.Search;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Searches the web and returns results as text.
/// Delegates to a configured <see cref="ISearchBackend"/> for the actual search.
/// </summary>
[NetclawTool("web_search",
    "Search the web and return a list of results with titles, URLs, and snippets",
    Grant = "web")]
public sealed partial class WebSearchTool : NetclawTool<WebSearchTool.Params>
{
    private const int DefaultMaxResults = 10;

    private readonly ISearchBackend _backend;

    public record Params(
        [property: Description("The search query")] string Query,
        [property: Description("Maximum number of results to return (default 10, max 30)")] int? MaxResults = null);

    public WebSearchTool(ISearchBackend backend)
    {
        _backend = backend;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Query))
            return "Error: 'query' parameter is required.";

        var maxResults = Math.Clamp(args.MaxResults ?? DefaultMaxResults, 1, 30);

        var result = await _backend.SearchAsync(args.Query, maxResults, ct);

        return result switch
        {
            SearchBackendResult.Success success when success.Results.Count == 0
                => $"No results found for: {args.Query}",
            SearchBackendResult.Success success
                => FormatResults(success.Results, args.Query),
            SearchBackendResult.Error error
                => $"Error: {error.Message}",
            _ => "Error: Unexpected search backend result."
        };
    }

    private static string FormatResults(IReadOnlyList<SearchResult> results, string query)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Search results for: {query}");
        sb.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.AppendLine($"{i + 1}. {r.Title}");
            sb.AppendLine($"   URL: {r.Url}");
            if (!string.IsNullOrEmpty(r.Snippet))
                sb.AppendLine($"   {r.Snippet}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
