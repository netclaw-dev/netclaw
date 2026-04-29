// -----------------------------------------------------------------------
// <copyright file="Broadcasts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ProtoBuf;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Published via Akka pub/sub after a session completes a turn.
/// Adapters subscribe to deliver replies through their respective channels.
/// </summary>
[ProtoContract]
public sealed class TurnBroadcast
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    [ProtoMember(2)]
    public SerializableChatMessage AssistantReply { get; set; } = new();

    [ProtoMember(3)]
    public long BroadcastAtMs { get; set; }

    public DateTimeOffset BroadcastAt => DateTimeOffset.FromUnixTimeMilliseconds(BroadcastAtMs);
}

/// <summary>
/// Published via Akka pub/sub after a session completes compaction.
/// </summary>
[ProtoContract]
public sealed class CompactionBroadcast
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    [ProtoMember(2)]
    public string Summary { get; set; } = string.Empty;

    [ProtoMember(3)]
    public long CompactedAtMs { get; set; }

    public DateTimeOffset CompactedAt => DateTimeOffset.FromUnixTimeMilliseconds(CompactedAtMs);
}
