using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Refreshes the dynamic tool index and memory context layers after MCP startup completes.
/// Runs after <see cref="McpClientManager"/> so MCP tools are included in the index.
/// The tool index is written to system-managed shadow files and injected dynamically
/// into each LLM call via file-backed context layers. Also updates memory context layers
/// based on config provider + Memorizer connectivity.
/// </summary>
internal sealed class ToolIndexUpdater : IHostedService
{
    private readonly McpShadowCatalogWriter _shadowCatalogWriter;
    private readonly ToolRegistry _toolRegistry;
    private readonly MemoryIndexContextLayer _memoryIndexLayer;
    private readonly MemoryConfig _memoryConfig;
    private readonly ILogger<ToolIndexUpdater> _logger;

    public ToolIndexUpdater(
        McpShadowCatalogWriter shadowCatalogWriter,
        ToolRegistry toolRegistry,
        MemoryIndexContextLayer memoryIndexLayer,
        MemoryConfig memoryConfig,
        ILogger<ToolIndexUpdater> logger)
    {
        _shadowCatalogWriter = shadowCatalogWriter;
        _toolRegistry = toolRegistry;
        _memoryIndexLayer = memoryIndexLayer;
        _memoryConfig = memoryConfig;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _shadowCatalogWriter.WriteCatalogs();
        _logger.LogInformation("Tool index updated ({ToolCount} registrations)", _toolRegistry.GetAllRegistrations().Count);

        var state = ResolveMemoryState();
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
