// -----------------------------------------------------------------------
// <copyright file="SessionDrainHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Shared drain logic used by both <see cref="DaemonRestartCoordinator"/> (config restart)
/// and the CoordinatedShutdown drain task (SIGTERM / daemon stop).
/// </summary>
internal static class SessionDrainHelper
{
    /// <summary>
    /// Queries the session manager for active sessions, sends <see cref="PrepareForDaemonRestart"/>
    /// to each in parallel, and waits for acknowledgement or cancellation.
    /// </summary>
    /// <remarks>
    /// Callers control the timeout by providing a <see cref="CancellationToken"/> from a
    /// <see cref="CancellationTokenSource"/> with the desired deadline.
    /// </remarks>
    public static async Task<DrainResult> DrainAsync(
        IActorRef sessionManager,
        string reason,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var activeIdsResponse = await sessionManager.Ask<ActiveEntityIds>(
            GetActiveEntityIds.Instance,
            timeout: Timeout.InfiniteTimeSpan,
            cancellationToken: cancellationToken);

        var sessionIds = activeIdsResponse.EntityIds
            .Select(id => new SessionId(id))
            .ToArray();

        if (sessionIds.Length == 0)
            return DrainResult.Empty;

        logger.LogInformation(
            "Draining {SessionCount} active session(s) for {Reason}.",
            sessionIds.Length, reason);

        var drainTasks = sessionIds.Select(async sessionId =>
        {
            try
            {
                var ack = await sessionManager.Ask<CommandAck>(
                    new PrepareForDaemonRestart(sessionId, reason),
                    timeout: Timeout.InfiniteTimeSpan,
                    cancellationToken: cancellationToken);

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

        return new DrainResult(sessionIds, drained, timedOut);
    }

    internal sealed record DrainOutcome(SessionId SessionId, bool Drained);

    internal sealed record DrainResult(
        IReadOnlyList<SessionId> AllSessionIds,
        IReadOnlyList<SessionId> DrainedSessionIds,
        IReadOnlyList<SessionId> TimedOutSessionIds)
    {
        public static readonly DrainResult Empty = new([], [], []);

        public Dictionary<string, string> ToNotificationContext() => new()
        {
            ["drainOutcome"] = TimedOutSessionIds.Count == 0 ? "drained" : "timeout",
            ["activeSessions"] = AllSessionIds.Count.ToString(CultureInfo.InvariantCulture),
            ["drainedSessions"] = DrainedSessionIds.Count.ToString(CultureInfo.InvariantCulture),
            ["timedOutSessions"] = TimedOutSessionIds.Count.ToString(CultureInfo.InvariantCulture)
        };
    }
}
