using Akka.Actor;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Shared drain logic used by both <see cref="DaemonRestartCoordinator"/> (config restart)
/// and the CoordinatedShutdown drain task (SIGTERM / daemon stop).
/// </summary>
internal static class SessionDrainHelper
{
    /// <summary>
    /// Sends <see cref="PrepareForDaemonRestart"/> to each session in parallel and waits
    /// for acknowledgement or timeout.
    /// </summary>
    public static async Task<DrainResult> DrainAsync(
        IActorRef sessionManager,
        IReadOnlyList<SessionId> sessionIds,
        string reason,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0)
            return DrainResult.Empty;

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainCts.CancelAfter(timeout);

        var drainTasks = sessionIds.Select(async sessionId =>
        {
            try
            {
                var ack = await sessionManager.Ask<CommandAck>(
                    new PrepareForDaemonRestart
                    {
                        SessionId = sessionId,
                        Reason = reason
                    },
                    timeout: timeout,
                    cancellationToken: drainCts.Token);

                return new DrainOutcome(sessionId, ack.SessionId == sessionId);
            }
            catch (OperationCanceledException)
            {
                return new DrainOutcome(sessionId, false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to drain session {SessionId} before shutdown.", sessionId.Value);
                return new DrainOutcome(sessionId, false);
            }
        }).ToArray();

        var outcomes = await Task.WhenAll(drainTasks);
        var drained = outcomes.Where(static x => x.Drained).Select(static x => x.SessionId).ToArray();
        var timedOut = outcomes.Where(static x => !x.Drained).Select(static x => x.SessionId).ToArray();

        if (timedOut.Length == 0)
        {
            logger.LogInformation("All {SessionCount} active session(s) drained successfully.", drained.Length);
        }
        else
        {
            logger.LogWarning(
                "Drain completed with {DrainedCount} session(s) drained and {TimedOutCount} timed out; timed-out sessions will recover from the last durable checkpoint.",
                drained.Length,
                timedOut.Length);
        }

        return new DrainResult(drained, timedOut);
    }

    internal sealed record DrainOutcome(SessionId SessionId, bool Drained);

    internal sealed record DrainResult(IReadOnlyList<SessionId> DrainedSessionIds, IReadOnlyList<SessionId> TimedOutSessionIds)
    {
        public static readonly DrainResult Empty = new([], []);
    }
}
