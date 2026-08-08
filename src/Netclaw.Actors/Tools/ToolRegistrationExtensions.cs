// -----------------------------------------------------------------------
// <copyright file="ToolRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Skills;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Telemetry;
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
        NetclawPaths paths,
        ToolPathPolicy pathPolicy,
        ShellCommandPolicy shellCommandPolicy,
        ISearchBackend? searchBackend = null,
        ToolAccessPolicy? toolAccessPolicy = null,
        WebhookRouteStore? webhookRouteStore = null)
    {
        registry.Register(new ShellTool(config, pathPolicy, shellCommandPolicy));
        registry.Register(new FileReadTool(config, paths, pathPolicy));
        registry.Register(new FileListTool(config, paths, pathPolicy));
        registry.Register(new FileWriteTool(config, paths, pathPolicy));
        registry.Register(new FileEditTool(config, paths, pathPolicy));
        registry.Register(new AttachFileTool(config, paths, pathPolicy));
        if (webhookRouteStore is not null)
        {
            registry.Register(new SetWebhookTool(webhookRouteStore));
            registry.Register(new ListWebhooksTool(webhookRouteStore));
            registry.Register(new DeleteWebhookTool(webhookRouteStore));
        }
        if (searchBackend is not null)
            registry.Register(new WebSearchTool(searchBackend));
        registry.Register(new WebFetchTool(config));
        registry.Register(new SetWorkingDirectoryTool(config, paths));

        // Register search_tools and load_tool meta-tools (always loaded, "builtin" grant)
        registry.Register(new SearchToolsTool(registry, toolAccessPolicy));
        registry.Register(new LoadToolTool(registry, toolAccessPolicy));

        return registry;
    }

    /// <summary>
    /// Registers skill management tools (skill_load, skill_read_resource, skill_manage).
    /// All use "builtin" grant — available to all audiences.
    /// </summary>
    public static ToolRegistry WithSkillTools(
        this ToolRegistry registry,
        SkillRegistry skillRegistry,
        NetclawPaths paths,
        ISkillContentScanner scanner,
        IMcpPromptSkillLoader mcpPromptLoader,
        SkillInventoryRefresher inventoryRefresher,
        ISessionMetrics? sessionMetrics = null,
        SubAgentDefinitionRegistry? subAgentRegistry = null,
        SubAgentSpawner? subAgentSpawner = null,
        SkillSyncConfig? skillSyncConfig = null,
        FileSubAgentDefinitionLoader? subAgentLoader = null,
        ILogger<SkillLoadTool>? skillLoadLogger = null)
    {
        registry.Register(new SkillLoadTool(
            skillRegistry,
            scanner,
            mcpPromptLoader,
            sessionMetrics,
            subAgentRegistry,
            subAgentSpawner,
            skillSyncConfig,
            skillLoadLogger,
            subAgentLoader));
        registry.Register(new SkillReadResourceTool(skillRegistry, scanner, skillSyncConfig));
        registry.Register(new SkillManageTool(skillRegistry, paths, scanner, inventoryRefresher));
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
        ReminderHistoryStore historyStore,
        SchedulingConfig schedulingConfig,
        IEnumerable<IReminderTargetResolver>? targetResolvers = null)
    {
        registry.Register(new SetReminderTool(reminderManager, timeProvider, schedulingConfig, targetResolvers));
        registry.Register(new CancelReminderTool(reminderManager, schedulingConfig));
        registry.Register(new ListRemindersTool(reminderManager, schedulingConfig));
        registry.Register(new GetReminderHistoryTool(historyStore, schedulingConfig));
        return registry;
    }

    /// <summary>
    /// Registers background job tools that communicate with the
    /// <see cref="BackgroundJobManagerActor"/> via Ask.
    /// </summary>
    public static ToolRegistry WithBackgroundJobTools(
        this ToolRegistry registry,
        IActorRef jobManager)
    {
        registry.Register(new CheckBackgroundJobTool(jobManager));
        return registry;
    }

    /// <summary>
    /// Register MCP tools discovered from an MCP server into the tool registry.
    /// Tools are wrapped as <see cref="McpToolAdapter"/> with namespaced names.
    /// Descriptions exceeding <paramref name="maxDescriptionChars"/> are truncated.
    /// Schemas exceeding <paramref name="maxSchemaWarnChars"/> trigger a warning log.
    /// </summary>
    public static ToolRegistry WithMcpTools(
        this ToolRegistry registry,
        string serverName,
        IList<McpClientTool> tools,
        string? grantCategory = null,
        IMcpToolInvoker? invoker = null,
        int maxDescriptionChars = 0,
        int maxSchemaWarnChars = 0,
        ILogger? logger = null)
    {
        var adapters = PrepareMcpTools(
            serverName,
            tools.Cast<AIFunction>().ToList(),
            grantCategory,
            invoker,
            maxDescriptionChars,
            maxSchemaWarnChars,
            logger);

        foreach (var adapter in adapters)
            registry.Register(adapter);

        return registry;
    }

    public static IReadOnlyList<McpToolAdapter> PrepareMcpTools(
        string serverName,
        IReadOnlyList<AIFunction> tools,
        string? grantCategory,
        IMcpToolInvoker? invoker,
        int maxDescriptionChars,
        int maxSchemaWarnChars,
        ILogger? logger)
    {
        var adapters = new List<McpToolAdapter>(tools.Count);
        foreach (var tool in tools)
        {
            var adapter = new McpToolAdapter(
                tool,
                serverName,
                tool.Name,
                grantCategory,
                invoker,
                maxDescriptionChars,
                logger);
            adapters.Add(adapter);

            if (maxSchemaWarnChars > 0 && adapter.ParameterSchema.ValueKind != System.Text.Json.JsonValueKind.Undefined)
            {
                var schemaChars = adapter.ParameterSchema.GetRawText().Length;
                if (schemaChars > maxSchemaWarnChars)
                {
                    logger?.LogWarning(
                        "MCP tool '{ServerName}/{ToolName}' has an oversized schema ({SchemaChars} chars, threshold {Threshold}). " +
                        "This wastes context window budget when the tool is loaded. Consider asking the MCP server author to reduce schema verbosity.",
                        serverName, tool.Name, schemaChars, maxSchemaWarnChars);
                }
            }
        }

        return adapters;
    }
}
