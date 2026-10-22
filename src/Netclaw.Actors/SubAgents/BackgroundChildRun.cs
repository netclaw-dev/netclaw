// -----------------------------------------------------------------------
// <copyright file="BackgroundChildRun.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
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
    public required WorkingContextSnapshot? InitialWorkingSnapshot { get; init; }
    public required long AcceptedAtMs { get; init; }
    public required ChildRunLifecycle Lifecycle { get; init; }

    /// <summary>
    /// The reminder fire key of the turn that started this child, or null when no reminder started that turn.
    /// The turn that consumes the result notice copies it to <see cref="SessionProtocol.TurnRecorded.SourceReminderId"/>, so a
    /// failed post of the child result raises the reminder alert. The field is durable: the acceptance event and
    /// the snapshot both carry it. A record written before the field reads as null.
    /// </summary>
    public required ReminderId? SourceReminderId { get; init; }
}

/// <summary>
/// The durable life of one child run. A run ends in <see cref="Terminal"/>. The session owner then admits one
/// notice for it, and the run becomes <see cref="Reported"/>. The result text is not part of the state after
/// that step: the journal keeps the terminal event, and <c>result.md</c> keeps a copy for the model.
/// </summary>
public abstract record ChildRunLifecycle
{
    private ChildRunLifecycle() { }
    public sealed record Accepted(ChildRunCheckpoint? Checkpoint) : ChildRunLifecycle;
    public sealed record Cancelling(ChildRunCheckpoint? Checkpoint) : ChildRunLifecycle;

    /// <summary>
    /// The child ended and the owner has not yet admitted its notice.
    /// <see cref="LegacyPrepared"/> is set only for a run that a deployed journal or snapshot recorded in the old
    /// prepared state. No writer sets it.
    /// </summary>
    public sealed record Terminal(ChildRunTerminal Result) : ChildRunLifecycle
    {
        public LegacyPreparedResult? LegacyPrepared { get; init; }
    }

    /// <summary>The parent received the result: as an admitted notice, or as a legacy tool pair.</summary>
    public sealed record Reported(SubAgentRunOutcome Outcome, SubAgentOutcomeReason? Reason, ChildRunReport Report)
        : ChildRunLifecycle;
}

/// <summary>How the parent received the result of a child run.</summary>
public abstract record ChildRunReport
{
    private ChildRunReport() { }

    /// <summary>The owner admitted a notice input for the run.</summary>
    public sealed record Notice(InputId NoticeInputId) : ChildRunReport;

    /// <summary>A deployed runtime put the synthetic tool pair into the history. Read-only data.</summary>
    public sealed record LegacyDelivery(ToolCallId DeliveryCallId) : ChildRunReport;
}

public sealed record ChildRunCheckpoint(int CompletedRound, string Summary, WorkingContextDelta ConfirmedActivity);
public sealed record ChildRunTerminal(SubAgentProtocol.SubAgentResult Result, bool Lost, string? EvidenceWarning);

/// <summary>
/// The tool pair and the reviewed working context that a deployed runtime prepared for a result.
/// The reader keeps them so that a later legacy closure puts the pair into the history once.
/// </summary>
public sealed record LegacyPreparedResult(SerializableChatMessage Call, SerializableChatMessage Result, WorkingContextDelta? ReviewedWorkingContext);

/// <summary>
/// Names the tool pair that a deployed parent decision consumed. Deployed journal rows carry it, and the reader
/// applies it. No writer sets it.
/// </summary>
public sealed record ChildContinuation(SubAgentRunId RunId, ToolCallId DeliveryCallId);
