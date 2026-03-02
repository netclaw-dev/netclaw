using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Netclaw.Configuration;
using Netclaw.Search;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Registers all first-party tool definitions with the <see cref="ToolRegistry"/>.
/// Tools are source-generated from <see cref="Netclaw.Tools.NetclawToolAttribute"/> — see ADR-001.
/// </summary>
public static class ToolRegistrationExtensions
{
    public static ToolRegistry WithFirstPartyTools(this ToolRegistry registry, ToolConfig config, ISearchBackend? searchBackend = null)
    {
        registry.Register(new ShellTool(config));
        registry.Register(new FileReadTool(config));
        registry.Register(new FileWriteTool());
        registry.Register(new AttachFileTool());
        if (searchBackend is not null)
            registry.Register(new WebSearchTool(searchBackend));
        registry.Register(new WebFetchTool());

        // Register search_tools meta-tool (always loaded, "builtin" grant)
        registry.Register(new SearchToolsTool(registry));

        return registry;
    }

    /// <summary>
    /// Register MCP tools discovered from an MCP server into the tool registry.
    /// Tools are wrapped as <see cref="McpToolAdapter"/> with namespaced names.
    /// </summary>
    public static ToolRegistry WithMcpTools(
        this ToolRegistry registry,
        string serverName,
        IList<McpClientTool> tools,
        string? grantCategory = null)
    {
        foreach (var tool in tools)
            registry.Register(new McpToolAdapter(tool, serverName, tool.Name, grantCategory));

        return registry;
    }
}
