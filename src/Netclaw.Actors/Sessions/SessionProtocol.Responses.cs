// -----------------------------------------------------------------------
// <copyright file="SessionProtocol.Responses.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;

namespace Netclaw.Actors.Sessions;

public static partial class SessionProtocol
{
    // ===== Responses (Ask replies) =====

    /// <summary>
    /// Acknowledged receipt of a command by the session actor.
    /// The command has been accepted and will be processed.
    /// </summary>
    public record CommandAck(SessionId SessionId) : ISessionResponse
    {
        public static CommandAck For(SessionId sessionId) => new(sessionId);
    }

    /// <summary>
    /// Reports that a session stopped and supplies any reminder that startup must register.
    /// </summary>
    public sealed record DaemonRestartPrepared(
        SessionId SessionId,
        ReminderDefinition? RestartReminder) : CommandAck(SessionId);

    /// <summary>
    /// Negative acknowledgement — the command was rejected.
    /// </summary>
    public sealed record CommandNack(SessionId SessionId, string Reason) : ISessionResponse
    {
        public static CommandNack For(SessionId sessionId, string reason) =>
            new(sessionId, reason);
    }
}
