using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

/// <summary>
/// File-backed memory search tool. Searches local markdown memory files
/// by substring matching against title, tags, and content.
/// Only registered when Memory.Provider = "files".
/// When Memorizer is configured, <c>memorizer/search_memories</c> is available
/// directly through MCP discovery — no builtin wrapper needed.
/// </summary>
[NetclawTool("search_memories",
    "Search cross-session memory for prior knowledge, saved context, and project information. "
    + "Returns matching memories ranked by relevance.",
    Grant = "builtin")]
public sealed partial class SearchMemoriesTool : NetclawTool<SearchMemoriesTool.Params>
{
    private readonly FileMemoryStore _store;
    private readonly ILogger _logger;

    public record Params(
        [property: Description("Search query to find relevant memories")]
        string Query,
        [property: Description("Maximum number of results to return (default 5)")]
        int? Limit = null);

    public SearchMemoriesTool(FileMemoryStore store, ILogger<SearchMemoriesTool>? logger = null)
    {
        _store = store;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var limit = args.Limit is > 0 ? args.Limit.Value : 5;

        try
        {
            var results = await _store.SearchAsync(args.Query, limit, ct);

            if (results.Count == 0)
            {
                _logger.LogInformation("Memory search: query='{Query}', no results", args.Query);
                return "No memories found.";
            }

            var sb = new StringBuilder();
            foreach (var result in results)
            {
                sb.AppendLine($"━━━ {result.Title} ━━━");
                if (result.Tags.Length > 0)
                    sb.AppendLine($"Tags: {string.Join(", ", result.Tags)}");
                sb.AppendLine(result.Content);
                sb.AppendLine();
            }

            var formatted = sb.ToString().TrimEnd();
            _logger.LogInformation(
                "Memory search: query='{Query}', results={Count}, chars={Chars}",
                args.Query, results.Count, formatted.Length);

            return formatted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory search failed: query='{Query}'", args.Query);
            return $"Error searching memories: {ex.Message}";
        }
    }
}
