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
    private readonly TimeSpan _pollInterval;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    public PidFileWatchdogService(
        NetclawPaths paths,
        IHostApplicationLifetime appLifetime,
        ILogger<PidFileWatchdogService> logger)
        : this(paths, appLifetime, logger, DefaultPollInterval)
    {
    }

    internal PidFileWatchdogService(
        NetclawPaths paths,
        IHostApplicationLifetime appLifetime,
        ILogger<PidFileWatchdogService> logger,
        TimeSpan pollInterval)
    {
        _paths = paths;
        _appLifetime = appLifetime;
        _logger = logger;
        _pollInterval = pollInterval;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollTask = PollAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        // OperationCanceledException from WaitForNextTickAsync on shutdown is
        // expected and handled by StopAsync via ConfigureAwaitOptions.SuppressThrowing.
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (!File.Exists(_paths.PidFilePath))
            {
                _logger.LogWarning(
                    "PID file missing ({PidFilePath}). Initiating shutdown to prevent orphan daemon.",
                    _paths.PidFilePath);
                _appLifetime.StopApplication();
                return;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_pollTask is not null)
        {
            await _pollTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
