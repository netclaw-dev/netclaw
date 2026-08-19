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

internal enum ToolExposureTier
{
    Core,
    Deferred
}

/// <summary>
/// Registers <see cref="INetclawTool"/> definitions with grant categories for policy filtering.
/// Sessions receive only tools whose grant category is in the session's allowed set.
/// </summary>
public sealed class ToolRegistry
{
    private sealed record RegistryEntry(ToolRegistration Registration, ToolExposureTier ExposureTier);

    private sealed record RegistrySnapshot(RegistryEntry[] Entries, ToolRegistration[] Registrations)
    {
        public static RegistrySnapshot Empty { get; } = new([], []);

        public static RegistrySnapshot Create(RegistryEntry[] entries) =>
            new(entries, [..entries.Select(static entry => entry.Registration)]);
    }

    // Copy-on-write. Registrations change at startup, at MCP reconnect, and at shutdown.
    // Reads happen on every tool call from every concurrent session, so a reader takes no
    // lock: it reads this field once and works on that array. A writer
    // holds _writeSync, builds a replacement array, and publishes it in one volatile write,
    // so a reader sees either the old set or the new set and never a partial swap.
    private volatile RegistrySnapshot _tools = RegistrySnapshot.Empty;
    private readonly object _writeSync = new();

    public void Register(INetclawTool tool)
        => Register(tool, ToolExposureTier.Deferred);

    /// <summary>
    /// Registers a tool in the small model-visible core set.
    /// </summary>
    public void RegisterCore(INetclawTool tool)
        => Register(tool, ToolExposureTier.Core);

    private void Register(INetclawTool tool, ToolExposureTier exposureTier)
    {
        lock (_writeSync)
        {
            var current = _tools;
            _tools = RegistrySnapshot.Create(
            [
                ..current.Entries,
                new RegistryEntry(new ToolRegistration(tool, tool.GrantCategory), exposureTier)
            ]);
        }
    }

    public void Replace(INetclawTool tool)
        => Replace(tool, ToolExposureTier.Deferred);

    /// <summary>
    /// Replaces a tool and keeps it in the small model-visible core set.
    /// </summary>
    public void ReplaceCore(INetclawTool tool)
        => Replace(tool, ToolExposureTier.Core);

    private void Replace(INetclawTool tool, ToolExposureTier exposureTier)
    {
        lock (_writeSync)
        {
            var current = _tools;
            _tools = RegistrySnapshot.Create(
            [
                ..current.Entries.Where(entry => !string.Equals(
                    entry.Registration.Tool.Name,
                    tool.Name,
                    StringComparison.Ordinal)),
                new RegistryEntry(new ToolRegistration(tool, tool.GrantCategory), exposureTier),
            ]);
        }
    }

    /// <summary>
    /// Replaces every tool of one MCP server in a single publication. Pass an empty list to
    /// remove the server. A reader never sees the server with a partial tool set.
    /// </summary>
    /// <remarks>
    /// The caller owns the order against its own connection state. Publish the connection
    /// first and the tools second when a server comes up, so a tool the model can see is
    /// always dispatchable. Remove the tools first and the connection second when a server
    /// goes down, so the model stops seeing a tool before dispatch loses it.
    /// </remarks>
    public void PublishMcpServerTools(string serverName, IReadOnlyList<McpToolAdapter> tools)
    {
        lock (_writeSync)
        {
            var current = _tools;
            _tools = RegistrySnapshot.Create(
            [
                ..current.Entries.Where(entry => entry.Registration.Tool is not McpToolAdapter mcp
                                    || !string.Equals(mcp.ServerName, serverName, StringComparison.OrdinalIgnoreCase)),
                ..tools.Select(static tool => new RegistryEntry(
                    new ToolRegistration(tool, tool.GrantCategory),
                    ToolExposureTier.Deferred)),
            ]);
        }
    }

    /// <summary>
    /// Register an <see cref="AITool"/> directly (for test fakes that don't implement INetclawTool).
    /// </summary>
    public void Register(AITool tool, string grantCategory)
        => Register(tool, grantCategory, ToolExposureTier.Deferred);

    /// <summary>
    /// Registers a test or framework tool in the small model-visible core set.
    /// </summary>
    public void RegisterCore(AITool tool, string grantCategory)
        => Register(tool, grantCategory, ToolExposureTier.Core);

    private void Register(AITool tool, string grantCategory, ToolExposureTier exposureTier)
    {
        lock (_writeSync)
        {
            var current = _tools;
            _tools = RegistrySnapshot.Create(
            [
                ..current.Entries,
                new RegistryEntry(
                    new ToolRegistration(new AIToolAdapter(tool, grantCategory), grantCategory),
                    exposureTier)
            ]);
        }
    }

    /// <summary>All registered tools as AITool for ChatOptions.Tools.</summary>
    public IReadOnlyList<AITool> GetAllTools() =>
        GetRegistrationsSnapshot().Select(t => t.Tool.ToAITool()).ToList();

    /// <summary>Only tools whose grant category is in the allowed set.</summary>
    public IReadOnlyList<AITool> GetToolsForGrants(IReadOnlySet<string> grantedCategories) =>
        GetRegistrationsSnapshot()
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
        // One volatile read. Both passes below scan the same array, so a concurrent
        // publication cannot make the canonical pass and the alias pass disagree.
        var tools = _tools.Entries;
        var direct = tools.FirstOrDefault(entry => entry.Registration.Tool.Name == name);
        if (direct is not null)
            return direct.Registration;
        return tools.FirstOrDefault(entry =>
            entry.Registration.Tool is McpToolAdapter mcp
            && string.Equals(mcp.LlmFacingName.Value, name, StringComparison.Ordinal))?.Registration;
    }

    /// <summary>
    /// Returns the small set of tools that a session can expose before discovery.
    /// Invocation policy still filters this set for each current trust context.
    /// </summary>
    public IReadOnlyList<AITool> GetCoreTools() =>
        GetEntriesSnapshot()
            .Where(static entry => entry.ExposureTier == ToolExposureTier.Core)
            .Select(static entry => entry.Registration.Tool.ToAITool())
            .ToList();

    /// <summary>
    /// Returns tools that should always be loaded into the LLM context.
    /// All non-MCP tools are always loaded; MCP tools use dynamic discovery via search_tools.
    /// </summary>
    public IReadOnlyList<AITool> GetAlwaysLoadedTools() =>
        GetRegistrationsSnapshot()
            .Where(t => t.Tool is not McpToolAdapter)
            .Select(t => t.Tool.ToAITool())
            .ToList();

    /// <summary>
    /// Returns a read-only view of all registered tools without copying the snapshot.
    /// </summary>
    public IReadOnlyList<ToolRegistration> GetAllRegistrations() =>
        Array.AsReadOnly(_tools.Registrations);

    /// <summary>
    /// Returns tools discovered from one MCP server.
    /// </summary>
    public IReadOnlyList<INetclawTool> GetToolsForServer(McpServerName serverName, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(serverName.Value) || maxResults <= 0)
            return [];

        return GetRegistrationsSnapshot()
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
        return GetRegistrationsSnapshot()
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

    internal static McpServerSummary SummarizeMcpServer(
        string serverName,
        IReadOnlyList<INetclawTool> tools)
    {
        var mcpTools = tools.OfType<McpToolAdapter>().ToList();
        return new McpServerSummary(
            serverName,
            DescribeServerCapability(serverName, mcpTools),
            mcpTools.Count);
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

        return GetRegistrationsSnapshot()
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

        var candidates = GetRegistrationsSnapshot()
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
    /// Uses progressive disclosure: list directly-callable tools and compact deferred
    /// capabilities, then route detailed discovery through search_tools.
    /// </summary>
    public string GenerateCompressedIndex()
    {
        var registrations = GetEntriesSnapshot();
        if (registrations.Count == 0)
            return string.Empty;

        return BuildCompressedIndex(registrations);
    }

    /// <summary>
    /// Generates the compressed tool index filtered to the tools discoverable by the
    /// supplied audience and feature gates.
    /// </summary>
    public string GenerateCompressedIndex(TrustAudience audience, ToolAccessPolicy policy)
    {
        var visible = GetEntriesSnapshot()
            .Where(entry => policy.IsToolExposed(entry.Registration.Tool, audience))
            .ToList();

        return BuildCompressedIndex(visible);
    }

    private static string BuildCompressedIndex(IReadOnlyList<RegistryEntry> registrations)
    {
        if (registrations.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        var coreTools = registrations
            .Where(static entry => entry.ExposureTier == ToolExposureTier.Core)
            .ToList();
        var deferredFirstPartyTools = registrations
            .Where(static entry => entry.ExposureTier == ToolExposureTier.Deferred
                                   && entry.Registration.Tool is not McpToolAdapter)
            .ToList();
        var mcpTools = registrations
            .Where(static entry => entry.Registration.Tool is McpToolAdapter)
            .ToList();

        if (coreTools.Count > 0)
        {
            sb.AppendLine("[directly callable core tools]");
            var builtinGrouped = coreTools.GroupBy(entry => entry.Registration.GrantCategory);
            foreach (var group in builtinGrouped)
            {
                var names = group.Select(entry => entry.Registration.Tool.Name);
                sb.AppendLine($"{group.Key}: {string.Join(", ", names)}");
            }
        }

        if (deferredFirstPartyTools.Count > 0)
        {
            if (sb.Length > 0)
                sb.AppendLine();

            sb.AppendLine("[deferred first-party tools - discover with search_tools]");
            foreach (var entry in deferredFirstPartyTools.OrderBy(
                         static entry => entry.Registration.Tool.Name,
                         StringComparer.Ordinal))
            {
                var tool = entry.Registration.Tool;
                sb.AppendLine($"{tool.Name}: {BuildCompactHint(tool.Description)}");
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

    private static IReadOnlyList<McpServerSummary> GetMcpServerSummaries(IReadOnlyList<RegistryEntry> registrations)
    {
        return registrations
            .Select(entry => entry.Registration.Tool)
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

    private static string BuildCompactHint(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "No description available.";

        var singleLine = Regex.Replace(description.Trim(), "\\s+", " ");
        return singleLine.Length <= 80 ? singleLine : singleLine[..77] + "...";
    }

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

    // The published array is never mutated after publication, so a reader can use it
    // directly. This replaces a lock plus a full list copy on every read.
    private IReadOnlyList<RegistryEntry> GetEntriesSnapshot() => _tools.Entries;

    private IReadOnlyList<ToolRegistration> GetRegistrationsSnapshot() => _tools.Registrations;

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

        public Task<string> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            ToolInvocationContext context,
            CancellationToken ct = default)
            => Task.FromResult("Not supported via adapter");
    }
}
