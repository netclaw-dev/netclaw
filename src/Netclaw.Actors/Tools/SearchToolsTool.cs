using System.ComponentModel;
using System.Text;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Meta-tool that searches the <see cref="ToolRegistry"/> for available tools.
/// Returns compressed summaries of matching tools so the LLM can decide which
/// to load dynamically. Used as part of the two-layer discovery architecture.
/// </summary>
[NetclawTool("search_tools",
    "Search for available tools by keyword. Returns tool names, descriptions, and parameter names. "
    + "Use this to discover tools before calling them.",
    Grant = "builtin")]
public sealed partial class SearchToolsTool : NetclawTool<SearchToolsTool.Params>
{
    private readonly ToolRegistry _registry;
    private const int MaxResults = 10;

    public record Params(
        [property: Description("Search query to match against tool names and descriptions")]
        string Query,
        [property: Description("Optional MCP server name to filter results (e.g., 'memorizer')")]
        string? Server = null);

    public SearchToolsTool(ToolRegistry registry)
    {
        _registry = registry;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var query = args.Query.Trim();
        var results = _registry.SearchTools(query, args.Server, MaxResults);

        if (results.Count == 0)
            return Task.FromResult($"No tools found matching '{query}'.");

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} tool(s):");
        sb.AppendLine();

        foreach (var tool in results)
        {
            var desc = tool.Description.Length > 80
                ? tool.Description[..77] + "..."
                : tool.Description;
            sb.AppendLine($"  {tool.Name} — {desc}");
        }

        sb.AppendLine();
        sb.AppendLine("Call any tool above by its full name. Tools are now loaded and available.");
        return Task.FromResult(sb.ToString());
    }
}
