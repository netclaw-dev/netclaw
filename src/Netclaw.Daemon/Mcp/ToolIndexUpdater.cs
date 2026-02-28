using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Refreshes the dynamic tool index context layer after MCP startup completes.
/// Runs after <see cref="McpClientManager"/> so MCP tools are included in the index.
/// The tool index is NOT part of the persisted system prompt — it's injected
/// dynamically into each LLM call so rehydrated sessions see fresh tool info.
/// </summary>
internal sealed class ToolIndexUpdater : IHostedService
{
    private readonly ToolIndexContextLayer _contextLayer;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger<ToolIndexUpdater> _logger;

    public ToolIndexUpdater(
        ToolIndexContextLayer contextLayer,
        ToolRegistry toolRegistry,
        ILogger<ToolIndexUpdater> logger)
    {
        _contextLayer = contextLayer;
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var index = _toolRegistry.GenerateCompressedIndex();
        _contextLayer.Update(index);
        _logger.LogInformation("Tool index updated ({ToolCount} registrations)", _toolRegistry.GetAllRegistrations().Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
