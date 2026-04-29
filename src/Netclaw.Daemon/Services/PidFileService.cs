// -----------------------------------------------------------------------
// <copyright file="PidFileService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    private readonly DaemonRestartSignal _restartSignal;
    private readonly DaemonStartClock _startClock;
    private readonly ILogger<PidFileService> _logger;

    public PidFileService(NetclawPaths paths, DaemonRestartSignal restartSignal, DaemonStartClock startClock, ILogger<PidFileService> logger)
    {
        _paths = paths;
        _restartSignal = restartSignal;
        _startClock = startClock;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _paths.EnsureDirectoriesExist();

        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var startedAt = _startClock.StartedAt.ToString("o", CultureInfo.InvariantCulture);
        File.WriteAllText(_paths.PidFilePath, $"{pid}\n{startedAt}");
        _logger.LogDebug("Wrote daemon PID file: {PidFilePath} -> {Pid}", _paths.PidFilePath, pid);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_restartSignal.RestartRequested)
        {
            _logger.LogDebug("Restart requested — keeping PID file (same process): {PidFilePath}", _paths.PidFilePath);
            return Task.CompletedTask;
        }

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
