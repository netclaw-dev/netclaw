using System.Text;
using Akka.Actor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Memory;
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
    private readonly McpShadowCatalogWriter _shadowCatalogWriter;
    private readonly ToolRegistry _toolRegistry;
    private readonly MemoryIndexContextLayer _memoryIndexLayer;
    private readonly SubAgentDiscoveryContextLayer _subAgentDiscoveryLayer;
    private readonly SubAgentDefinitionRegistry _subAgentRegistry;
    private readonly FileSubAgentDefinitionLoader _agentLoader;
    private readonly SubAgentSpawner _subAgentSpawner;
    private readonly MemoryConfig _memoryConfig;
    private readonly ILogger<ToolIndexUpdater> _logger;

    public ToolIndexUpdater(
        McpShadowCatalogWriter shadowCatalogWriter,
        ToolRegistry toolRegistry,
        MemoryIndexContextLayer memoryIndexLayer,
        SubAgentDiscoveryContextLayer subAgentDiscoveryLayer,
        SubAgentDefinitionRegistry subAgentRegistry,
        FileSubAgentDefinitionLoader agentLoader,
        SubAgentSpawner subAgentSpawner,
        MemoryConfig memoryConfig,
        ILogger<ToolIndexUpdater> logger)
    {
        _shadowCatalogWriter = shadowCatalogWriter;
        _toolRegistry = toolRegistry;
        _memoryIndexLayer = memoryIndexLayer;
        _subAgentDiscoveryLayer = subAgentDiscoveryLayer;
        _subAgentRegistry = subAgentRegistry;
        _agentLoader = agentLoader;
        _subAgentSpawner = subAgentSpawner;
        _memoryConfig = memoryConfig;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var state = ResolveMemoryState();

        // Load file-based agent definitions (after MCP tools are registered).
        LoadFileBasedAgents();

        // Register spawn_agent tool now that all agents and the spawner are available.
        _toolRegistry.Register(new SpawnAgentTool(_subAgentRegistry, _subAgentSpawner));

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
        var files = _agentLoader.LoadAll();
        var loaded = 0;
        foreach (var file in files)
        {
            var profile = file.ToProfile();
            if (_subAgentRegistry.Register(profile))
            {
                loaded++;
            }
            else
            {
                _logger.LogWarning(
                    "Agent '{Name}' from file conflicts with an existing registration — skipping",
                    profile.Name);
            }
        }

        if (loaded > 0)
            _logger.LogInformation("Loaded {Count} file-based agent definition(s)", loaded);
    }

    private void UpdateSubAgentDiscovery()
    {
        var agents = _subAgentRegistry.GetUserFacing();
        if (agents.Count == 0)
        {
            _subAgentDiscoveryLayer.Update(string.Empty);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[available-subagents — use spawn_agent to delegate]");
        foreach (var agent in agents)
        {
            sb.AppendLine($"- {agent.Name}: {agent.Description} (timeout: {agent.TimeoutSeconds}s)");
        }

        sb.AppendLine();
        sb.AppendLine("Use spawn_agent(agent: \"<name>\", task: \"<description>\") to delegate.");
        sb.AppendLine("Subagents run autonomously with their own tools and return results.");

        _subAgentDiscoveryLayer.Update(sb.ToString());
        _logger.LogInformation("Subagent discovery layer updated ({Count} agents)", agents.Count);
    }

    private MemoryContextState ResolveMemoryState()
    {
        return _memoryConfig.Provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase)
            ? MemoryContextState.SqlitePrimary
            : MemoryContextState.SqliteDegraded;
    }
}
