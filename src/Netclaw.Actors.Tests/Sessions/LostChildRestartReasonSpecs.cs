// -----------------------------------------------------------------------
// <copyright file="LostChildRestartReasonSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable specs for issue 2602. A child run is live when its session owner stops. When the owner starts again,
/// recovery records the run as lost. The lost record must name the cause, must not count as a success, and must tell
/// the parent model that nothing re-runs the child and that its later effects are uncertain.
/// <para>
/// Recovery cannot tell a daemon restart drain from a daemon crash or from a stop of the session actor in a live
/// daemon. The drain keeps its restart facts in actor-local state and in the reminder store, and the journal of the
/// session holds none of them. Thus the reason names only the fact that recovery knows: the owner started again, and
/// the child did not survive. The reason is <c>owner_restarted</c>.
/// </para>
/// <para>
/// A live child actor that stops while its owner runs is not a restart. That run keeps <c>actor_stopped</c>. A stop
/// that the child or the parent decides keeps its own reason.
/// </para>
/// </summary>
public sealed class LostChildRestartReasonSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    private const string Agent = "child-a";

    /// <summary>The reason that recovery records for a child run that was live when its owner started again.</summary>
    private static readonly SubAgentOutcomeReason OwnerRestarted = new("owner_restarted");

    /// <summary>
    /// The live case of issue 2602. A child completes one tool round, and the session records a checkpoint. A daemon
    /// restart drain stops the session while the child waits for its next model call. After the restart the reminder
    /// wakes the session, and recovery records the run as lost. The result file names the reason, says that the child
    /// did not survive a restart of its owner, says that nothing re-runs the child, and says that the effects after the
    /// last checkpoint are uncertain.
    /// Planted bug: recovery records the run with the reason <c>actor_stopped</c> and the old text (today).
    /// </summary>
    [Fact]
    public async Task A_child_lost_to_a_restart_drain_names_owner_restarted_in_its_result_file()
    {
        // Arrange
        var sessionId = new SessionId("lost-reason/drain-checkpoint");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, secondCall) = await StartCheckpointedChildAsync(journal, sessionId, subscriber);

        try
        {
            // Act: the daemon drains the session while the child runs, and the reminder wakes it after the restart.
            await DrainAndWakeAsync(sessionId);

            // Assert
            var terminal = await AwaitTerminalAsync(journal, accepted);
            await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)),
                Deadline, cancellationToken: Cancellation);
            Assert.True(terminal.Terminal.Lost, "Recovery must record the live run as lost.");
            Assert.Equal(OwnerRestarted, terminal.Terminal.Result.OutcomeReason);

            var result = await File.ReadAllTextAsync(ResultPath(sessionId, accepted), Cancellation);
            Note(result);
            Assert.Contains($"- reason: {OwnerRestarted.Value}", result, StringComparison.Ordinal);
            Assert.DoesNotContain(SubAgentOutcomeReason.ActorStopped.Value, result, StringComparison.Ordinal);
            AssertSaysTheOwnerRestarted(result, "result.md");
            Assert.Contains("after the last checkpoint are uncertain", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            secondCall.TrySetResult();
        }
    }

    /// <summary>
    /// The session actor stops with no drain while a child waits in its first model call. No checkpoint exists. The next
    /// join recovers the session. The notice that the parent model reads names the same reason as the result file, and it
    /// says plainly that the child did not survive a restart of its owner and that nothing re-runs it.
    /// Planted bug: the notice carries <c>reason=actor_stopped</c> and no text about the restart (today).
    /// </summary>
    [Fact]
    public async Task The_notice_of_a_child_lost_to_a_restart_names_owner_restarted_for_the_parent_model()
    {
        // Arrange
        var sessionId = new SessionId("lost-reason/crash-notice");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);

        try
        {
            // Act
            await RestartSessionAsync(sessionId);

            // Assert
            await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)),
                Deadline, cancellationToken: Cancellation);
            var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId)).UserMessage.Content!;
            Note(notice);
            Assert.Contains($"reason={OwnerRestarted.Value}", notice, StringComparison.Ordinal);
            Assert.DoesNotContain(SubAgentOutcomeReason.ActorStopped.Value, notice, StringComparison.Ordinal);
            AssertSaysTheOwnerRestarted(notice, "the notice");
            Assert.Contains("uncertain", notice, StringComparison.OrdinalIgnoreCase);

            // The parent model reads the same reason in its request.
            await AwaitAssertAsync(() => Assert.Contains(Clients.Parent.ReceivedMessages,
                request => request.Any(message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                    && message.Text.Contains(accepted.Run.RunId.Value, StringComparison.Ordinal)
                    && message.Text.Contains($"reason={OwnerRestarted.Value}", StringComparison.Ordinal))),
                Deadline, cancellationToken: Cancellation);

            var result = await File.ReadAllTextAsync(ResultPath(sessionId, accepted), Cancellation);
            Assert.Contains($"- reason: {OwnerRestarted.Value}", result, StringComparison.Ordinal);
            AssertSaysTheOwnerRestarted(result, "result.md");
            Assert.Contains("uncertain", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    /// <summary>
    /// A run that a restart lost is not a success, also when it has a checkpoint. The session log line
    /// <c>SubAgent completed: ... success=...</c> prints <see cref="SubAgentOutput.Success"/>, and the session copies that
    /// value from the terminal record. The spec reads both.
    /// Planted bug: a lost run with a checkpoint becomes a partial run, and a partial run reports success (today).
    /// </summary>
    [Fact]
    public async Task A_checkpointed_child_lost_to_a_restart_is_not_a_success()
    {
        // Arrange
        var sessionId = new SessionId("lost-reason/crash-success");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, secondCall) = await StartCheckpointedChildAsync(journal, sessionId, subscriber);

        try
        {
            // Act
            var recovered = await RestartSessionAsync(sessionId);

            // Assert
            var completed = await recovered.FishForMessageAsync<SubAgentOutput>(
                output => output.Phase == SubAgentPhase.Completed, Deadline, cancellationToken: Cancellation);
            var terminal = await AwaitTerminalAsync(journal, accepted);
            Note($"SubAgentOutput success={completed.Success} outcome={completed.Outcome} reason={completed.OutcomeReason?.Value ?? "-"}");
            Assert.True(terminal.Terminal.Lost, "Recovery must record the live run as lost.");
            Assert.False(completed.Success, "The session log line of a lost run must say success=False.");
            Assert.False(terminal.Terminal.Result.Success, "The terminal record of a lost run must not be a success.");
            Assert.Equal(OwnerRestarted, terminal.Terminal.Result.OutcomeReason);
            Assert.Equal(OwnerRestarted, completed.OutcomeReason);
        }
        finally
        {
            secondCall.TrySetResult();
        }
    }

    /// <summary>
    /// Guard. A child that ends normally stays a success with no reason. Its notice and its result file name no lost
    /// reason.
    /// </summary>
    [Fact]
    public async Task A_child_that_ends_normally_stays_a_success_with_no_reason()
    {
        // Arrange
        var sessionId = new SessionId("lost-reason/normal-end");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);

        // Act
        gate.TrySetResult();

        // Assert
        var completed = await subscriber.FishForMessageAsync<SubAgentOutput>(
            output => output.Phase == SubAgentPhase.Completed, Deadline, cancellationToken: Cancellation);
        var terminal = await AwaitTerminalAsync(journal, accepted);
        await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)),
            Deadline, cancellationToken: Cancellation);
        Assert.False(terminal.Terminal.Lost);
        Assert.True(terminal.Terminal.Result.Success);
        Assert.Equal(SubAgentRunOutcome.Completed, terminal.Terminal.Result.Outcome);
        Assert.Null(terminal.Terminal.Result.OutcomeReason);
        Assert.True(completed.Success, "The session log line of a completed run must say success=True.");
        Assert.Null(completed.OutcomeReason);
        var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId)).UserMessage.Content!;
        Assert.Contains("outcome=completed", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=", notice, StringComparison.Ordinal);
        Assert.DoesNotContain(OwnerRestarted.Value, await File.ReadAllTextAsync(ResultPath(sessionId, accepted), Cancellation),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Guard. A child ends normally, and the session records its terminal result. The actor stops before the notice. The
    /// recovery prepares the result of that ended run. Recovery changes only a run that was live, so this run keeps its
    /// success and its outcome.
    /// </summary>
    [Fact]
    public async Task Recovery_keeps_the_outcome_of_a_run_that_ended_before_the_owner_stopped()
    {
        // Arrange: the preparation of the result is held, so the journal ends at TerminalRecorded.
        var sessionId = new SessionId("lost-reason/ended-before-stop");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        // The spec never releases this hold. The recovered session writes its own result file. A released write of the
        // stopped session could land after the cleanup of the temp folder and create the folder again.
        Preparation.HoldNext();
        gate.TrySetResult();
        await Preparation.Entered.Task.WaitAsync(Deadline, Cancellation);
        Assert.Empty(NoticesFor(journal, accepted.Run.RunId));

        // Act
        await RestartSessionAsync(sessionId);

        // Assert
        await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)),
            Deadline, cancellationToken: Cancellation);
        var terminal = Assert.Single(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(),
            evt => evt.RunId == accepted.Run.RunId);
        Assert.False(terminal.Terminal.Lost);
        Assert.True(terminal.Terminal.Result.Success);
        var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId)).UserMessage.Content!;
        Assert.Contains("outcome=completed", notice, StringComparison.Ordinal);
        Assert.DoesNotContain(OwnerRestarted.Value, notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guard. The live child actor stops while its owner runs. That is not a restart of the owner, so the run keeps the
    /// reason <c>actor_stopped</c>, and its result file does not name a restart reason.
    /// </summary>
    [Fact]
    public async Task A_live_child_actor_that_stops_while_its_owner_runs_keeps_actor_stopped()
    {
        // Arrange
        var sessionId = new SessionId("lost-reason/child-actor-stop");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);

        try
        {
            // Act
            var childPath = $"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}/subagent-{Agent}-{accepted.Run.RunId.Value}";
            Sys.Stop(await Sys.ActorSelection(childPath).ResolveOne(Deadline, Cancellation));

            // Assert
            var terminal = await AwaitTerminalAsync(journal, accepted);
            await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)),
                Deadline, cancellationToken: Cancellation);
            Assert.True(terminal.Terminal.Lost);
            Assert.False(terminal.Terminal.Result.Success);
            Assert.Equal(SubAgentOutcomeReason.ActorStopped, terminal.Terminal.Result.OutcomeReason);
            var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId)).UserMessage.Content!;
            Assert.Contains($"reason={SubAgentOutcomeReason.ActorStopped.Value}", notice, StringComparison.Ordinal);
            Assert.DoesNotContain(OwnerRestarted.Value,
                await File.ReadAllTextAsync(ResultPath(sessionId, accepted), Cancellation), StringComparison.Ordinal);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    /// <summary>
    /// Guard. The parent cancels a live child. The run keeps the reason <c>cancelled_by_parent</c>, and it is not lost.
    /// </summary>
    [Fact]
    public async Task A_parent_cancel_keeps_cancelled_by_parent()
    {
        // Arrange
        var sessionId = new SessionId("lost-reason/parent-cancel");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);

        try
        {
            // Act
            var session = await ResolveSessionAsync(sessionId);
            await session.Ask<string>(new ChildRunControl(accepted.Run.RunId, true, accepted.Run.OriginalContext),
                Deadline, Cancellation);

            // Assert
            var terminal = await AwaitTerminalAsync(journal, accepted);
            await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)),
                Deadline, cancellationToken: Cancellation);
            Assert.False(terminal.Terminal.Lost);
            Assert.False(terminal.Terminal.Result.Success);
            Assert.Equal(SubAgentOutcomeReason.CancelledByParent, terminal.Terminal.Result.OutcomeReason);
            var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId)).UserMessage.Content!;
            Assert.Contains($"reason={SubAgentOutcomeReason.CancelledByParent.Value}", notice, StringComparison.Ordinal);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    // Says that the owner of the child restarted, that the child did not survive, and that nothing re-runs the child.
    // The words are a contract with the parent model. The model must not read a restart loss as a platform fault.
    private static void AssertSaysTheOwnerRestarted(string text, string where)
    {
        Assert.True(text.Contains("did not survive", StringComparison.OrdinalIgnoreCase)
                    && text.Contains("restart", StringComparison.OrdinalIgnoreCase),
            $"{where} must say that the child did not survive a restart of its owner. Text: {text}");
        Assert.True(text.Contains("nothing re-runs", StringComparison.OrdinalIgnoreCase),
            $"{where} must say that nothing re-runs the child. Text: {text}");
    }

    private async Task<ChildRunEvent.TerminalRecorded> AwaitTerminalAsync(AcceptanceFault journal, ChildRunAccepted accepted)
    {
        await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(),
            evt => evt.RunId == accepted.Run.RunId), Deadline, cancellationToken: Cancellation);
        return Assert.Single(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(), evt => evt.RunId == accepted.Run.RunId);
    }

    // The daemon drains the session, and the session stops. After the restart the reminder system sends the restart
    // reminder, and the reminder wakes the session.
    private async Task DrainAndWakeAsync(SessionId sessionId)
    {
        var session = await ResolveSessionAsync(sessionId);
        var stopped = CreateTestProbe();
        stopped.Watch(session);
        var drained = await AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(sessionId, "daemon-stop"));
        var reminder = drained.RestartReminder;
        if (reminder is null)
            Assert.Fail("The drain returned no restart reminder, but a live child needs one.");
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
        AssertAck(await SendRestartReminderAsync(sessionId, reminder));
    }

    /// <summary>
    /// Starts a child that changes one file in its first model response. The session records the checkpoint of that
    /// round. The child then waits in its second model call until the caller releases the returned gate.
    /// </summary>
    private async Task<(ChildRunAccepted Accepted, TaskCompletionSource SecondCall)> StartCheckpointedChildAsync(
        AcceptanceFault journal, SessionId sessionId, Akka.TestKit.TestProbe subscriber)
    {
        var changed = Path.Combine(TestPaths.WorkspacesDirectory, "project", "ChangedBeforeRestart.cs");
        var (accepted, firstCall) = await StartChildAsync(sessionId, Alice, "one", changedFile: changed);
        await ExpectTurnsAsync(subscriber, 1);

        // The first call took its gate. Only then can the second call get a gate of its own.
        await AwaitAssertAsync(() => Assert.Null(Clients.A.NextResponseGate), Deadline, cancellationToken: Cancellation);
        var secondCall = NewGate();
        Clients.A.NextResponseGate = secondCall;
        firstCall.TrySetResult();

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(journal.Committed.OfType<ChildRunEvent.Checkpointed>(), evt => evt.RunId == accepted.Run.RunId);
            Assert.Equal(2, Clients.A.CallCount);
            Assert.Null(Clients.A.NextResponseGate);
        }, Deadline, cancellationToken: Cancellation);
        Assert.DoesNotContain(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(), evt => evt.RunId == accepted.Run.RunId);
        return (accepted, secondCall);
    }
}

/// <summary>
/// Guard for issue 2602 on the virtual scheduler. The child watchdog stops a child that makes no progress. The child
/// decides that stop, and the owner does not restart, so the run keeps its timeout reason.
/// </summary>
public sealed class LostChildWatchdogReasonSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    protected override bool UseTestScheduler => true;

    [Fact]
    public async Task A_child_watchdog_timeout_keeps_its_timeout_reason()
    {
        // Arrange
        var sessionId = new SessionId("lost-reason/watchdog");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);

        try
        {
            // Act: the virtual clock passes every budget of the child model call.
            await AwaitAssertAsync(() =>
            {
                AdvanceScheduler(TimeSpan.FromHours(1));
                Assert.Contains(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(), evt => evt.RunId == accepted.Run.RunId);
            }, Deadline, cancellationToken: Cancellation);

            // Assert
            var terminal = Assert.Single(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(),
                evt => evt.RunId == accepted.Run.RunId);
            Note($"watchdog reason={terminal.Terminal.Result.OutcomeReason?.Value ?? "-"}");
            Assert.False(terminal.Terminal.Lost);
            Assert.False(terminal.Terminal.Result.Success);
            Assert.Contains(terminal.Terminal.Result.OutcomeReason, new SubAgentOutcomeReason?[]
            {
                SubAgentOutcomeReason.NoSubstantiveOutputTimeout,
                SubAgentOutcomeReason.NoActivityTimeout,
                SubAgentOutcomeReason.ThinkingOnlyTimeout
            });
        }
        finally
        {
            gate.TrySetResult();
        }
    }
}
