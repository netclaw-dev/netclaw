using Akka.Actor;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Search;
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Registers all first-party tool definitions with the <see cref="ToolRegistry"/>.
/// Tools are source-generated from <see cref="Netclaw.Tools.NetclawToolAttribute"/> — see ADR-001.
/// </summary>
public static class ToolRegistrationExtensions
{
    public static ToolRegistry WithFirstPartyTools(
        this ToolRegistry registry,
        ToolConfig config,
        ISearchBackend? searchBackend = null,
        ToolPathPolicy? pathPolicy = null)
    {
        registry.Register(new ShellTool(config, pathPolicy));
        registry.Register(new FileReadTool(config, pathPolicy));
        registry.Register(new FileWriteTool(pathPolicy));
        registry.Register(new AttachFileTool());
        if (searchBackend is not null)
            registry.Register(new WebSearchTool(searchBackend));
        registry.Register(new WebFetchTool());

        // Register search_tools meta-tool (always loaded, "builtin" grant)
        registry.Register(new SearchToolsTool(registry));

        return registry;
    }

    /// <summary>
    /// Registers reminder tools (set, cancel, list) that communicate with the
    /// <see cref="ReminderManagerActor"/> via Ask.
    /// </summary>
    public static ToolRegistry WithReminderTools(
        this ToolRegistry registry,
        IActorRef reminderManager,
        TimeProvider timeProvider,
        ReminderConfig config)
    {
        registry.Register(new SetReminderTool(reminderManager, timeProvider, config));
        registry.Register(new CancelReminderTool(reminderManager));
        registry.Register(new ListRemindersTool(reminderManager));
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
        string? grantCategory = null,
        IMcpToolInvoker? invoker = null)
    {
        foreach (var tool in tools)
            registry.Register(new McpToolAdapter(tool, serverName, tool.Name, grantCategory, invoker));

        return registry;
    }
}
