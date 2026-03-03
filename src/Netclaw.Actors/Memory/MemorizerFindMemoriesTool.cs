using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

/// <summary>
/// Memory search tool that delegates directly to <c>memorizer/search_memories</c> MCP.
/// Returns lightweight results with IDs, titles, and similarity scores.
///
/// Registered when <c>Memory.Provider = "memorizer"</c>.
/// </summary>
[NetclawTool("find_memories",
    "Search cross-session memory for prior knowledge. Returns lightweight results (ID, title, score). "
    + "Use get_memories(ids) to load full content for selected results.",
    Grant = "builtin")]
public sealed partial class MemorizerFindMemoriesTool : NetclawTool<MemorizerFindMemoriesTool.Params>
{
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger _logger;

    private const string MemorizerSearchToolName = "memorizer/search_memories";

    public record Params(
        [property: Description("Search query to find relevant memories")]
        string Query,
        [property: Description("Maximum number of results to return (default 5)")]
        int? Limit = null,
        [property: Description("Optional comma-separated tags to filter results (e.g. \"reference, how-to\")")]
        string? Tags = null);

    public MemorizerFindMemoriesTool(
        ToolRegistry toolRegistry,
        ILogger<MemorizerFindMemoriesTool>? logger = null)
    {
        _toolRegistry = toolRegistry;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var searchTool = _toolRegistry.GetByName(MemorizerSearchToolName);
        if (searchTool is null)
        {
            _logger.LogWarning("Memorizer search tool not available");
            return "Memory search unavailable: Memorizer MCP server not connected.";
        }

        var arguments = new Dictionary<string, object?> { ["query"] = args.Query };
        if (args.Limit is > 0)
            arguments["limit"] = args.Limit;
        if (!string.IsNullOrWhiteSpace(args.Tags))
        {
            var tags = args.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tags.Length > 0)
                arguments["filterTags"] = tags;
        }

        try
        {
            var result = await searchTool.ExecuteAsync(arguments, ct);
            _logger.LogInformation("Memory find completed: query='{Query}', result={ResultLength} chars",
                args.Query, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory find error: query='{Query}'", args.Query);
            return $"Error searching memories: {ex.Message}";
        }
    }
}
