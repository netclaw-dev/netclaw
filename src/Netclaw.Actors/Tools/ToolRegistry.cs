// -----------------------------------------------------------------------
// <copyright file="ToolRegistry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Registration entry pairing a tool with its ACL grant category.
/// </summary>
public sealed record ToolRegistration(INetclawTool Tool, string GrantCategory);

/// <summary>
/// Summary metadata for an MCP server used in progressive-disclosure routing.
/// </summary>
public sealed record McpServerSummary(string ServerName, string Description, int ToolCount);

/// <summary>
/// Registers <see cref="INetclawTool"/> definitions with grant categories for policy filtering.
/// Sessions receive only tools whose grant category is in the session's allowed set.
/// </summary>
public sealed class ToolRegistry
{
    private readonly List<ToolRegistration> _tools = [];

    public void Register(INetclawTool tool)
    {
        _tools.Add(new ToolRegistration(tool, tool.GrantCategory));
    }

    public void Replace(INetclawTool tool)
    {
        _tools.RemoveAll(t => string.Equals(t.Tool.Name, tool.Name, StringComparison.Ordinal));
        Register(tool);
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

    /// <summary>Find a tool by name for dispatch. Accepts either the
    /// canonical name (<c>server/tool</c> for MCP) or the LLM-facing
    /// sanitized alias (<c>server__tool</c>).</summary>
    public INetclawTool? GetByName(string name) =>
        FindRegistration(name)?.Tool;

    public ToolRegistration? GetRegistrationByToolName(string name) =>
        FindRegistration(name);

    /// <summary>
    /// Map a canonical tool name to the LLM-facing alias the registered
    /// tool exposes. Returns the input unchanged for first-party tools
    /// (where the canonical form is already LLM-safe) and for names
    /// that don't resolve to a registered tool. Called at the outbound
    /// LLM-request boundary to translate canonical names stored in
    /// conversation history back to what the model expects on the
    /// wire.
    /// </summary>
    public string ToLlmFacingName(string canonicalName) =>
        FindRegistration(canonicalName)?.Tool.LlmFacingName.Value ?? canonicalName;

    /// <summary>
    /// Map either a canonical or LLM-facing tool name to the canonical
    /// form. Returns the input unchanged for names that don't resolve
    /// to a registered tool. Used at the inbound LLM-response boundary
    /// to normalize <see cref="Microsoft.Extensions.AI.FunctionCallContent.Name"/>
    /// so internal consumers (audit, approvals, events) always see the
    /// operator-facing identifier.
    /// </summary>
    public string ToCanonicalName(string name) =>
        FindRegistration(name)?.Tool.Name ?? name;

    private ToolRegistration? FindRegistration(string name)
    {
        var direct = _tools.FirstOrDefault(t => t.Tool.Name == name);
        if (direct is not null)
            return direct;
        return _tools.FirstOrDefault(t =>
            t.Tool is McpToolAdapter mcp
            && string.Equals(mcp.LlmFacingName.Value, name, StringComparison.Ordinal));
    }

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
    /// Returns tools discovered from one MCP server.
    /// </summary>
    public IReadOnlyList<INetclawTool> GetToolsForServer(McpServerName serverName, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(serverName.Value) || maxResults <= 0)
            return [];

        return _tools
            .Where(t => t.Tool is McpToolAdapter mcp
                        && string.Equals(mcp.ServerName, serverName.Value, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Tool)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Returns MCP server summaries for progressive-disclosure discovery.
    /// </summary>
    public IReadOnlyList<McpServerSummary> GetMcpServerSummaries()
    {
        return _tools
            .Select(t => t.Tool)
            .OfType<McpToolAdapter>()
            .GroupBy(t => t.ServerName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var tools = group.ToList();
                var serverName = tools[0].ServerName;
                var description = DescribeServerCapability(serverName, tools);
                return new McpServerSummary(serverName, description, tools.Count);
            })
            .OrderBy(x => x.ServerName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Search tools by keyword, matching against name and description.
    /// Returns up to <paramref name="maxResults"/> matching tools.
    /// </summary>
    public IReadOnlyList<INetclawTool> SearchTools(string query, McpServerName? serverFilter, int maxResults)
    {
        var queryParts = TokenizeQuery(query);

        if (queryParts.Count == 0)
            return [];

        return _tools
            .Where(t =>
            {
                // Apply server filter if specified
                if (serverFilter is not null && t.Tool is McpToolAdapter mcp)
                {
                    if (!string.Equals(mcp.ServerName, serverFilter.Value.Value, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else if (serverFilter is not null)
                {
                    return false; // non-MCP tools filtered out when server filter is set
                }

                var nameLower = t.Tool.Name.ToLowerInvariant();
                var descLower = t.Tool.Description.ToLowerInvariant();

                return queryParts.Any(p =>
                    nameLower.Contains(p, StringComparison.Ordinal)
                    || descLower.Contains(p, StringComparison.Ordinal));
            })
            .Take(maxResults)
            .Select(t => t.Tool)
            .ToList();
    }

    /// <summary>
    /// Returns fuzzy suggestions when direct keyword matching returns no tools.
    /// </summary>
    public IReadOnlyList<INetclawTool> SuggestTools(string query, McpServerName? serverFilter, int maxResults)
    {
        var normalizedQuery = NormalizeSearchText(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return [];

        var candidates = _tools
            .Where(t => PassesServerFilter(t.Tool, serverFilter))
            .Select(t => t.Tool)
            .ToList();

        var suggestions = candidates
            .Select(tool => new
            {
                Tool = tool,
                Score = ComputeSuggestionScore(normalizedQuery, tool)
            })
            .Where(x => x.Score >= 0.40)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Tool.Name, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(x => x.Tool)
            .ToList();

        return suggestions;
    }

    /// <summary>
    /// Generates a compressed tool index for the system prompt.
    /// Uses progressive disclosure: list directly-callable tools and MCP servers,
    /// then route detailed discovery through search_tools.
    /// </summary>
    public string GenerateCompressedIndex()
    {
        if (_tools.Count == 0)
            return string.Empty;

        return BuildCompressedIndex(_tools);
    }

    /// <summary>
    /// Generates the compressed tool index filtered to the tools discoverable by the
    /// supplied audience and feature gates.
    /// </summary>
    public string GenerateCompressedIndex(TrustAudience audience, ToolAccessPolicy policy)
    {
        var visible = _tools
            .Where(t => policy.IsToolExposed(t.Tool, CreateContext(audience)))
            .ToList();

        return BuildCompressedIndex(visible);
    }

    private static string BuildCompressedIndex(IReadOnlyList<ToolRegistration> registrations)
    {
        if (registrations.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        // Separate always-loaded (directly callable) tools from MCP (dynamic) tools
        var builtinTools = registrations.Where(t => t.Tool is not McpToolAdapter).ToList();
        var mcpTools = registrations.Where(t => t.Tool is McpToolAdapter).ToList();

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
            if (sb.Length > 0)
                sb.AppendLine();

            sb.AppendLine("[MCP capability servers - discover tools with search_tools]");

            foreach (var summary in GetMcpServerSummaries(mcpTools))
            {
                sb.AppendLine($"{summary.ServerName} ({summary.ToolCount} tools): {summary.Description}");
            }

            sb.AppendLine("- Choose a server by need (browser, memory, email, etc.).");
            sb.AppendLine("- Then call search_tools(query: \"<intent>\", server: \"<server_name>\").");
            sb.AppendLine("- To browse one server, call search_tools(query: \"all\", server: \"<server_name>\").");
            sb.AppendLine("- MCP tools are not directly callable until loaded via search_tools.");
        }

        return sb.ToString();
    }

    private static IReadOnlyList<McpServerSummary> GetMcpServerSummaries(IReadOnlyList<ToolRegistration> registrations)
    {
        return registrations
            .Select(t => t.Tool)
            .OfType<McpToolAdapter>()
            .GroupBy(t => t.ServerName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var tools = group.ToList();
                var serverName = tools[0].ServerName;
                var description = DescribeServerCapability(serverName, tools);
                return new McpServerSummary(serverName, description, tools.Count);
            })
            .OrderBy(x => x.ServerName, StringComparer.Ordinal)
            .ToList();
    }

    private static ToolExecutionContext CreateContext(TrustAudience audience)
        => new(null, null) { Audience = audience };

    private static string DescribeServerCapability(string serverName, IReadOnlyList<McpToolAdapter> tools)
    {
        var normalized = serverName.Trim().ToLowerInvariant();

        if (normalized.Contains("browser", StringComparison.Ordinal))
            return "Interactive browser automation for navigation, clicking, typing, and page snapshots.";

        if (normalized.Contains("memorizer", StringComparison.Ordinal)
            || normalized.Contains("memory", StringComparison.Ordinal))
            return "Persistent memory storage and retrieval across sessions.";

        if (normalized.Contains("email", StringComparison.Ordinal)
            || normalized.Contains("mail", StringComparison.Ordinal))
            return "Email operations for sending, reading, and inbox workflows.";

        if (normalized.Contains("github", StringComparison.Ordinal)
            || normalized.Contains("git", StringComparison.Ordinal))
            return "GitHub operations for repositories, issues, and pull requests.";

        if (normalized.Contains("search", StringComparison.Ordinal))
            return "Search and retrieval capabilities.";

        var fallback = tools
            .Select(t => t.Description)
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));

        if (string.IsNullOrWhiteSpace(fallback))
            return "General MCP capability server.";

        var singleLine = Regex.Replace(fallback.Trim(), "\\s+", " ");
        return singleLine.Length <= 96 ? singleLine : singleLine[..93] + "...";
    }

    private static IReadOnlyList<string> TokenizeQuery(string query)
    {
        var normalized = NormalizeSearchText(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private static bool PassesServerFilter(INetclawTool tool, McpServerName? serverFilter)
    {
        if (serverFilter is null)
            return true;

        if (tool is not McpToolAdapter mcp)
            return false;

        return string.Equals(mcp.ServerName, serverFilter.Value.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSearchText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var value = text.Trim().ToLowerInvariant();
        value = value.Replace("mcp:", string.Empty, StringComparison.OrdinalIgnoreCase);
        value = Regex.Replace(value, "[^a-z0-9_]+", " ");
        return Regex.Replace(value, "\\s+", " ").Trim();
    }

    private static double Similarity(string source, string target)
    {
        if (source.Length == 0 || target.Length == 0)
            return 0;

        if (string.Equals(source, target, StringComparison.Ordinal))
            return 1.0;

        var distance = LevenshteinDistance(source, target);
        var maxLen = Math.Max(source.Length, target.Length);
        return 1.0 - (double)distance / maxLen;
    }

    private static double ComputeSuggestionScore(string normalizedQuery, INetclawTool tool)
    {
        var fullName = NormalizeSearchText(tool.Name);

        var shortName = fullName;
        var slashIndex = tool.Name.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex + 1 < tool.Name.Length)
        {
            shortName = NormalizeSearchText(tool.Name[(slashIndex + 1)..]);
        }

        var description = NormalizeSearchText(tool.Description);

        return Math.Max(
            Similarity(normalizedQuery, fullName),
            Math.Max(
                Similarity(normalizedQuery, shortName),
                Similarity(normalizedQuery, description)));
    }

    private static int LevenshteinDistance(string source, string target)
    {
        var rows = source.Length + 1;
        var cols = target.Length + 1;
        var d = new int[rows, cols];

        for (var i = 0; i < rows; i++) d[i, 0] = i;
        for (var j = 0; j < cols; j++) d[0, j] = j;

        for (var i = 1; i < rows; i++)
        {
            for (var j = 1; j < cols; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[rows - 1, cols - 1];
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
            // Bare AITool adapters are only used for first-party / test
            // fakes whose names already satisfy the Anthropic regex —
            // FromCanonical round-trips them unchanged and asserts that
            // assumption at construction.
            LlmFacingName = LlmFacingToolName.FromCanonical(Name);
            Description = tool is AIFunction fn ? (fn.Description ?? "") : "";
            ParameterSchema = default;
        }

        public string Name { get; }
        public LlmFacingToolName LlmFacingName { get; }
        public string Description { get; }
        public string GrantCategory { get; }
        public System.Text.Json.JsonElement ParameterSchema { get; }
        public AITool ToAITool() => _tool;

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => Task.FromResult("Not supported via adapter");
    }
}
