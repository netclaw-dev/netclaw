// -----------------------------------------------------------------------
// <copyright file="DaemonRestartCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Akka.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Services;

public interface IDaemonRestartCoordinator
{
    Task RequestConfigRestartAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Coordinates graceful daemon restart by closing ingress, draining active sessions,
/// persisting restart recovery state, and only then requesting host shutdown.
/// </summary>
public sealed class DaemonRestartCoordinator : IDaemonRestartCoordinator
{
    internal static readonly TimeSpan RestartDrainTimeout = TimeSpan.FromSeconds(20);

    private readonly SessionIngressGate _ingressGate;
    private readonly RestartManifestStore _manifestStore;
    private readonly IRequiredActor<SessionManagerActorKey> _sessionManagerProvider;
    private readonly DaemonRestartSignal _restartSignal;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly DaemonLifecycleNotifier _lifecycleNotifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DaemonRestartCoordinator> _logger;
    private readonly TimeSpan _restartDrainTimeout;

    public DaemonRestartCoordinator(
        SessionIngressGate ingressGate,
        RestartManifestStore manifestStore,
        IRequiredActor<SessionManagerActorKey> sessionManagerProvider,
        DaemonRestartSignal restartSignal,
        IHostApplicationLifetime appLifetime,
        DaemonLifecycleNotifier lifecycleNotifier,
        TimeProvider timeProvider,
        ILogger<DaemonRestartCoordinator> logger,
        TimeSpan? restartDrainTimeout = null)
    {
        _ingressGate = ingressGate;
        _manifestStore = manifestStore;
        _sessionManagerProvider = sessionManagerProvider;
        _restartSignal = restartSignal;
        _appLifetime = appLifetime;
        _lifecycleNotifier = lifecycleNotifier;
        _timeProvider = timeProvider;
        _logger = logger;
        _restartDrainTimeout = restartDrainTimeout ?? RestartDrainTimeout;
    }

    public async Task RequestConfigRestartAsync(CancellationToken cancellationToken)
    {
        if (!_ingressGate.TryClose(SessionIngressGate.RestartInProgressMessage))
        {
            _logger.LogInformation("Coordinated restart already in progress; ignoring duplicate config reload request.");
            return;
        }

        try
        {
            var sessionManager = await _sessionManagerProvider.GetAsync(cancellationToken);

            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            drainCts.CancelAfter(_restartDrainTimeout);

            var drainResult = await SessionDrainHelper.DrainAsync(
                sessionManager, "config-reload", _logger, drainCts.Token);

            var manifest = new RestartManifest
            {
                Reason = "config-reload",
                RequestedAt = _timeProvider.GetUtcNow(),
                SessionIds = drainResult.AllSessionIds.Select(static id => id.Value).ToList(),
                TimedOutSessionIds = drainResult.TimedOutSessionIds.Select(static id => id.Value).ToList()
            };

            if (manifest.SessionIds.Count == 0)
                await _manifestStore.DeleteAsync();
            else
                await _manifestStore.WriteAsync(manifest, cancellationToken);

            _lifecycleNotifier.NotifyShutdown("config-reload", drainResult.ToNotificationContext());
            _restartSignal.RequestRestart();
            _appLifetime.StopApplication();
        }
        catch
        {
            _ingressGate.Reopen();
            throw;
        }
    }
}
