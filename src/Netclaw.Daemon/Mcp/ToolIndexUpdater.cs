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
/// based on Memorizer connectivity.
/// </summary>
internal sealed class ToolIndexUpdater : IHostedService
{
    private readonly McpShadowCatalogWriter _shadowCatalogWriter;
    private readonly ToolRegistry _toolRegistry;
    private readonly MemoryIndexContextLayer _memoryIndexLayer;
    private readonly ILogger<ToolIndexUpdater> _logger;

    public ToolIndexUpdater(
        McpShadowCatalogWriter shadowCatalogWriter,
        ToolRegistry toolRegistry,
        MemoryIndexContextLayer memoryIndexLayer,
        ILogger<ToolIndexUpdater> logger)
    {
        _shadowCatalogWriter = shadowCatalogWriter;
        _toolRegistry = toolRegistry;
        _memoryIndexLayer = memoryIndexLayer;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _shadowCatalogWriter.WriteCatalogs();
        _logger.LogInformation("Tool index updated ({ToolCount} registrations)", _toolRegistry.GetAllRegistrations().Count);

        // Detect Memorizer connectivity by checking for known MCP tool names
        var memorizerConnected =
            _toolRegistry.GetByName("memorizer/search_memories") is not null
            || _toolRegistry.GetByName("memorizer/search") is not null;

        _memoryIndexLayer.Update(memorizerConnected);
        _logger.LogInformation("Memory context layer updated (Memorizer connected: {Connected})", memorizerConnected);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
