// -----------------------------------------------------------------------
// <copyright file="ToolLoopCheckpoint.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>Framework-owned private evidence for one authorized task.</summary>
public sealed record ToolLoopCheckpoint
{
    public string TaskId { get; init; } = string.Empty;
    public IReadOnlyList<ToolLoopEntry> Entries { get; init; } = [];
    public IReadOnlyList<ToolLoopKey> ColdKeys { get; init; } = [];
    public IReadOnlyList<ToolLoopAdjacent> AdjacentHistory { get; init; } = [];
    public string? LastBlockedAction { get; init; }
}

public sealed record ToolLoopEntry
{
    public string ToolName { get; init; } = string.Empty;
    public string ArgumentsHash { get; init; } = string.Empty;
    public string OutcomeHash { get; init; } = string.Empty;
    public int EqualRounds { get; init; }
    public bool Corrected { get; init; }
    public bool PendingJob { get; init; }
}

public sealed record ToolLoopKey(string ToolName, string ArgumentsHash);
public sealed record ToolLoopAdjacent(string ActionHash, string OutcomeHash);

public sealed record ToolLoopDelta
{
    public string TaskId { get; init; } = string.Empty;
    public bool Reset { get; init; }
    public IReadOnlyList<ToolLoopEntry> Upserts { get; init; } = [];
    public IReadOnlyList<ToolLoopKey> RemovedKeys { get; init; } = [];
    public IReadOnlyList<ToolLoopKey> ColdKeys { get; init; } = [];
    public IReadOnlyList<ToolLoopAdjacent> AdjacentHistory { get; init; } = [];
    public string? LastBlockedAction { get; init; }
}

public sealed record ToolLoopAdmission
{
    public string TaskId { get; init; } = string.Empty;
    public string ActionHash { get; init; } = string.Empty;
    public IReadOnlyList<ToolLoopPreparedCall> Calls { get; init; } = [];
    public IReadOnlyList<string> RefusedCallIds { get; init; } = [];
}

public sealed record ToolLoopPreparedCall(string CallId, string ToolName, string ArgumentsHash, bool AllowsPendingJob);
public sealed record ToolLoopObservation
{
    public string CallId { get; init; } = string.Empty;
    public int Category { get; init; }
    public string ResultHash { get; init; } = string.Empty;
    public bool PendingJob { get; init; }
    public bool Synthetic { get; init; }
    public bool MissingReceipt { get; init; }
}
