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
        ISearchBackend? searchBackend = null,
        ToolPathPolicy? pathPolicy = null,
        ShellCommandPolicy? shellCommandPolicy = null,
        ToolAccessPolicy? toolAccessPolicy = null,
        NetclawPaths? paths = null,
        WebhookRouteStore? webhookRouteStore = null)
    {
        registry.Register(new ShellTool(config, pathPolicy, shellCommandPolicy));
        registry.Register(new FileReadTool(config, pathPolicy, paths));
        registry.Register(new FileWriteTool(config, pathPolicy));
        registry.Register(new FileEditTool(config, pathPolicy));
        registry.Register(new AttachFileTool(config));
        if (webhookRouteStore is not null)
        {
            registry.Register(new SetWebhookTool(webhookRouteStore));
            registry.Register(new ListWebhooksTool(webhookRouteStore));
            registry.Register(new DeleteWebhookTool(webhookRouteStore));
        }
        if (searchBackend is not null)
            registry.Register(new WebSearchTool(searchBackend));
        registry.Register(new WebFetchTool(config));
        registry.Register(new SetWorkingDirectoryTool(config));

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
        SkillIndexContextLayer skillIndexLayer,
        NetclawPaths paths,
        ISkillContentScanner scanner,
        IReadOnlyList<ResolvedExternalSource> externalSources,
        ISessionMetrics? sessionMetrics = null,
        SubAgentDefinitionRegistry? subAgentRegistry = null,
        SubAgentSpawner? subAgentSpawner = null,
        SkillSyncConfig? skillSyncConfig = null)
    {
        registry.Register(new SkillLoadTool(skillRegistry, scanner, sessionMetrics, subAgentRegistry, subAgentSpawner, skillSyncConfig));
        registry.Register(new SkillReadResourceTool(skillRegistry, scanner, skillSyncConfig));
        registry.Register(new SkillManageTool(skillRegistry, skillIndexLayer, paths, scanner, externalSources));
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
        foreach (var tool in tools)
        {
            var adapter = new McpToolAdapter(tool, serverName, tool.Name, grantCategory, invoker, maxDescriptionChars);
            registry.Register(adapter);

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

        return registry;
    }
}
