// -----------------------------------------------------------------------
// <copyright file="ToolIndexUpdater.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Refreshes the dynamic tool index, memory context layers, and subagent discovery
/// layer after MCP startup completes. Runs after <see cref="McpClientManager"/> so
/// MCP tools are included in the index and file-based agent definitions can
/// reference MCP tool names.
/// </summary>
internal sealed class ToolIndexUpdater : IHostedService
{
    private readonly NetclawPaths _paths;
    private readonly McpShadowCatalogWriter _shadowCatalogWriter;
    private readonly ToolRegistry _toolRegistry;
    private readonly MemoryIndexContextLayer _memoryIndexLayer;
    private readonly SubAgentDiscoveryContextLayer _subAgentDiscoveryLayer;
    private readonly SubAgentDefinitionRegistry _subAgentRegistry;
    private readonly FileSubAgentDefinitionLoader _agentLoader;
    private readonly SubAgentSpawner _subAgentSpawner;
    private readonly SubAgentConfig _subAgentConfig;
    private readonly ILogger<ToolIndexUpdater> _logger;

    public ToolIndexUpdater(
        NetclawPaths paths,
        McpShadowCatalogWriter shadowCatalogWriter,
        ToolRegistry toolRegistry,
        MemoryIndexContextLayer memoryIndexLayer,
        SubAgentDiscoveryContextLayer subAgentDiscoveryLayer,
        SubAgentDefinitionRegistry subAgentRegistry,
        FileSubAgentDefinitionLoader agentLoader,
        SubAgentSpawner subAgentSpawner,
        SubAgentConfig subAgentConfig,
        ILogger<ToolIndexUpdater> logger)
    {
        _paths = paths;
        _shadowCatalogWriter = shadowCatalogWriter;
        _toolRegistry = toolRegistry;
        _memoryIndexLayer = memoryIndexLayer;
        _subAgentDiscoveryLayer = subAgentDiscoveryLayer;
        _subAgentRegistry = subAgentRegistry;
        _agentLoader = agentLoader;
        _subAgentSpawner = subAgentSpawner;
        _subAgentConfig = subAgentConfig;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var state = ResolveMemoryState();

        // Load file-based agent definitions (after MCP tools are registered).
        LoadFileBasedAgents();

        // Register spawn_agent tool now that all agents and the spawner are available.
        _toolRegistry.Register(new SpawnAgentTool(_subAgentRegistry, _subAgentSpawner, _paths, _subAgentConfig, _agentLoader));

        // Write catalogs after all tools are registered.
        _shadowCatalogWriter.WriteCatalogs();
        _logger.LogInformation("Tool index updated ({ToolCount} registrations)", _toolRegistry.GetAllRegistrations().Count);

        _memoryIndexLayer.Update(state);
        _logger.LogInformation("Memory context layer updated (state: {State})", state);

        // Update subagent discovery context layer.
        UpdateSubAgentDiscovery();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void LoadFileBasedAgents()
    {
        var profiles = _agentLoader.LoadAll();
        var conflicts = _subAgentRegistry.ReplaceFileProfiles(profiles);

        foreach (var conflict in conflicts)
        {
            _logger.LogWarning(
                "Agent '{Name}' from file conflicts with an existing registration — skipping",
                conflict);
        }

        if (profiles.Count > 0)
            _logger.LogInformation("Loaded {Count} file-based agent definition(s)", profiles.Count - conflicts.Count);
    }

    private void UpdateSubAgentDiscovery()
    {
        var agents = _subAgentRegistry.GetUserFacing();
        _subAgentDiscoveryLayer.Update(SubAgentDiscoveryContextLayer.BuildIndex(agents, _paths.AgentsDirectory));
        _logger.LogInformation("Subagent discovery layer updated ({Count} agents)", agents.Count);
    }

    private static MemoryContextState ResolveMemoryState() => MemoryContextState.SqlitePrimary;
}
