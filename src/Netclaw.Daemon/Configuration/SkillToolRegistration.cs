// -----------------------------------------------------------------------
// <copyright file="SkillToolRegistration.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Telemetry;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Skills;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Registers skill management tools (<c>skill_load</c>, <c>skill_read_resource</c>,
/// <c>skill_manage</c>) with the <see cref="ToolRegistry"/> after the DI container
/// is built, so that <see cref="ISkillContentScanner"/> resolves to the real
/// implementation registered by <c>AddContentSecurity()</c>.
/// </summary>
internal static class SkillToolRegistration
{
    public static void RegisterSkillTools(IServiceProvider services)
    {
        var registry = services.GetRequiredService<ToolRegistry>();
        var skillRegistry = services.GetRequiredService<SkillRegistry>();
        var paths = services.GetRequiredService<NetclawPaths>();
        var toolConfig = services.GetRequiredService<ToolConfig>();
        var pathPolicy = services.GetRequiredService<ToolPathPolicy>();
        var scanner = services.GetRequiredService<ISkillContentScanner>();
        var mcpPromptLoader = services.GetRequiredService<IMcpPromptSkillLoader>();
        var inventoryRefresher = services.GetRequiredService<SkillInventoryRefresher>();
        var metrics = services.GetService<ISessionMetrics>();
        var subAgentRegistry = services.GetService<SubAgentDefinitionRegistry>();
        var subAgentSpawner = services.GetService<SubAgentSpawner>();
        var subAgentLoader = services.GetService<FileSubAgentDefinitionLoader>();
        var skillSyncConfig = services.GetService<SkillSyncConfig>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        registry.ReplaceCore(new FileReadTool(toolConfig, paths, pathPolicy, skillRegistry, metrics,
            loggerFactory.CreateLogger<FileReadTool>()));

        registry.WithSkillTools(
            skillRegistry,
            paths,
            scanner,
            mcpPromptLoader,
            inventoryRefresher,
            metrics,
            subAgentRegistry,
            subAgentSpawner,
            skillSyncConfig,
            subAgentLoader,
            loggerFactory.CreateLogger<SkillLoadTool>());
    }
}
