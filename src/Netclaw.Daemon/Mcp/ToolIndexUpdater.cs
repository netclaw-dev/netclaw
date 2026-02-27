using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Refreshes the compressed tool index in the system prompt after MCP startup completes.
/// Runs after <see cref="McpClientManager"/> so MCP tools are included in the index.
/// </summary>
internal sealed class ToolIndexUpdater : IHostedService
{
    private readonly FileSystemPromptProvider _promptProvider;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger<ToolIndexUpdater> _logger;

    public ToolIndexUpdater(
        FileSystemPromptProvider promptProvider,
        ToolRegistry toolRegistry,
        ILogger<ToolIndexUpdater> logger)
    {
        _promptProvider = promptProvider;
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var index = _toolRegistry.GenerateCompressedIndex();
        _promptProvider.SetToolIndex(index);
        _logger.LogInformation("Tool index updated ({ToolCount} registrations)", _toolRegistry.GetAllRegistrations().Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
