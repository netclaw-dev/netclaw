// -----------------------------------------------------------------------
// <copyright file="SearchToolsTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Meta-tool that searches the <see cref="ToolRegistry"/> for available tools.
/// Returns compressed summaries of matching tools so the LLM can decide which
/// deferred first-party or MCP tools to load dynamically.
/// </summary>
[NetclawTool("search_tools",
    "Search for available tools by keyword. Returns tool names and descriptions — tools are NOT loaded until you call load_tool. "
    + "Use query='servers' to list MCP servers and query='all' with a server filter to browse one server.",
    Grant = "builtin")]
public sealed partial class SearchToolsTool : NetclawTool<SearchToolsTool.Params>
{
    private readonly ToolRegistry _registry;
    private readonly ToolAccessPolicy _policy;
    private const int MaxSearchResults = 10;
    private const int MaxServerBrowseResults = 30;

    public record Params(
        [property: Description("Search query for tools. Use 'servers' to list MCP servers. Use 'all' with server filter to list all tools in one server.")]
        string Query,
        [property: Description("Optional MCP server name to filter results (e.g., 'memorizer')")]
        string? Server = null);

    public SearchToolsTool(ToolRegistry registry, ToolAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(policy);

        _registry = registry;
        _policy = policy;
    }

    protected override Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        var query = args.Query.Trim();
        var serverFilter = NormalizeServerFilter(args.Server);

        if (IsServerCatalogQuery(query))
            return Task.FromResult(BuildServerCatalog(context));

        if (IsListAllQuery(query))
        {
            if (serverFilter is null)
            {
                return Task.FromResult(BuildServerCatalog(
                    context,
                    "To browse tools, call search_tools(query: \"all\", server: \"<server_name>\")."));
            }

            var serverTools = FilterVisible(
                    _registry.GetToolsForServer(serverFilter.Value, int.MaxValue),
                    context)
                .Take(MaxServerBrowseResults)
                .ToList();
            if (serverTools.Count == 0)
            {
                return Task.FromResult($"No tools found for server '{serverFilter.Value}' in the current trust context.");
            }

            return Task.FromResult(BuildToolList(
                serverTools,
                $"Found {serverTools.Count} tool(s) in server '{serverFilter.Value}':"));
        }

        var results = FilterVisible(_registry.SearchTools(query, serverFilter, int.MaxValue), context)
            .Take(MaxSearchResults)
            .ToList();

        if (results.Count == 0)
        {
            var suggestions = FilterVisible(_registry.SuggestTools(query, serverFilter, int.MaxValue), context)
                .Take(MaxSearchResults)
                .ToList();
            if (suggestions.Count == 0)
            {
                if (serverFilter is null)
                    return Task.FromResult($"No tools found matching '{query}'. Try search_tools(query: \"servers\") to browse MCP capabilities.");

                return Task.FromResult($"No tools found matching '{query}' in server '{serverFilter.Value}'. Try search_tools(query: \"all\", server: \"{serverFilter.Value}\") to browse tools.");
            }

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
                // Emit the LLM-facing alias so what the model reads here
                // matches what it must emit in a tool_use call.
                suggestionBuilder.AppendLine($"  ? {tool.LlmFacingName} :: {desc}{parameterHint}");
            }

            suggestionBuilder.AppendLine();
            suggestionBuilder.AppendLine(
                "Suggestions are not loaded yet. Call search_tools again with one of the exact tool names above.");
            return Task.FromResult(suggestionBuilder.ToString());
        }

        return Task.FromResult(BuildToolList(results, $"Found {results.Count} tool(s):"));
    }

    private string BuildServerCatalog(ToolInvocationContext context, string? trailingHint = null)
    {
        var summaries = _registry.GetMcpServerSummaries()
            .Select(summary =>
            {
                var visibleTools = FilterVisible(
                    _registry.GetToolsForServer(new McpServerName(summary.ServerName), int.MaxValue),
                    context);
                return visibleTools.Count == 0
                    ? null
                    : ToolRegistry.SummarizeMcpServer(summary.ServerName, visibleTools);
            })
            .OfType<McpServerSummary>()
            .ToList();
        if (summaries.Count == 0)
            return "No MCP servers are currently registered.";

        var sb = new StringBuilder();
        sb.AppendLine($"Available MCP servers ({summaries.Count}):");
        sb.AppendLine();

        foreach (var summary in summaries)
        {
            sb.AppendLine($"  {summary.ServerName} ({summary.ToolCount} tools): {summary.Description}");
        }

        sb.AppendLine();
        sb.AppendLine("To browse one server, call search_tools(query: \"all\", server: \"<server_name>\").");
        sb.AppendLine("To find tools, call search_tools(query: \"<intent>\", server: \"<server_name>\"), then load_tool(\"<name>\") to activate.");
        if (!string.IsNullOrWhiteSpace(trailingHint))
        {
            sb.AppendLine();
            sb.AppendLine(trailingHint);
        }

        return sb.ToString();
    }

    private IReadOnlyList<INetclawTool> FilterVisible(IReadOnlyList<INetclawTool> tools, ToolInvocationContext context)
        => _policy.FilterDiscoverableTools(tools, context);

    private static string BuildToolList(IReadOnlyList<INetclawTool> tools, string heading)
    {
        var sb = new StringBuilder();
        sb.AppendLine(heading);
        sb.AppendLine();

        foreach (var tool in tools)
        {
            var desc = tool.Description.Length > 80
                ? tool.Description[..77] + "..."
                : tool.Description;
            var parameterHint = GetParameterHint(tool);
            sb.AppendLine($"  {tool.LlmFacingName} — {desc}{parameterHint}");
        }

        sb.AppendLine();
        sb.AppendLine("To use a tool, call load_tool(\"<tool_name>\") to activate it first.");
        return sb.ToString();
    }

    private static bool IsServerCatalogQuery(string query)
    {
        var normalized = NormalizeControlQuery(query);
        return normalized is "" or "server" or "servers" or "mcp" or "mcp server" or "mcp servers" or "capabilities";
    }

    private static bool IsListAllQuery(string query)
    {
        var normalized = NormalizeControlQuery(query);
        return normalized is "all" or "list" or "list all" or "*";
    }

    private static string NormalizeControlQuery(string query)
    {
        var trimmed = query.Trim();
        if (trimmed == "*")
            return "*";

        if (trimmed.Length == 0)
            return string.Empty;

        return string.Join(' ', trimmed
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static McpServerName? NormalizeServerFilter(string? server)
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

        return new McpServerName(value);
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
