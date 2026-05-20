// -----------------------------------------------------------------------
// <copyright file="CommandResponses.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Common marker for the two replies the session actor sends in response to a
/// command Ask — <see cref="CommandAck"/> (accepted) and <see cref="CommandNack"/>
/// (rejected). Lets callers (channel bindings, HTTP callback endpoints, the
/// reminder execution actor) declare a typed Ask response instead of taking an
/// untyped <c>object</c> and pattern-matching against an open set.
/// </summary>
public interface ICommandReply : INoSerializationVerificationNeeded
{
    SessionId SessionId { get; }
}

/// <summary>
/// Acknowledged receipt of a command by the session actor.
/// The command has been accepted and will be processed.
/// </summary>
public sealed record CommandAck(SessionId SessionId) : ICommandReply
{
    public static CommandAck For(SessionId sessionId) => new(sessionId);
}

/// <summary>
/// Negative acknowledgement — the command was rejected.
/// </summary>
public sealed record CommandNack(SessionId SessionId, string Reason) : ICommandReply
{
    public static CommandNack For(SessionId sessionId, string reason) =>
        new(sessionId, reason);
}
