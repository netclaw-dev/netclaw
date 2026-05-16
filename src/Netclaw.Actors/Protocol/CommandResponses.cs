// -----------------------------------------------------------------------
// <copyright file="CommandResponses.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Acknowledged receipt of a command by the session actor.
/// The command has been accepted and will be processed.
/// </summary>
public sealed record CommandAck(SessionId SessionId) : INoSerializationVerificationNeeded
{
    public static CommandAck For(SessionId sessionId) => new(sessionId);
}

/// <summary>
/// Negative acknowledgement — the command was rejected.
/// </summary>
public sealed record CommandNack(SessionId SessionId, string Reason) : INoSerializationVerificationNeeded
{
    public static CommandNack For(SessionId sessionId, string reason) =>
        new(sessionId, reason);
}
