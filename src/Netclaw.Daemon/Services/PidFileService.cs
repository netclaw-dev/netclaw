using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Owns daemon PID file lifecycle so process restarts always refresh the PID.
/// </summary>
public sealed class PidFileService : IHostedService
{
    private readonly NetclawPaths _paths;
    private readonly ILogger<PidFileService> _logger;

    public PidFileService(NetclawPaths paths, ILogger<PidFileService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _paths.EnsureDirectoriesExist();

        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        File.WriteAllText(_paths.PidFilePath, pid);
        _logger.LogDebug("Wrote daemon PID file: {PidFilePath} -> {Pid}", _paths.PidFilePath, pid);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(_paths.PidFilePath))
            {
                File.Delete(_paths.PidFilePath);
                _logger.LogDebug("Removed daemon PID file: {PidFilePath}", _paths.PidFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up daemon PID file: {PidFilePath}", _paths.PidFilePath);
        }

        return Task.CompletedTask;
    }
}
