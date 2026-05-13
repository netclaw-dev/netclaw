// -----------------------------------------------------------------------
// <copyright file="Broadcasts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Serialization;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Published via Akka pub/sub after a session completes a turn.
/// Adapters subscribe to deliver replies through their respective channels.
/// </summary>
public sealed record TurnBroadcast : INetclawSerializableMessage
{
    public SessionId SessionId { get; init; }

    public SerializableChatMessage AssistantReply { get; init; } = new();

    public long BroadcastAtMs { get; init; }

    public DateTimeOffset BroadcastAt => DateTimeOffset.FromUnixTimeMilliseconds(BroadcastAtMs);
}

/// <summary>
/// Published via Akka pub/sub after a session completes compaction.
/// </summary>
public sealed record CompactionBroadcast : INetclawSerializableMessage
{
    public SessionId SessionId { get; init; }

    public string Summary { get; init; } = string.Empty;

    public long CompactedAtMs { get; init; }

    public DateTimeOffset CompactedAt => DateTimeOffset.FromUnixTimeMilliseconds(CompactedAtMs);
}
