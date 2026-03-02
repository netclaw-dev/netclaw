using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

/// <summary>
/// Meta-tool that wraps the Memorizer MCP server's <c>search_memories</c> tool.
/// Resolves the underlying MCP tool at call time via <see cref="ToolRegistry"/>.
/// Returns graceful error if Memorizer is not connected.
/// </summary>
[NetclawTool("search_memories",
    "Search cross-session memory for prior knowledge, saved context, and project information. "
    + "Returns matching memories ranked by relevance.",
    Grant = "builtin")]
public sealed partial class SearchMemoriesTool : NetclawTool<SearchMemoriesTool.Params>
{
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger _logger;

    /// <summary>
    /// Known MCP tool names for Memorizer's search. Checked in order.
    /// </summary>
    private static readonly string[] MemorizerToolNames =
    [
        "memorizer/search_memories",
        "memorizer/search"
    ];

    public record Params(
        [property: Description("Search query to find relevant memories")]
        string Query,
        [property: Description("Maximum number of results to return (default 5)")]
        int? Limit = null);

    public SearchMemoriesTool(ToolRegistry toolRegistry, ILogger<SearchMemoriesTool>? logger = null)
    {
        _toolRegistry = toolRegistry;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var mcpTool = FindMemorizerTool();
        if (mcpTool is null)
        {
            _logger.LogWarning("Memory search requested but Memorizer MCP is not connected");
            return "Memory store is not available. Memorizer MCP server is not connected. "
                   + "Check McpServers configuration in netclaw.json.";
        }

        var arguments = new Dictionary<string, object?>
        {
            ["query"] = args.Query
        };

        if (args.Limit is > 0)
            arguments["limit"] = args.Limit.Value;

        try
        {
            var result = await mcpTool.ExecuteAsync(arguments, ct);
            var formatted = FormatResult(result);

            _logger.LogInformation(
                "Memory search: query='{Query}', result={ResultLength} chars",
                args.Query, formatted.Length);

            return formatted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory search failed: query='{Query}'", args.Query);
            return $"Error searching memories: {ex.Message}";
        }
    }

    private INetclawTool? FindMemorizerTool()
    {
        foreach (var name in MemorizerToolNames)
        {
            var tool = _toolRegistry.GetByName(name);
            if (tool is not null)
                return tool;
        }

        return null;
    }

    private static string FormatResult(string rawResult)
    {
        if (string.IsNullOrWhiteSpace(rawResult))
            return "No memories found.";

        // If the result is already readable text, return as-is
        if (!rawResult.TrimStart().StartsWith('{') && !rawResult.TrimStart().StartsWith('['))
            return rawResult;

        // Try to parse and format JSON for readability
        try
        {
            using var doc = JsonDocument.Parse(rawResult);
            var sb = new StringBuilder();

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var count = 0;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    count++;
                    if (item.TryGetProperty("title", out var title))
                        sb.AppendLine($"━━━ {title.GetString()} ━━━");

                    if (item.TryGetProperty("text", out var text))
                        sb.AppendLine(text.GetString());
                    else if (item.TryGetProperty("content", out var content))
                        sb.AppendLine(content.GetString());

                    sb.AppendLine();
                }

                if (count == 0)
                    return "No memories found.";

                return sb.ToString().TrimEnd();
            }

            return rawResult;
        }
        catch (JsonException)
        {
            return rawResult;
        }
    }
}
