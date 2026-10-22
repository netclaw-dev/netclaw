// -----------------------------------------------------------------------
// <copyright file="ChildRunPark.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Tools;

namespace Netclaw.Actors.SubAgents;

// STUB (durable-child-consent-wait, PR 1 tests first, task 3.1). These types let the PR 1 tests compile. The PR 1
// implementer gives them their behavior. The members have no "required" modifier, because a test makes an empty park.

/// <summary>
/// The state of a child run that waits for consent (design D2). The child builds it when its batch ends. PR 1 keeps it
/// actor-local in the owning session.
/// </summary>
internal sealed record ChildRunPark
{
    /// <summary>Every message after the system message, as the child keeps it (M12).</summary>
    public IReadOnlyList<SerializableChatMessage> Transcript { get; init; } = [];

    /// <summary>The loop guard after the finished calls. It is the only copy of the round (M10, H1).</summary>
    public TurnLoopGuardState? LoopGuard { get; init; }

    /// <summary>The project directory and the inherited working directory that the batch ran with (H3).</summary>
    public ChildParkScopeDirectories? ScopeDirectories { get; init; }

    /// <summary>The skill overlay of the spawn, or null. The agent profile does not hold it.</summary>
    public string? SkillOverlay { get; init; }

    /// <summary>One consent request for each parked call, as the gate made it.</summary>
    public IReadOnlyList<ParentApprovalRequest> Requests { get; init; } = [];
}

/// <summary>The two working directories of the batch that parked (design D2).</summary>
internal sealed record ChildParkScopeDirectories(string? ProjectDirectory, string? InheritedWorkingDirectory);

/// <summary>The child tells its owner that its batch parked (design D1). The sender is the child.</summary>
internal sealed record ChildParkReached(SubAgentRunId RunId, ChildRunPark Park) : INoSerializationVerificationNeeded;

/// <summary>The journal rejected an event of a child run (design D3). The owner records the end of the run.</summary>
internal sealed record ChildEventRejected(SubAgentRunId RunId) : INoSerializationVerificationNeeded;

/// <summary>The start of a resumed child: the park, and the recorded answer of each parked call by its call ID.</summary>
internal sealed record ChildRunResume(ChildRunPark Park, IReadOnlyDictionary<string, ApprovalRedriveEntry> Answers);

/// <summary>What the owner does with a park that arrives (design D3).</summary>
internal enum ChildParkDecision
{
    /// <summary>Record the requests and show the prompts.</summary>
    Accept,

    /// <summary>The run is cancelling. End it as cancelled and show no prompt.</summary>
    EndCancelled,

    /// <summary>The sender is not the owned child. Send it ChildTerminalCommitted and record nothing.</summary>
    StopSender
}

/// <summary>What the owner does after an answer to a parked request (design D3, resume gate).</summary>
internal enum ChildResumeDecision
{
    /// <summary>The run has no park.</summary>
    Nothing,

    /// <summary>A request is open, or a restart drain is pending.</summary>
    Wait,

    /// <summary>Every request has an answer. Prepare the resume.</summary>
    Resume
}

/// <summary>The two pure decisions of the park owner. They are the Stryker targets of child-resume-gate.</summary>
internal static class ChildParkRules
{
    // STUB: the implementer replaces both bodies.
    public static ChildParkDecision AcceptPark(bool ownedSender, ChildRunLifecycle? lifecycle)
        => ChildParkDecision.Accept;

    public static ChildResumeDecision ResumeGate(bool hasPark, int openRequests, bool drainPending)
        => ChildResumeDecision.Wait;
}
