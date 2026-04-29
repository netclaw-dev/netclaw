// -----------------------------------------------------------------------
// <copyright file="ObservedMemoryCheckpointPayload.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Memory;

public sealed record ObservedMemoryCheckpointPayload(
    string SessionId,
    string TriggerType,
    string Sensitivity,
    IReadOnlyList<SQLiteMemoryCurationOperation> Operations);
