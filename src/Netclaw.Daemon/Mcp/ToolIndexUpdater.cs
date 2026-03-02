using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Tools;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Refreshes the dynamic tool index after MCP startup completes.
/// Runs after <see cref="McpClientManager"/> so MCP tools are included in the index.
/// The index is written to system-managed shadow files and injected dynamically
/// into each LLM call via file-backed context layers.
/// </summary>
internal sealed class ToolIndexUpdater : IHostedService
{
    private readonly McpShadowCatalogWriter _shadowCatalogWriter;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger<ToolIndexUpdater> _logger;

    public ToolIndexUpdater(
        McpShadowCatalogWriter shadowCatalogWriter,
        ToolRegistry toolRegistry,
        ILogger<ToolIndexUpdater> logger)
    {
        _shadowCatalogWriter = shadowCatalogWriter;
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _shadowCatalogWriter.WriteCatalogs();
        _logger.LogInformation("Tool index updated ({ToolCount} registrations)", _toolRegistry.GetAllRegistrations().Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
