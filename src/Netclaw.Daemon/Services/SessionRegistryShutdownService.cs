using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Daemon.Gateway;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Ensures active SignalR sessions are disposed promptly during host shutdown.
/// </summary>
public sealed class SessionRegistryShutdownService : IHostedService
{
    private readonly SessionRegistry _registry;
    private readonly ILogger<SessionRegistryShutdownService> _logger;

    public SessionRegistryShutdownService(
        SessionRegistry registry,
        ILogger<SessionRegistryShutdownService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var hardCutoff = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hardCutoff.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            await _registry.ShutdownAsync(hardCutoff.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Session shutdown hit hard cutoff during daemon stop.");
        }
    }
}
