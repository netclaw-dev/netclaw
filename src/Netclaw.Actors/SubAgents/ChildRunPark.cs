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

/// <summary>
/// The state of a child run that waits for consent (design D2). The child builds it when its batch ends. The owner
/// keeps it in the run record (durable-child-park-recovery, design D2).
/// </summary>
internal sealed record ChildRunPark
{
    /// <summary>The cap on the size of one park record in bytes (design D4).</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>Every message after the system message, as the child keeps it (M12).</summary>
    public IReadOnlyList<SerializableChatMessage> Transcript { get; init; } = [];

    /// <summary>The loop guard after the finished calls. It is the only copy of the round (M10, H1).</summary>
    public required TurnLoopGuardState LoopGuard { get; init; }

    /// <summary>The project directory and the inherited working directory that the batch ran with (H3).</summary>
    public required ChildParkScopeDirectories ScopeDirectories { get; init; }

    /// <summary>
    /// The file activity of the child after the finished calls: the project directory, the read files, and the changed
    /// files. A finished <c>set_working_directory</c> call of the batch sets this project directory.
    /// </summary>
    public required WorkingContextDelta FileActivity { get; init; }

    /// <summary>The skill overlay of the spawn, or null. The agent profile does not hold it.</summary>
    public string? SkillOverlay { get; init; }
}

/// <summary>One journaled consent request of a parked run, and its answer when the journal holds one (design D2).</summary>
internal sealed record ChildParkRequest(SessionProtocol.ToolApprovalRequested Request, SessionProtocol.ToolApprovalResolved? Resolution);

/// <summary>The two working directories of the batch that parked (design D2).</summary>
internal sealed record ChildParkScopeDirectories(string? ProjectDirectory, string? InheritedWorkingDirectory);

/// <summary>
/// The child tells its owner that its batch parked (design D1). The sender is the child. The requests travel beside the
/// park: one consent request for each parked call, as the gate made it.
/// </summary>
internal sealed record ChildParkReached(SubAgentRunId RunId, ChildRunPark Park, IReadOnlyList<ParentApprovalRequest> Requests)
    : INoSerializationVerificationNeeded;

/// <summary>
/// The journal rejected an event of a child run (design D3). The owner records the end of the run only when the run
/// still has the lifecycle that the event expected (<see cref="ChildParkRules.RejectedEventEnds"/>).
/// </summary>
internal sealed record ChildEventRejected(SubAgentRunId RunId, ChildRejectedEvent Kind) : INoSerializationVerificationNeeded;

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

/// <summary>What recovery does with a child run (PR 2 design D6, F3).</summary>
internal enum ChildRecoveryAction
{
    /// <summary>The run has nothing to recover.</summary>
    Nothing,

    /// <summary>The run was running at the restart. Record it as lost.</summary>
    Lost,

    /// <summary>The run is parked. Try the resume; the resume gate waits while a request is open.</summary>
    Resume,

    /// <summary>The run ended. Prepare its result.</summary>
    PrepareResult
}

/// <summary>The kind of a child event that the journal rejected (PR 2 design D5).</summary>
internal enum ChildRejectedEvent
{
    Parked,
    Answer,
    Resumed
}

/// <summary>The pure decisions of the park owner. They are the Stryker targets of child-resume-gate.</summary>
internal static class ChildParkRules
{
    // <child-resume-gate>
    // Security boundary: only the owned child of an Accepted run can open prompts. Any other sender gets the stop
    // signal, so a stale actor or a stranger cannot make the owner ask the requester.
    public static ChildParkDecision AcceptPark(bool ownedSender, ChildRunLifecycle? lifecycle) => (ownedSender, lifecycle) switch
    {
        (true, ChildRunLifecycle.Accepted) => ChildParkDecision.Accept,
        (true, ChildRunLifecycle.Cancelling) => ChildParkDecision.EndCancelled,
        _ => ChildParkDecision.StopSender
    };

    // Guard 1: a batch with two parked calls waits for both answers. Guard 2: during a restart drain nothing runs;
    // recovery resumes the run after the restart.
    public static ChildResumeDecision ResumeGate(bool hasPark, int openRequests, bool drainPending)
        => !hasPark ? ChildResumeDecision.Nothing
            : openRequests > 0 || drainPending ? ChildResumeDecision.Wait
            : ChildResumeDecision.Resume;

    // At most once: a run that was running at the restart is lost, because its work is uncertain. A park ran nothing,
    // so recovery tries its resume, and the resume gate waits for its answers.
    public static ChildRecoveryAction RecoveryAction(ChildRunLifecycle lifecycle) => lifecycle switch
    {
        ChildRunLifecycle.Accepted or ChildRunLifecycle.Cancelling => ChildRecoveryAction.Lost,
        ChildRunLifecycle.Parked => ChildRecoveryAction.Resume,
        ChildRunLifecycle.Terminal => ChildRecoveryAction.PrepareResult,
        _ => ChildRecoveryAction.Nothing
    };

    public static bool ParkFits(long parkBytes) => parkBytes <= ChildRunPark.MaxBytes;

    // A parked run with an open request waits for a person. It does not keep the session in memory and does not make a
    // restart reminder. A parked run with every answer waits only for a drain to end, so it counts.
    public static bool CountsAsUnreported(ChildRunLifecycle lifecycle, int openRequests) => lifecycle switch
    {
        ChildRunLifecycle.Parked => openRequests == 0,
        _ => true
    };

    // A rejected record ends the run only in the lifecycle that the record expected. A duplicate answer from the stash
    // can resume the run before the rejection of the first answer arrives; that run keeps going.
    public static bool RejectedEventEnds(ChildRejectedEvent kind, ChildRunLifecycle? lifecycle) => (kind, lifecycle) switch
    {
        (ChildRejectedEvent.Parked, ChildRunLifecycle.Accepted) => true,
        (ChildRejectedEvent.Answer or ChildRejectedEvent.Resumed, ChildRunLifecycle.Parked) => true,
        _ => false
    };

    // The run record holds a child request, so a snapshot keeps it. An open parent request still blocks the snapshot.
    public static bool IgnoredBySnapshot(SessionProtocol.ToolApprovalRequested request) => request.SourceChildRunId is not null;

    // Returns the damage of a loop-guard state, or null when the state can be valid. A damaged state must not resume.
    public static string? LoopGuardDamage(TurnLoopGuardState loopGuard)
        => loopGuard.ToolIterationCount < 0 || loopGuard.ToolCallCount < 0 || loopGuard.RefusedRepeatCount < 0
            ? "a count is negative"
            : loopGuard.RefusedRepeatCount > TurnStateTracker.EscalatedRefusedRepeat + 1
                ? "the refused-repeat count is above the escalation"
                : loopGuard.Episodes.Values.Any(static episode => episode.EqualRounds is < 0 or > 2)
                    ? "an equal-round count is outside 0 to 2"
                    : null;
    // </child-resume-gate>

    // Compile shell for running-child-drain-park task 1.1. It keeps the PR 2 rule for every park.
    public static bool RequesterCanAnswer(int requestCount, bool supportsInteractiveApproval) => supportsInteractiveApproval;
}
