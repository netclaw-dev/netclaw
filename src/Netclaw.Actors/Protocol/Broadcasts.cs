// -----------------------------------------------------------------------
// <copyright file="Broadcasts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Published via Akka pub/sub after a session completes a turn.
/// Adapters subscribe to deliver replies through their respective channels.
/// </summary>
public sealed class TurnBroadcast
{
    public SessionId SessionId { get; set; }

    public SerializableChatMessage AssistantReply { get; set; } = new();

    public long BroadcastAtMs { get; set; }

    public DateTimeOffset BroadcastAt => DateTimeOffset.FromUnixTimeMilliseconds(BroadcastAtMs);
}

/// <summary>
/// Published via Akka pub/sub after a session completes compaction.
/// </summary>
public sealed class CompactionBroadcast
{
    public SessionId SessionId { get; set; }

    public string Summary { get; set; } = string.Empty;

    public long CompactedAtMs { get; set; }

    public DateTimeOffset CompactedAt => DateTimeOffset.FromUnixTimeMilliseconds(CompactedAtMs);
}
