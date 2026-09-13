// -----------------------------------------------------------------------
// <copyright file="PidFileWatchdogService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Polls for PID file existence and initiates graceful shutdown when the file
/// is missing. This prevents orphan daemons when the PID file is lost (crash,
/// external deletion, filesystem issue). Uses periodic polling rather than
/// <see cref="FileSystemWatcher"/> because inotify on Linux can miss events,
/// and the daemon's lock file covers the detection gap.
/// </summary>
public sealed class PidFileWatchdogService : IHostedService, IDisposable
{
    private readonly NetclawPaths _paths;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<PidFileWatchdogService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private ITimer? _timer;

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    public PidFileWatchdogService(
        NetclawPaths paths,
        IHostApplicationLifetime appLifetime,
        ILogger<PidFileWatchdogService> logger,
        TimeProvider timeProvider)
        : this(paths, appLifetime, logger, timeProvider, DefaultPollInterval)
    {
    }

    internal PidFileWatchdogService(
        NetclawPaths paths,
        IHostApplicationLifetime appLifetime,
        ILogger<PidFileWatchdogService> logger,
        TimeProvider timeProvider,
        TimeSpan pollInterval)
    {
        _paths = paths;
        _appLifetime = appLifetime;
        _logger = logger;
        _timeProvider = timeProvider;
        _pollInterval = pollInterval;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = _timeProvider.CreateTimer(
            static state => ((PidFileWatchdogService)state!).CheckPidFile(),
            this,
            _pollInterval,
            _pollInterval);
        return Task.CompletedTask;
    }

    private void CheckPidFile()
    {
        if (!File.Exists(_paths.PidFilePath))
        {
            var timer = Interlocked.Exchange(ref _timer, null);
            if (timer is null)
                return;

            timer.Dispose();
            _logger.LogWarning(
                "PID file missing ({PidFilePath}). Initiating shutdown to prevent orphan daemon.",
                _paths.PidFilePath);
            _appLifetime.StopApplication();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var timer = Interlocked.Exchange(ref _timer, null);
        if (timer is not null)
            await timer.DisposeAsync();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _timer, null)?.Dispose();
    }
}
