using System.Globalization;
using System.Linq;
using Akka.Actor;
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
            var activeIdsResponse = await sessionManager.Ask<ActiveEntityIds>(
                GetActiveEntityIds.Instance,
                timeout: _restartDrainTimeout,
                cancellationToken: cancellationToken);

            var sessionIds = activeIdsResponse.EntityIds
                .Select(id => new SessionId(id))
                .ToArray();

            _logger.LogInformation(
                "Starting coordinated config restart for {SessionCount} active session(s).",
                sessionIds.Length);

            var drainResult = await DrainSessionsAsync(sessionManager, sessionIds, cancellationToken);

            var manifest = new RestartManifest
            {
                Reason = "config-reload",
                RequestedAt = _timeProvider.GetUtcNow(),
                SessionIds = sessionIds.Select(static id => id.Value).ToList(),
                TimedOutSessionIds = drainResult.TimedOutSessionIds.Select(static id => id.Value).ToList()
            };

            if (manifest.SessionIds.Count == 0)
                await _manifestStore.DeleteAsync();
            else
                await _manifestStore.WriteAsync(manifest, cancellationToken);

            var notificationContext = new Dictionary<string, string>
            {
                ["drainOutcome"] = drainResult.TimedOutSessionIds.Count == 0 ? "drained" : "timeout",
                ["activeSessions"] = sessionIds.Length.ToString(CultureInfo.InvariantCulture),
                ["drainedSessions"] = drainResult.DrainedSessionIds.Count.ToString(CultureInfo.InvariantCulture),
                ["timedOutSessions"] = drainResult.TimedOutSessionIds.Count.ToString(CultureInfo.InvariantCulture)
            };

            _lifecycleNotifier.NotifyShutdown("config-reload", notificationContext);
            _restartSignal.RequestRestart();
            _appLifetime.StopApplication();
        }
        catch
        {
            _ingressGate.Reopen();
            throw;
        }
    }

    private async Task<DrainResult> DrainSessionsAsync(IActorRef sessionManager, IReadOnlyList<SessionId> sessionIds, CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0)
            return DrainResult.Empty;

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainCts.CancelAfter(_restartDrainTimeout);

        var drainTasks = sessionIds.Select(async sessionId =>
        {
            try
            {
                var ack = await sessionManager.Ask<CommandAck>(
                    new PrepareForDaemonRestart
                    {
                        SessionId = sessionId,
                        Reason = "config-reload"
                    },
                    timeout: _restartDrainTimeout,
                    cancellationToken: drainCts.Token);

                return new DrainOutcome(sessionId, ack.SessionId == sessionId);
            }
            catch (OperationCanceledException)
            {
                return new DrainOutcome(sessionId, false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to drain session {SessionId} before restart.", sessionId.Value);
                return new DrainOutcome(sessionId, false);
            }
        }).ToArray();

        var outcomes = await Task.WhenAll(drainTasks);
        var drained = outcomes.Where(static x => x.Drained).Select(static x => x.SessionId).ToArray();
        var timedOut = outcomes.Where(static x => !x.Drained).Select(static x => x.SessionId).ToArray();

        if (timedOut.Length == 0)
        {
            _logger.LogInformation("All {SessionCount} active session(s) drained before restart.", drained.Length);
        }
        else
        {
            _logger.LogWarning(
                "Proceeding with restart after drain timeout; {DrainedCount} session(s) drained and {TimedOutCount} will recover from the last durable checkpoint.",
                drained.Length,
                timedOut.Length);
        }

        return new DrainResult(drained, timedOut);
    }

    private sealed record DrainOutcome(SessionId SessionId, bool Drained);

    private sealed record DrainResult(IReadOnlyList<SessionId> DrainedSessionIds, IReadOnlyList<SessionId> TimedOutSessionIds)
    {
        public static readonly DrainResult Empty = new([], []);
    }
}
