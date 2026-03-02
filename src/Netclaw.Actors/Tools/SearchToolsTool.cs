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
        var serverFilter = NormalizeServerFilter(args.Server);
        var results = _registry.SearchTools(query, serverFilter, MaxResults);

        if (results.Count == 0)
        {
            var suggestions = _registry.SuggestTools(query, serverFilter, MaxResults);
            if (suggestions.Count == 0)
                return Task.FromResult($"No tools found matching '{query}'.");

            var suggestionBuilder = new StringBuilder();
            suggestionBuilder.AppendLine($"No exact tools found matching '{query}'.");
            suggestionBuilder.AppendLine();
            suggestionBuilder.AppendLine("Did you mean:");
            suggestionBuilder.AppendLine();

            foreach (var tool in suggestions)
            {
                var desc = tool.Description.Length > 80
                    ? tool.Description[..77] + "..."
                    : tool.Description;
                var parameterHint = GetParameterHint(tool);

                // NOTE: Keep suggestions in a distinct format so LlmSessionActor doesn't
                // auto-load them as discovered tools. Auto-loading is only for exact matches.
                suggestionBuilder.AppendLine($"  ? {tool.Name} :: {desc}{parameterHint}");
            }

            suggestionBuilder.AppendLine();
            suggestionBuilder.AppendLine(
                "Suggestions are not loaded yet. Call search_tools again with one of the exact tool names above.");
            return Task.FromResult(suggestionBuilder.ToString());
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} tool(s):");
        sb.AppendLine();

        foreach (var tool in results)
        {
            var desc = tool.Description.Length > 80
                ? tool.Description[..77] + "..."
                : tool.Description;
            var parameterHint = GetParameterHint(tool);
            sb.AppendLine($"  {tool.Name} — {desc}{parameterHint}");
        }

        sb.AppendLine();
        sb.AppendLine("Call any tool above by its full name. Tools are now loaded and available.");
        return Task.FromResult(sb.ToString());
    }

    private static string? NormalizeServerFilter(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
            return null;

        var value = server.Trim();
        if (value.Equals("default", StringComparison.OrdinalIgnoreCase)
            || value.Equals("all", StringComparison.OrdinalIgnoreCase)
            || value.Equals("any", StringComparison.OrdinalIgnoreCase)
            || value == "*")
        {
            return null;
        }

        if (value.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase))
            value = value[4..];

        return value;
    }

    private static string GetParameterHint(INetclawTool tool)
    {
        if (tool.ParameterSchema.ValueKind != System.Text.Json.JsonValueKind.Object
            || !tool.ParameterSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return string.Empty;
        }

        var names = properties.EnumerateObject().Select(p => p.Name).ToList();
        if (names.Count == 0)
            return string.Empty;

        const int maxShown = 4;
        var shown = names.Take(maxShown).ToList();
        if (names.Count > maxShown)
        {
            shown.Add($"+{names.Count - maxShown} more");
        }

        return $" (params: {string.Join(", ", shown)})";
    }
}
