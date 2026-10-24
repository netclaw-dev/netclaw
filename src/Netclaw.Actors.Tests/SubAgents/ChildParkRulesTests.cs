// -----------------------------------------------------------------------
// <copyright file="ChildParkRulesTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// PR 1 and PR 2 of durable-child-consent-wait (issue #2572). The pure decisions of the park owner, as tables. They are
/// also the Stryker targets of child-resume-gate.
/// </summary>
public sealed class ChildParkRulesTests
{
    /// <summary>
    /// U3, design D3 "Refusals". The owner accepts a park only from the owned child of an Accepted run. A cancelling
    /// run ends as cancelled. A sender that the owner does not own gets the stop signal, and nothing is recorded.
    /// Planted bug: a park of a cancelling run shows a prompt, or a stranger can park a run.
    /// </summary>
    [Fact]
    public void Park_acceptance_follows_the_owner_and_the_lifecycle()
    {
        (bool Owned, ChildRunLifecycle Lifecycle, ChildParkDecision Expected)[] table =
        [
            (true, new ChildRunLifecycle.Accepted(null), ChildParkDecision.Accept),
            (true, new ChildRunLifecycle.Cancelling(null), ChildParkDecision.EndCancelled),
            (false, new ChildRunLifecycle.Accepted(null), ChildParkDecision.StopSender)
        ];

        Assert.Equal(table.Select(static row => row.Expected),
            table.Select(static row => ChildParkRules.AcceptPark(row.Owned, row.Lifecycle)));
    }

    /// <summary>
    /// U4, design D3 resume gate. A park waits while a request is open (guard 1) and while a restart drain is pending
    /// (guard 2). It resumes when every request has an answer. A run with no park does nothing.
    /// Planted bug: the first of two answers resumes the run, or an answer during a drain runs the call.
    /// </summary>
    [Fact]
    public void The_resume_gate_waits_for_every_answer_and_for_the_drain()
    {
        (bool HasPark, int Open, bool Drain, ChildResumeDecision Expected)[] table =
        [
            (true, 1, false, ChildResumeDecision.Wait),
            (true, 0, true, ChildResumeDecision.Wait),
            (true, 0, false, ChildResumeDecision.Resume),
            (false, 0, false, ChildResumeDecision.Nothing)
        ];

        Assert.Equal(table.Select(static row => row.Expected),
            table.Select(static row => ChildParkRules.ResumeGate(row.HasPark, row.Open, row.Drain)));
    }

    /// <summary>
    /// PR 2 design D6, F3. Recovery records a running run as lost, keeps a parked run with an open request, resumes a
    /// parked run with all answers, prepares the result of an ended run, and does nothing for a reported run.
    /// Planted bug: recovery records a parked run as lost, or resumes a run with an open request.
    /// </summary>
    [Fact]
    public void Recovery_acts_on_each_lifecycle_by_its_open_requests()
    {
        (ChildRunLifecycle Lifecycle, int Open, ChildRecoveryAction Expected)[] table =
        [
            (new ChildRunLifecycle.Accepted(null), 0, ChildRecoveryAction.Lost),
            (new ChildRunLifecycle.Accepted(null), 1, ChildRecoveryAction.Lost),
            (new ChildRunLifecycle.Cancelling(null), 0, ChildRecoveryAction.Lost),
            (new ChildRunLifecycle.Cancelling(null), 1, ChildRecoveryAction.Lost),
            (Parked(), 0, ChildRecoveryAction.Resume),
            (Parked(), 1, ChildRecoveryAction.Wait),
            (Terminal(), 0, ChildRecoveryAction.PrepareResult),
            (Terminal(), 1, ChildRecoveryAction.PrepareResult),
            (Reported(), 0, ChildRecoveryAction.Nothing),
            (Reported(), 1, ChildRecoveryAction.Nothing)
        ];

        Assert.Equal(table.Select(static row => row.Expected),
            table.Select(static row => ChildParkRules.RecoveryAction(row.Lifecycle, row.Open)));
    }

    /// <summary>
    /// PR 2 design D4. A park of exactly 4 MiB fits. One byte more does not.
    /// Planted bug: the cap is off by one, or the owner never refuses a park.
    /// </summary>
    [Fact]
    public void A_park_fits_up_to_the_cap()
    {
        Assert.Equal([true, false], new long[] { ChildRunPark.MaxBytes, ChildRunPark.MaxBytes + 1 }.Select(ChildParkRules.ParkFits));
        Assert.Equal(4 * 1024 * 1024, ChildRunPark.MaxBytes);
    }

    /// <summary>
    /// PR 2 design D7 and F6. A loop-guard state is damaged when a count is negative, the refused-repeat count is above
    /// the escalation count plus one, an equal-round count is outside 0 to 2, or the round is below the last checkpoint
    /// round. The valid row holds each limit value.
    /// Planted bug: a damaged park resumes and makes the reducer throw at the next checkpoint, or a valid park ends.
    /// </summary>
    [Fact]
    public void A_damaged_loop_guard_is_found_and_a_valid_one_passes()
    {
        var limit = TurnStateTracker.EscalatedRefusedRepeat + 1;
        (string Name, TurnLoopGuardState Guard, int? LastCheckpoint, bool Damaged)[] table =
        [
            ("valid", Guard(round: 3, calls: 4, refused: limit, equalRounds: 2), 3, false),
            ("valid with no checkpoint", Guard(round: 0, calls: 0, refused: 0, equalRounds: 0), null, false),
            ("negative round", Guard(round: -1), null, true),
            ("negative call count", Guard(calls: -1), null, true),
            ("negative refused repeat", Guard(refused: -1), null, true),
            ("refused repeat above the escalation", Guard(refused: limit + 1), null, true),
            ("equal rounds above 2", Guard(equalRounds: 3), null, true),
            ("equal rounds below 0", Guard(equalRounds: -1), null, true),
            ("equal rounds of 7", Guard(equalRounds: 7), null, true),
            ("round below the last checkpoint", Guard(round: 2), 3, true)
        ];

        Assert.Equal(table.Select(static row => (row.Name, row.Damaged)),
            table.Select(static row => (row.Name, ChildParkRules.LoopGuardDamage(row.Guard, row.LastCheckpoint) is not null)));
    }

    /// <summary>
    /// PR 2 design F8 and F9. A parked run with an open request does not count as unreported, so it neither keeps the
    /// session in memory nor makes a restart reminder. A parked run with all answers does, as a running or ended run.
    /// Planted bug: a parked session never passivates, or an answered park gets no restart reminder.
    /// </summary>
    [Fact]
    public void Only_a_parked_run_with_an_open_request_does_not_count_as_unreported()
    {
        (string Name, ChildRunLifecycle Lifecycle, int Open, bool Expected)[] table =
        [
            ("parked open", Parked(), 1, false),
            ("parked answered", Parked(), 0, true),
            ("accepted", new ChildRunLifecycle.Accepted(null), 0, true),
            ("terminal", Terminal(), 0, true),
            ("reported", Reported(), 0, false)
        ];

        Assert.Equal(table.Select(static row => (row.Name, row.Expected)),
            table.Select(static row => (row.Name, ChildParkRules.CountsAsUnreported(row.Lifecycle, row.Open))));
    }

    /// <summary>
    /// PR 2 design D5. A rejected child event ends the run only when the run still has the lifecycle that the event
    /// expected: a park expects Accepted, and an answer and a resume expect Parked. Every other pair keeps the run.
    /// Planted bug: a late rejection ends a run that a duplicate answer resumed (the race of PR 1).
    /// </summary>
    [Fact]
    public void A_rejected_child_event_ends_only_the_lifecycle_that_it_expected()
    {
        (ChildRejectedEvent Kind, Type Lifecycle)[] ends =
        [
            (ChildRejectedEvent.Parked, typeof(ChildRunLifecycle.Accepted)),
            (ChildRejectedEvent.Answer, typeof(ChildRunLifecycle.Parked)),
            (ChildRejectedEvent.Resumed, typeof(ChildRunLifecycle.Parked))
        ];
        ChildRunLifecycle?[] lifecycles =
            [new ChildRunLifecycle.Accepted(null), new ChildRunLifecycle.Cancelling(null), Parked(), Terminal(), Reported(), null];
        var table = Enum.GetValues<ChildRejectedEvent>()
            .SelectMany(kind => lifecycles.Select(lifecycle => (Kind: kind, Lifecycle: lifecycle)))
            .ToArray();

        Assert.Equal(table.Select(row => (row.Kind, row.Lifecycle?.GetType().Name, ends.Contains((row.Kind, row.Lifecycle?.GetType()!)))),
            table.Select(static row => (row.Kind, row.Lifecycle?.GetType().Name, ChildParkRules.RejectedEventEnds(row.Kind, row.Lifecycle))));
    }

    /// <summary>
    /// PR 2 design D4. A routine snapshot ignores a child entry, because the run record holds the park. An open parent
    /// entry still blocks the snapshot.
    /// Planted bug: a child request blocks every snapshot, or a parent approval no longer blocks it.
    /// </summary>
    [Fact]
    public void A_snapshot_ignores_a_child_entry_and_not_a_parent_entry()
    {
        var parent = new ToolApprovalRequested { SessionId = new SessionId("park/snapshot"), CallId = "call-parent", ToolName = "shell_execute" };
        var child = parent with { CallId = "run-1/subagent-approval/1", SourceChildRunId = new SubAgentRunId("run-1") };

        Assert.Equal([true, false], new[] { child, parent }.Select(ChildParkRules.IgnoredBySnapshot));
    }

    private static TurnLoopGuardState Guard(int round = 1, int calls = 1, int refused = 0, int equalRounds = 0) => new(
        round, calls, [], null,
        new Dictionary<ToolRecurrenceKey, ToolRecurrenceEpisode>
        {
            [new ToolRecurrenceKey(new ToolName("authority_probe"), "hash")] = new("ok", equalRounds, false, false)
        },
        refused);

    private static ChildRunLifecycle.Parked Parked() => new(new ChildRunPark
    {
        LoopGuard = Guard(), ScopeDirectories = new ChildParkScopeDirectories(null, null), FileActivity = WorkingContextDelta.Empty
    }, ImmutableArray<ChildParkRequest>.Empty);

    private static ChildRunLifecycle.Terminal Terminal() => new(new ChildRunTerminal(new SubAgentProtocol.SubAgentResult
    {
        AgentName = new AgentName("worker"), Output = "Done.", Completion = new ChildRunCompletion.Completed(WorkingContextDelta.Empty)
    }, Lost: false, EvidenceWarning: null));

    private static ChildRunLifecycle.Reported Reported()
        => new(SubAgentRunOutcome.Completed, null, new ChildRunReport.Notice(new InputId("notice-input")));
}
