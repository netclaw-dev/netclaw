// -----------------------------------------------------------------------
// <copyright file="BinaryUpdateCheckService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    private readonly string _upgradeHint;
    private readonly UpdateChannel _channel;

    public BinaryUpdateCheckService(
        HttpClient httpClient,
        ILogger<BinaryUpdateCheckService> logger,
        DaemonConfig daemonConfig,
        IOperationalNotificationSink? notificationSink = null,
        TimeProvider? timeProvider = null)
        // FullVersion (not Version) so a beta build reports its prerelease suffix and
        // can be compared against the beta channel rather than stranding on its core.
        : this(httpClient, logger, BuildInfo.FullVersion, daemonConfig.DisableSelfUpdate,
            notificationSink, timeProvider, daemonConfig.UpdateChannel)
    {
    }

    // Internal constructor for testing
    internal BinaryUpdateCheckService(
        HttpClient httpClient,
        ILogger<BinaryUpdateCheckService> logger,
        string currentVersion,
        bool selfUpdateDisabled = false,
        IOperationalNotificationSink? notificationSink = null,
        TimeProvider? timeProvider = null,
        UpdateChannel channel = UpdateChannel.Stable)
    {
        _httpClient = httpClient;
        _logger = logger;
        _currentVersion = currentVersion;
        _upgradeHint = selfUpdateDisabled
            ? "Pull a newer container image to upgrade."
            : "Run 'netclaw update' to upgrade.";
        _notificationSink = notificationSink;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _channel = channel;
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
                _httpClient, _currentVersion, cancellationToken, _channel);

            if (result.IsUpdateAvailable)
            {
                _logger.LogWarning(
                    "Netclaw update available: {CurrentVersion} → {LatestVersion}. {UpgradeHint}",
                    result.CurrentVersion, result.LatestVersion, _upgradeHint);

                EmitUpdateAlert(result);
            }
            else if (!result.CheckSucceeded)
            {
                _logger.LogInformation("Netclaw update check failed: {ErrorDetail}", result.ErrorDetail);
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
            $"Netclaw update available: {result.CurrentVersion} → {result.LatestVersion}. {_upgradeHint}",
            AlertSeverity.Info,
            source: result.LatestVersion,
            context: new Dictionary<string, string>
            {
                ["currentVersion"] = result.CurrentVersion,
                ["latestVersion"] = result.LatestVersion,
            }));
    }
}
