// -----------------------------------------------------------------------
// <copyright file="BackgroundChildRun.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.SubAgents;

public abstract record ChildRunStartKey(SessionId SessionId, string TurnId)
{
    public sealed record Tool(SessionId SessionId, string TurnId, ToolCallId CallId)
        : ChildRunStartKey(SessionId, TurnId);
    public sealed record Slash(SessionId SessionId, string TurnId, InputId InputId)
        : ChildRunStartKey(SessionId, TurnId);
}

public sealed record BackgroundChildRun
{
    public required SubAgentRunId RunId { get; init; }
    public required SubAgentScopeId ScopeId { get; init; }
    public required ChildRunStartKey StartKey { get; init; }
    public required AgentName AgentName { get; init; }
    public required string SourceOperation { get; init; }
    public required string ArgumentsDigest { get; init; }
    public required SessionProtocol.TurnContextRecord OriginalContext { get; init; }
    public required WorkingContextSnapshot InitialWorkingSnapshot { get; init; }
    public required long AcceptedAtMs { get; init; }
    public required ChildRunLifecycle Lifecycle { get; init; }
}

public abstract record ChildRunLifecycle
{
    private ChildRunLifecycle() { }
    public sealed record Accepted(ChildRunCheckpoint? Checkpoint) : ChildRunLifecycle;
    public sealed record Cancelling(ChildRunCheckpoint? Checkpoint) : ChildRunLifecycle;
    public sealed record Terminal(ChildRunTerminal Result) : ChildRunLifecycle;
    public sealed record Prepared(ChildPreparedResult Delivery) : ChildRunLifecycle;
    public sealed record Consumed(ChildPreparedResult Delivery) : ChildRunLifecycle;
    public sealed record Closed(ToolCallId DeliveryCallId, SubAgentRunOutcome Outcome,
        SubAgentOutcomeReason? Reason) : ChildRunLifecycle;
}

public sealed record ChildRunCheckpoint(int CompletedRound, string Summary, WorkingContextDelta ConfirmedActivity);
public sealed record ChildRunTerminal(SubAgentProtocol.SubAgentResult Result, bool Lost, string? EvidenceWarning);
public sealed record ChildPreparedResult(ChildRunTerminal Terminal, SerializableChatMessage Call, SerializableChatMessage Result);
public sealed record ChildContinuation(SubAgentRunId RunId, ToolCallId DeliveryCallId);
