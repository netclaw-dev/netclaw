// -----------------------------------------------------------------------
// <copyright file="ObservedMemoryCheckpointPayload.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;

namespace Netclaw.Actors.Memory;

/// <summary>
/// Checkpoint payload for observed memory proposals. JSON-serialized into the
/// SQLite checkpoint queue (<c>payload</c> column); the <see cref="JsonConverter"/>
/// attributes keep the on-disk discriminator strings (<c>"observed-memory-proposals"</c>,
/// <c>"normal"</c>, …) byte-identical while the in-memory fields carry the enums.
/// </summary>
public sealed record ObservedMemoryCheckpointPayload(
    string SessionId,
    [property: JsonConverter(typeof(CheckpointTriggerTypeJsonConverter))]
    CheckpointTriggerType TriggerType,
    [property: JsonConverter(typeof(MemorySensitivityJsonConverter))]
    MemorySensitivity Sensitivity,
    IReadOnlyList<SQLiteMemoryCurationOperation> Operations);
