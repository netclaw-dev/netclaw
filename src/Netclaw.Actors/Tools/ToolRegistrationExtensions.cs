using Akka.Actor;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Search;
using Netclaw.Security;
using Netclaw.Security.Skills;

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
        ToolPathPolicy? pathPolicy = null,
        ToolAccessPolicy? toolAccessPolicy = null,
        NetclawPaths? paths = null)
    {
        registry.Register(new ShellTool(config, pathPolicy));
        registry.Register(new FileReadTool(config, pathPolicy, paths));
        registry.Register(new FileWriteTool(config, pathPolicy));
        registry.Register(new AttachFileTool(config));
        if (searchBackend is not null)
            registry.Register(new WebSearchTool(searchBackend));
        registry.Register(new WebFetchTool());

        // Register search_tools meta-tool (always loaded, "builtin" grant)
        registry.Register(new SearchToolsTool(registry, toolAccessPolicy));

        return registry;
    }

    /// <summary>
    /// Registers skill management tools (skill_load, skill_read_resource, skill_manage).
    /// All use "builtin" grant — available to all audiences.
    /// </summary>
    public static ToolRegistry WithSkillTools(
        this ToolRegistry registry,
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        NetclawPaths paths,
        ISkillContentScanner scanner)
    {
        registry.Register(new SkillLoadTool(skillRegistry));
        registry.Register(new SkillReadResourceTool(skillRegistry));
        registry.Register(new SkillManageTool(skillRegistry, skillIndexLayer, paths, scanner));
        return registry;
    }

    /// <summary>
    /// Registers reminder tools (set, cancel, list, get_history) that communicate with the
    /// <see cref="ReminderManagerActor"/> via Ask.
    /// </summary>
    public static ToolRegistry WithReminderTools(
        this ToolRegistry registry,
        IActorRef reminderManager,
        TimeProvider timeProvider,
        ReminderConfig config,
        ReminderHistoryStore historyStore)
    {
        registry.Register(new SetReminderTool(reminderManager, timeProvider, config));
        registry.Register(new CancelReminderTool(reminderManager));
        registry.Register(new ListRemindersTool(reminderManager));
        registry.Register(new GetReminderHistoryTool(historyStore));
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
        McpCapabilityClass capabilityClass = McpCapabilityClass.Unknown,
        IMcpToolInvoker? invoker = null)
    {
        foreach (var tool in tools)
            registry.Register(new McpToolAdapter(tool, serverName, tool.Name, grantCategory, capabilityClass, invoker));

        return registry;
    }
}
