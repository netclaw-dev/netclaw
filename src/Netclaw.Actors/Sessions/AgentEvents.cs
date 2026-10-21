// -----------------------------------------------------------------------
// <copyright file="AgentEvents.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

// TEST-FIRST STUBS (child-approval-events-and-parent-pause, PR 1, task 1). These shells let the PR 1 specs compile.
// The records hold data only. Every method throws NotImplementedException. Task 2.1 to task 2.4 replace them.

/// <summary>The address of one agent of a session. A value object. It never holds an actor reference.</summary>
internal abstract record AgentAddress
{
    private AgentAddress() { }

    /// <summary>The main agent of a session.</summary>
    public sealed record Session(SessionId SessionId) : AgentAddress;

    /// <summary>A child run of a session.</summary>
    public sealed record ChildRun(SessionId SessionId, SubAgentRunId RunId, AgentName Agent) : AgentAddress;
}

/// <summary>The minimal envelope of an agent event.</summary>
internal sealed record AgentEnvelope(AgentAddress Sender, AgentAddress Target, DateTimeOffset SentAt);

/// <summary>The closed family of events between the agents of one session. In-process only.</summary>
internal abstract record AgentEvent
{
    private AgentEvent() { }

    public required AgentEnvelope Envelope { get; init; }

    public sealed record ChildApprovalRequested(ToolCallId PromptCallId, ToolName Tool) : AgentEvent;

    public sealed record ChildApprovalResolved(ToolCallId PromptCallId, ChildApprovalResolution Resolution) : AgentEvent;

    public sealed record ChildRunEnded(
        ChildNotice Notice,
        SubAgentRunOutcome Outcome,
        SubAgentOutcomeReason? Reason,
        ChildRunResultFile ResultFile) : AgentEvent;
}

/// <summary>How a child consent request closed.</summary>
internal abstract record ChildApprovalResolution
{
    private ChildApprovalResolution() { }

    /// <summary>The operator allowed the call: one time (<see cref="ConsentAnswer.Once"/>) or with a grant.</summary>
    public sealed record Approved(ConsentAnswer Answer) : ChildApprovalResolution;

    public sealed record Refused(RefusalKind Kind) : ChildApprovalResolution;

    public sealed record Withdrawn(WithdrawReason Reason) : ChildApprovalResolution;
}

internal enum WithdrawReason
{
    CancelledByParent,
    ChildEnded,
    ChildLost,
    DaemonRestarted,
    PromptUnavailable
}

/// <summary>The runtime-made path of the <c>result.md</c> file of a child run.</summary>
internal readonly record struct ChildRunResultFile(string Value);

/// <summary>
/// The owner-stamped builders of agent events. Only the session that owns a run calls them, with its own run record.
/// </summary>
internal static class AgentEventFactory
{
    /// <summary>
    /// Builds the event for a child consent request after <c>ToolApprovalRequested</c> persists. The sender comes
    /// from <paramref name="run"/>, never from the prompt. The payload holds no text of the prompt.
    /// </summary>
    public static AgentEvent.ChildApprovalRequested ApprovalRequested(
        SessionId owner,
        BackgroundChildRun run,
        ToolInteractionRequest prompt,
        DateTimeOffset sentAt)
        => throw new NotImplementedException("Task 2.3 builds ChildApprovalRequested.");

    /// <summary>
    /// The field 14 adapter: reads an admitted child result notice as <see cref="AgentEvent.ChildRunEnded"/>.
    /// The stored row does not change.
    /// </summary>
    public static AgentEvent.ChildRunEnded RunEnded(
        InputAdmitted admitted,
        BackgroundChildRun run,
        ChildRunResultFile resultFile)
        => throw new NotImplementedException("Task 2.2 reads field 14 as ChildRunEnded.");
}

/// <summary>The one deterministic path from typed agent events to model text.</summary>
internal static class AgentEventRenderer
{
    public static string Render(IReadOnlyList<AgentEvent> events)
        => throw new NotImplementedException("Task 2.4 renders agent events.");
}
