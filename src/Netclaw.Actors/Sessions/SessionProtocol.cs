// -----------------------------------------------------------------------
// <copyright file="SessionProtocol.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// The session entity's <b>external</b> message contract — every message that
/// crosses the session actor boundary: commands and queries it receives,
/// responses and outputs it sends, and events it persists.
/// Internal self-messages the actor tells itself to drive its own state machine
/// (e.g. <c>LlmResponseReceived</c>, <c>ProcessingWatchdogExpired</c>) are
/// actor-private plumbing and intentionally live with the actor implementation,
/// NOT in this protocol class.
///
/// Split into partial files by category (<c>.Commands</c>, <c>.Events</c>,
/// <c>.Responses</c>, <c>.Outputs</c>) to keep each file readable; the logical
/// type is one <see cref="SessionProtocol"/> nesting all external message types
/// (accessed as <c>SessionProtocol.TurnRecorded</c>).
/// </summary>
public static partial class SessionProtocol
{
    /// <summary>Marker for an imperative request received by the session actor.</summary>
    public interface ISessionCommand : IWithSessionId
    {
    }

    /// <summary>
    /// Marker for a past-tense fact persisted to the session journal. Inherits
    /// <see cref="INetclawSerializableMessage"/> so every event is automatically
    /// bound to the protobuf serializer — declaring an event cannot forget the tag.
    /// </summary>
    public interface ISessionEvent : IWithSessionId, INetclawSerializableMessage
    {
        DateTimeOffset Timestamp { get; }
    }

    /// <summary>Marker for a read request received by the session actor, expecting an <see cref="ISessionResponse"/>.</summary>
    public interface ISessionQuery : IWithSessionId
    {
    }

    /// <summary>
    /// Marker for a reply the session actor sends in response to a command/query Ask —
    /// <see cref="CommandAck"/> (accepted), <see cref="CommandDeferred"/> (retry later),
    /// and <see cref="CommandNack"/> (rejected).
    /// Lets callers (channel bindings, HTTP callback endpoints, the reminder execution
    /// actor) declare a typed Ask response instead of an untyped <c>object</c>.
    /// Transient: replies are local-dispatch only.
    /// </summary>
    public interface ISessionResponse : INoSerializationVerificationNeeded
    {
        SessionId SessionId { get; }
    }
}
