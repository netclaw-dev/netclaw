// -----------------------------------------------------------------------
// <copyright file="LegacyCoordinationEvent.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Google.Protobuf;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>A deployed coordination record that needs replay-state validation.</summary>
public sealed record LegacyCoordinationEvent : SessionProtocol.ISessionEvent
{
    public required SessionId SessionId { get; init; }

    public required string Manifest { get; init; }

    public required ImmutableArray<byte> Payload { get; init; }

    public required IMessage LegacyPayload { get; init; }

    public required long RecordedAtMs { get; init; }

    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);
}
