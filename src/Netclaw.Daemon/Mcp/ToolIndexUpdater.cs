using Akka.Actor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Refreshes the dynamic tool index and memory context layers after MCP startup completes.
/// Runs after <see cref="McpClientManager"/> so MCP tools are included in the index.
/// The tool index is written to system-managed shadow files and injected dynamically
/// into each LLM call via file-backed context layers. Also updates memory context layers
/// based on config provider + Memorizer connectivity.
///
/// When Memorizer is configured and connected, registers memory tools
/// (<see cref="MemorizerFindMemoriesTool"/>, <see cref="MemorizerGetMemoriesTool"/>,
/// <see cref="MemorizerStoreMemoryTool"/>, <see cref="MemorizerUpdateMemoryTool"/>)
/// that delegate to Memorizer MCP or curation subagents.
/// </summary>
internal sealed class ToolIndexUpdater : IHostedService
{
    private readonly McpShadowCatalogWriter _shadowCatalogWriter;
    private readonly ToolRegistry _toolRegistry;
    private readonly MemoryIndexContextLayer _memoryIndexLayer;
    private readonly MemoryConfig _memoryConfig;
    private readonly SubAgentConfig _subAgentConfig;
    private readonly ActorSystem _actorSystem;
    private readonly IChatClientProvider _clientProvider;
    private readonly ILogger<ToolIndexUpdater> _logger;

    public ToolIndexUpdater(
        McpShadowCatalogWriter shadowCatalogWriter,
        ToolRegistry toolRegistry,
        MemoryIndexContextLayer memoryIndexLayer,
        MemoryConfig memoryConfig,
        SubAgentConfig subAgentConfig,
        ActorSystem actorSystem,
        IChatClientProvider clientProvider,
        ILogger<ToolIndexUpdater> logger)
    {
        _shadowCatalogWriter = shadowCatalogWriter;
        _toolRegistry = toolRegistry;
        _memoryIndexLayer = memoryIndexLayer;
        _memoryConfig = memoryConfig;
        _subAgentConfig = subAgentConfig;
        _actorSystem = actorSystem;
        _clientProvider = clientProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var state = ResolveMemoryState();

        // When Memorizer is connected, register subagent-backed memory tools.
        // These must be registered after MCP discovery so the curation subagent
        // can resolve Memorizer tools from the registry at execution time.
        if (state == MemoryContextState.MemorizerConnected)
        {
            _toolRegistry.Register(new MemorizerFindMemoriesTool(_toolRegistry));
            _toolRegistry.Register(new MemorizerGetMemoriesTool(_toolRegistry));
            _toolRegistry.Register(new MemorizerStoreMemoryTool(
                _actorSystem, _clientProvider, _toolRegistry, _subAgentConfig));
            _toolRegistry.Register(new MemorizerUpdateMemoryTool(_toolRegistry));
            _logger.LogInformation("Registered Memorizer-backed memory tools (find, get, store, update)");
        }

        // Write catalogs after all tools are registered (including Memorizer tools above)
        _shadowCatalogWriter.WriteCatalogs();
        _logger.LogInformation("Tool index updated ({ToolCount} registrations)", _toolRegistry.GetAllRegistrations().Count);

        _memoryIndexLayer.Update(state);
        _logger.LogInformation("Memory context layer updated (state: {State})", state);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private MemoryContextState ResolveMemoryState()
    {
        if (!_memoryConfig.Provider.Equals("memorizer", StringComparison.OrdinalIgnoreCase))
            return MemoryContextState.FileBacked;

        // Memorizer path: check if MCP tools are actually connected
        var memorizerConnected =
            _toolRegistry.GetByName("memorizer/search_memories") is not null
            || _toolRegistry.GetByName("memorizer/search") is not null;

        return memorizerConnected
            ? MemoryContextState.MemorizerConnected
            : MemoryContextState.MemorizerDisconnected;
    }
}
