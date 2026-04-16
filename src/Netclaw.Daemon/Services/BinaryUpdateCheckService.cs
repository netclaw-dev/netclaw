using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Checks for binary updates at daemon startup and periodically thereafter.
/// Emits an <see cref="AlertType.UpdateAvailable"/> operational alert when
/// an update is detected, so configured webhooks (Slack, etc.) get notified.
/// The result is cached in <see cref="UpdateCheckService"/> for 1 hour so
/// <see cref="Gateway.DaemonRuntimeStatusService"/> can include update info in the status API.
/// Never blocks startup, never downloads anything.
/// </summary>
internal sealed class BinaryUpdateCheckService : BackgroundService
{
    /// <summary>
    /// How often to recheck for updates after the initial startup check.
    /// </summary>
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(24);

    private readonly HttpClient _httpClient;
    private readonly ILogger<BinaryUpdateCheckService> _logger;
    private readonly IOperationalNotificationSink? _notificationSink;
    private readonly TimeProvider _timeProvider;
    private readonly string _currentVersion;

    public BinaryUpdateCheckService(
        HttpClient httpClient,
        ILogger<BinaryUpdateCheckService> logger,
        IOperationalNotificationSink? notificationSink = null,
        TimeProvider? timeProvider = null)
        : this(httpClient, logger, BuildInfo.Version, notificationSink, timeProvider)
    {
    }

    // Internal constructor for testing
    internal BinaryUpdateCheckService(
        HttpClient httpClient,
        ILogger<BinaryUpdateCheckService> logger,
        string currentVersion,
        IOperationalNotificationSink? notificationSink = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _currentVersion = currentVersion;
        _notificationSink = notificationSink;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial check at startup
        await CheckAndNotifyAsync(stoppingToken);

        // Periodic recheck
        using var timer = new PeriodicTimer(RecheckInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckAndNotifyAsync(stoppingToken);
        }
    }

    internal async Task CheckAndNotifyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await UpdateCheckService.CheckForUpdateAsync(
                _httpClient, _currentVersion, cancellationToken);

            if (result.IsUpdateAvailable)
            {
                _logger.LogWarning(
                    "Netclaw update available: {CurrentVersion} → {LatestVersion}. Run 'netclaw update' to upgrade.",
                    result.CurrentVersion, result.LatestVersion);

                EmitUpdateAlert(result);
            }
            else
            {
                _logger.LogInformation("Netclaw is up to date (v{Version})", _currentVersion);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Binary update check failed — continuing normally");
        }
    }

    private void EmitUpdateAlert(UpdateCheckResult result)
    {
        _notificationSink?.Emit(OperationalAlert.Create(
            _timeProvider,
            "update.available",
            AlertType.UpdateAvailable,
            $"Netclaw update available: {result.CurrentVersion} → {result.LatestVersion}. Run 'netclaw update' to upgrade.",
            AlertSeverity.Info,
            source: result.LatestVersion,
            context: new Dictionary<string, string>
            {
                ["currentVersion"] = result.CurrentVersion,
                ["latestVersion"] = result.LatestVersion,
            }));
    }
}
