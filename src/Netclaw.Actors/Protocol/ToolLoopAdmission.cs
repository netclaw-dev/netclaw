// -----------------------------------------------------------------------
// <copyright file="ToolLoopAdmission.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>Framework-owned record of the tool calls admitted in one parent batch.</summary>
public sealed record ToolLoopAdmission
{
    public string TaskId { get; init; } = string.Empty;
    public string ActionHash { get; init; } = string.Empty;
    public IReadOnlyList<ToolLoopPreparedCall> Calls { get; init; } = [];
    public IReadOnlyList<string> RefusedCallIds { get; init; } = [];
}

public sealed record ToolLoopPreparedCall(string CallId, string ToolName, string ArgumentsHash, bool AllowsPendingJob);

/// <summary>Framework-owned record of one admitted call's typed outcome.</summary>
public sealed record ToolLoopObservation
{
    public string CallId { get; init; } = string.Empty;
    public int Category { get; init; }
    public string ResultHash { get; init; } = string.Empty;
    public bool PendingJob { get; init; }
    public bool Synthetic { get; init; }
    public bool MissingReceipt { get; init; }
}
