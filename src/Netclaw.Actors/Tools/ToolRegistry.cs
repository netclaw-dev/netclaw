using System.Text;
using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Registration entry pairing a tool with its ACL grant category.
/// </summary>
public sealed record ToolRegistration(INetclawTool Tool, string GrantCategory);

/// <summary>
/// Registers <see cref="INetclawTool"/> definitions with grant categories for policy filtering.
/// Sessions receive only tools whose grant category is in the session's allowed set.
/// </summary>
public sealed class ToolRegistry
{
    private readonly List<ToolRegistration> _tools = new();

    public void Register(INetclawTool tool)
    {
        _tools.Add(new ToolRegistration(tool, tool.GrantCategory));
    }

    /// <summary>
    /// Register an <see cref="AITool"/> directly (for test fakes that don't implement INetclawTool).
    /// </summary>
    public void Register(AITool tool, string grantCategory)
    {
        _tools.Add(new ToolRegistration(new AIToolAdapter(tool, grantCategory), grantCategory));
    }

    /// <summary>All registered tools as AITool for ChatOptions.Tools.</summary>
    public IReadOnlyList<AITool> GetAllTools() =>
        _tools.Select(t => t.Tool.ToAITool()).ToList();

    /// <summary>Only tools whose grant category is in the allowed set.</summary>
    public IReadOnlyList<AITool> GetToolsForGrants(IReadOnlySet<string> grantedCategories) =>
        _tools
            .Where(t => grantedCategories.Contains(t.GrantCategory))
            .Select(t => t.Tool.ToAITool())
            .ToList();

    /// <summary>Find a tool by name for dispatch.</summary>
    public INetclawTool? GetByName(string name) =>
        _tools.FirstOrDefault(t => t.Tool.Name == name)?.Tool;

    /// <summary>
    /// Returns tools that should always be loaded into the LLM context.
    /// All non-MCP tools are always loaded; MCP tools use dynamic discovery via search_tools.
    /// </summary>
    public IReadOnlyList<AITool> GetAlwaysLoadedTools() =>
        _tools
            .Where(t => t.Tool is not McpToolAdapter)
            .Select(t => t.Tool.ToAITool())
            .ToList();

    /// <summary>
    /// Returns all registered tool registrations (for search and dynamic loading).
    /// </summary>
    public IReadOnlyList<ToolRegistration> GetAllRegistrations() => _tools;

    /// <summary>
    /// Search tools by keyword, matching against name and description.
    /// Returns up to <paramref name="maxResults"/> matching tools.
    /// </summary>
    public IReadOnlyList<INetclawTool> SearchTools(string query, string? serverFilter, int maxResults)
    {
        var queryLower = query.ToLowerInvariant();
        var queryParts = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return _tools
            .Where(t =>
            {
                // Apply server filter if specified
                if (serverFilter is not null && t.Tool is McpToolAdapter mcp)
                {
                    if (!string.Equals(mcp.ServerName, serverFilter, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else if (serverFilter is not null)
                {
                    return false; // non-MCP tools filtered out when server filter is set
                }

                var nameLower = t.Tool.Name.ToLowerInvariant();
                var descLower = t.Tool.Description.ToLowerInvariant();

                return queryParts.Any(p => nameLower.Contains(p) || descLower.Contains(p));
            })
            .Take(maxResults)
            .Select(t => t.Tool)
            .ToList();
    }

    /// <summary>
    /// Generates a compressed tool index for the system prompt.
    /// Groups tools by grant category for compact representation.
    /// </summary>
    public string GenerateCompressedIndex()
    {
        if (_tools.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        // Separate always-loaded (directly callable) tools from MCP (dynamic) tools
        var builtinTools = _tools.Where(t => t.Tool is not McpToolAdapter).ToList();
        var mcpTools = _tools.Where(t => t.Tool is McpToolAdapter).ToList();

        if (builtinTools.Count > 0)
        {
            sb.AppendLine("[directly callable tools]");
            var builtinGrouped = builtinTools.GroupBy(t => t.GrantCategory);
            foreach (var group in builtinGrouped)
            {
                var names = group.Select(t => t.Tool.Name);
                sb.AppendLine($"{group.Key}: {string.Join(", ", names)}");
            }
        }

        if (mcpTools.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[MCP tools — NOT directly callable, must load first via search_tools]");
            var mcpGrouped = mcpTools.GroupBy(t => t.GrantCategory);
            foreach (var group in mcpGrouped)
            {
                var names = group.Select(t => ((McpToolAdapter)t.Tool).BareToolName);
                sb.AppendLine($"{group.Key}: {string.Join(", ", names)}");
            }
            sb.AppendLine("→ REQUIRED: call search_tools(\"query\") first to load MCP tools, then call them");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Adapter to wrap bare <see cref="AITool"/> instances (e.g. test fakes) as <see cref="INetclawTool"/>.
    /// </summary>
    private sealed class AIToolAdapter : INetclawTool
    {
        private readonly AITool _tool;

        public AIToolAdapter(AITool tool, string grantCategory)
        {
            _tool = tool;
            GrantCategory = grantCategory;
            Name = tool is AIFunction f ? f.Name : tool.GetType().Name;
            Description = tool is AIFunction fn ? (fn.Description ?? "") : "";
            ParameterSchema = default;
        }

        public string Name { get; }
        public string Description { get; }
        public string GrantCategory { get; }
        public System.Text.Json.JsonElement ParameterSchema { get; }
        public AITool ToAITool() => _tool;

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => Task.FromResult("Not supported via adapter");
    }
}
