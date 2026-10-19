// -----------------------------------------------------------------------
// <copyright file="LegacyCoordinationEvidence.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

/// <summary>Retained evidence from a deployed coordination journal record.</summary>
public sealed record LegacyCoordinationEvidence
{
    public required string Manifest { get; init; }

    public required ImmutableArray<byte> Payload { get; init; }

    public LegacyCoordinationDisposition Disposition { get; init; }

    public IReadOnlyList<InputId> InputIds { get; init; } = Array.Empty<InputId>();

    public IReadOnlyList<InputId> ActiveInputIds { get; init; } = Array.Empty<InputId>();

    public TurnContextRecord? AdoptedContext { get; init; }

    public string? Reason { get; init; }

    public SubAgentRunId? ChildRunId { get; init; }

    public ToolCallId? DeliveryCallId { get; init; }

    public bool LegacyDispatchClosed { get; init; }

    public ChildRunCheckpoint? LegacyCheckpoint { get; init; }

    public long? LegacyTerminalSequenceNr { get; init; }

    /// <summary>Approval facts from a deployed snapshot that need owner hydration.</summary>
    public IReadOnlyList<SessionProtocol.ISessionEvent> ApprovalEvents { get; init; } = [];

    public bool HasRetainedFacts => Payload.Length > 0 || LegacyDispatchClosed || LegacyCheckpoint is not null
                                    || LegacyTerminalSequenceNr is not null || ApprovalEvents.Count > 0;

}

public enum LegacyCoordinationDisposition
{
    Preserved = 0,
    AppliedOrdinaryAdoption = 1,
    AppliedChildContinuation = 2,
    QuarantinedInvalidAdoption = 3,
    PreservedLegacyFacts = 4,
    UnresolvedLegacyChild = 5
}
