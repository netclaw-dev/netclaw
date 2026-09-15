// -----------------------------------------------------------------------
// <copyright file="DurableActivityDispatchState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Serialization;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Reserves one activity fingerprint before a channel binding admits it to a
/// session pipeline. The reservation is the durable duplicate authority.
/// </summary>
public sealed record DurableActivityDispatchReserved(
    string ActivityFingerprint,
    string? EvictedActivityFingerprint) : INetclawSerializableMessage;

/// <summary>
/// Releases a reservation when the local session pipeline did not admit it.
/// </summary>
public sealed record DurableActivityDispatchReleased(
    string ActivityFingerprint) : INetclawSerializableMessage;

/// <summary>
/// Stores the ordered, bounded activity fingerprint state for a channel
/// binding. The snapshot permits journal compaction without losing retention.
/// </summary>
public sealed record DurableActivityDispatchSnapshot(
    IReadOnlyList<string> ActivityFingerprints) : INetclawSerializableMessage;
