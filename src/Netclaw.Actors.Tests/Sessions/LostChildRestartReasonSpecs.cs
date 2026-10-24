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
/// the parent model that the runtime does not start the child again and that its later effects are uncertain.
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
    /// The live case of issue 2602. A child completes one tool round, and the session records a checkpoint. The session
    /// stops with no drain while the child waits for its next model call. A graceful drain parks such a child
    /// (running-child-drain-park), so only a stop with no drain or a missed child wait loses it. The next join recovers
    /// the session, and recovery records the run as lost. The result file names the reason, says that the child
    /// did not survive when its session restarted, says that the runtime does not start the child again, and says that the effects after the
    /// last checkpoint are uncertain.
    /// Planted bug: recovery records the run with the reason <c>actor_stopped</c> and the old text (today).
    /// </summary>
    [Fact]
    public async Task A_child_lost_to_a_restart_names_owner_restarted_in_its_result_file()
    {
        // Arrange
        var sessionId = new SessionId("lost-reason/drain-checkpoint");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, secondCall) = await StartCheckpointedChildAsync(journal, sessionId, subscriber);

        try
        {
            // Act: the session stops while the child runs, and the next join recovers it.
            await RestartSessionAsync(sessionId);

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
            AssertSaysTheSessionRestarted(result, "result.md");
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
    /// says plainly that the child did not survive when its session restarted and that the runtime does not start it again.
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
            AssertSaysTheSessionRestarted(notice, "the notice");
            Assert.Contains("uncertain", notice, StringComparison.OrdinalIgnoreCase);

            // The parent model reads the same reason in its request.
            await AwaitAssertAsync(() => Assert.Contains(Clients.Parent.ReceivedMessages,
                request => request.Any(message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                    && message.Text.Contains(accepted.Run.RunId.Value, StringComparison.Ordinal)
                    && message.Text.Contains($"reason={OwnerRestarted.Value}", StringComparison.Ordinal))),
                Deadline, cancellationToken: Cancellation);

            var result = await File.ReadAllTextAsync(ResultPath(sessionId, accepted), Cancellation);
            Assert.Contains($"- reason: {OwnerRestarted.Value}", result, StringComparison.Ordinal);
            AssertSaysTheSessionRestarted(result, "result.md");
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
    /// Review of the fix (option B). A child that a restart lost has a checkpoint. The notice that the parent model reads
    /// limits the uncertainty to the effects after the last checkpoint, as <c>result.md</c> does. It must not say that all
    /// effects of the child are uncertain, because the checkpoint confirmed the effects before it.
    /// Planted bug: the notice says "Its external effects are uncertain" for each lost run.
    /// </summary>
    [Fact]
    public async Task The_notice_of_a_checkpointed_child_lost_to_a_restart_limits_the_uncertainty_to_after_the_checkpoint()
    {
        // Arrange
        var sessionId = new SessionId("lost-reason/crash-notice-checkpoint");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, secondCall) = await StartCheckpointedChildAsync(journal, sessionId, subscriber);

        try
        {
            // Act
            await RestartSessionAsync(sessionId);

            // Assert
            await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)),
                Deadline, cancellationToken: Cancellation);
            var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId)).UserMessage.Content!;
            Note(notice);
            Assert.True(notice.Contains("effects after the last checkpoint are uncertain", StringComparison.OrdinalIgnoreCase),
                $"The notice of a checkpointed run must say that the effects after the last checkpoint are uncertain. Text: {notice}");
            Assert.False(notice.Contains("effects are uncertain", StringComparison.OrdinalIgnoreCase),
                $"The notice of a checkpointed run must not say that all of its effects are uncertain. Text: {notice}");
        }
        finally
        {
            secondCall.TrySetResult();
        }
    }

    /// <summary>
    /// Review of the fix (option B). A lost run keeps the merge of its checkpoint into the parent working context. The
    /// child confirmed a changed file before the restart. After the notice turn, the next parent model request lists that
    /// file in its recent files.
    /// Planted bug: the session drops the reviewed context of each run that is not a success.
    /// </summary>
    [Fact]
    public async Task A_checkpointed_child_lost_to_a_restart_merges_its_confirmed_changes_into_the_parent()
    {
        // Arrange
        var sessionId = new SessionId("lost-reason/crash-merge");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, secondCall) = await StartCheckpointedChildAsync(journal, sessionId, subscriber);

        try
        {
            // Act: the recovered session runs the notice turn. Then a person sends one more message.
            await RestartSessionAsync(sessionId);
            await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)),
                Deadline, cancellationToken: Cancellation);
            var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId));
            await AwaitAssertAsync(() => Assert.True(ConsumptionCount(journal, notice.InputId) > 0,
                "The recovered session must run the notice turn."), Deadline, cancellationToken: Cancellation);
            await SendAsync(sessionId, "ping", Operator(Alice));
            await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<TurnRecorded>(),
                evt => evt.UserMessage.Content == "ping"), Deadline, cancellationToken: Cancellation);

            // Assert
            Assert.True(notice.ChildNotice?.ReviewedWorkingContext is not null,
                "The notice of a lost run with a checkpoint must carry the reviewed context of the checkpoint.");
            // The ping turn runs after the notice turn, so the last parent request belongs to the ping turn.
            var recentFiles = RecentFilesOf(Clients.Parent.ReceivedMessages.Last());
            Assert.True(recentFiles.Contains(CheckpointedFile),
                $"recent_files: [{string.Join(", ", recentFiles)}]. Expected: {CheckpointedFile}");
        }
        finally
        {
            secondCall.TrySetResult();
        }
    }

    /// <summary>
    /// Review of the fix (option B). The live child actor stops while its owner runs, after one checkpoint. The run is not
    /// a restart, so the stored completion stays a partial run with the reason <c>actor_stopped</c>. The run is lost, so
    /// the session log line must say <c>success=False</c>.
    /// Planted bug: a partial completion reports success for each reason except <c>owner_restarted</c>.
    /// </summary>
    [Fact]
    public async Task A_checkpointed_live_child_that_stops_while_its_owner_runs_is_not_a_success()
    {
        // Arrange
        var sessionId = new SessionId("lost-reason/child-actor-stop-checkpoint");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, secondCall) = await StartCheckpointedChildAsync(journal, sessionId, subscriber);

        try
        {
            // Act
            var childPath = $"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}/subagent-{Agent}-{accepted.Run.RunId.Value}";
            Sys.Stop(await Sys.ActorSelection(childPath).ResolveOne(Deadline, Cancellation));

            // Assert
            var completed = await subscriber.FishForMessageAsync<SubAgentOutput>(
                output => output.Phase == SubAgentPhase.Completed, Deadline, cancellationToken: Cancellation);
            var terminal = await AwaitTerminalAsync(journal, accepted);
            Note($"SubAgentOutput success={completed.Success} outcome={completed.Outcome} reason={completed.OutcomeReason?.Value ?? "-"}");
            Assert.True(terminal.Terminal.Lost);
            var partial = Assert.IsType<ChildRunCompletion.Partial>(terminal.Terminal.Result.Completion);
            Assert.Equal(SubAgentOutcomeReason.ActorStopped, partial.PartialReason);
            Assert.False(completed.Success, "The session log line of a lost run must say success=False.");
            Assert.Equal(SubAgentOutcomeReason.ActorStopped, completed.OutcomeReason);
        }
        finally
        {
            secondCall.TrySetResult();
        }
    }

    /// <summary>
    /// Review of the fix (option B). Versions run in the order new, old, new. The new binary records the lost run as a
    /// partial run with the reason <c>owner_restarted</c>, and the daemon stops before the notice. The old binary reads
    /// that partial run as a success, so it admits a notice that carries a reviewed context. The spec writes those two
    /// records into the journal directly. The new binary must replay them with no error, run the notice turn, and merge
    /// the reviewed context.
    /// Planted bug: the reducer refuses a reviewed context on each run that is not a success, so the recovery throws.
    /// </summary>
    [Fact]
    public async Task A_notice_with_a_reviewed_context_that_an_older_binary_wrote_for_a_lost_run_replays()
    {
        // Arrange: a live child with one checkpoint. The session actor stops with no drain, and nothing recovers it yet.
        var sessionId = new SessionId("lost-reason/cross-version-replay");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, secondCall) = await StartCheckpointedChildAsync(journal, sessionId, subscriber);

        try
        {
            await StopAsync(await ResolveSessionAsync(sessionId));
            var checkpoint = journal.Committed.OfType<ChildRunEvent.Checkpointed>()
                .Last(evt => evt.RunId == accepted.Run.RunId).Checkpoint;
            Assert.Contains(CheckpointedFile, checkpoint.ConfirmedActivity.ConfirmedChangedFiles);

            // The records of the new binary and then of the old binary.
            var lost = new SubAgentProtocol.SubAgentResult
            {
                AgentName = accepted.Run.AgentName,
                RunId = accepted.Run.RunId,
                ScopeId = accepted.Run.ScopeId,
                Completion = new ChildRunCompletion.Partial(OwnerRestarted, checkpoint.ConfirmedActivity),
                Output = $"The child did not survive a restart. {checkpoint.Summary}"
            };
            var terminal = new ChildRunEvent.TerminalRecorded(
                new ChildRunTerminal(lost, Lost: true, EvidenceWarning: "Recovery does not relaunch uncertain child work."))
            {
                SessionId = sessionId, RunId = accepted.Run.RunId, RecordedAtMs = 10
            };
            var oldNotice = new InputAdmitted
            {
                SessionId = sessionId,
                InputId = new InputId("old-binary-notice"),
                UserMessage = new SerializableChatMessage
                {
                    Role = ChatRole.User,
                    Content = $"Child run ended. agent={accepted.Run.AgentName.Value} run={accepted.Run.RunId.Value} "
                              + $"outcome=partial reason={OwnerRestarted.Value} result_file={ResultPath(sessionId, accepted)}"
                },
                TurnContext = accepted.Run.OriginalContext with { TurnId = "old-binary-notice-turn" },
                ChildNotice = new ChildNotice(accepted.Run.RunId, checkpoint.ConfirmedActivity),
                AdmittedAtMs = 11
            };
            await AppendToJournalAsync(sessionId, terminal, oldNotice);

            // Act: a join starts the session, and the session replays the journal.
            var recovered = CreateTestProbe();
            Sys.EventStream.Subscribe(recovered.Ref, typeof(Akka.Event.Error));
            SessionManager.Tell(new JoinSession(recovered) { SessionId = sessionId, Filter = OutputFilter.Full });
            var first = await recovered.FishForMessageAsync<object>(message => message is SessionJoined
                    || (message is Akka.Event.Error error && IsRecoveryFailure(error)),
                ColdRecoveryCeiling, cancellationToken: Cancellation);
            Sys.EventStream.Unsubscribe(recovered.Ref, typeof(Akka.Event.Error));

            // Assert
            if (first is Akka.Event.Error failure)
                Assert.Fail($"The replay of the old notice failed: {failure.Cause?.Message ?? failure.Message}");
            await AwaitAssertAsync(() => Assert.True(ConsumptionCount(journal, oldNotice.InputId) > 0,
                "The recovered session must run the notice turn of the old notice."), Deadline, cancellationToken: Cancellation);
            await SendAsync(sessionId, "ping", Operator(Alice));
            await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<TurnRecorded>(),
                evt => evt.UserMessage.Content == "ping"), Deadline, cancellationToken: Cancellation);
            var recentFiles = RecentFilesOf(Clients.Parent.ReceivedMessages.Last());
            Assert.True(recentFiles.Contains(CheckpointedFile),
                $"recent_files: [{string.Join(", ", recentFiles)}]. Expected: {CheckpointedFile}");
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

    // Says that the session of the child restarted, that the child did not survive, and that the runtime does not start
    // the child again. The words are a contract with the parent model. The model must not read a restart loss as a
    // platform fault. Example wording: "The session of the child restarted (for example, a daemon restart). The child
    // did not survive. The runtime does not start the child again."
    private static void AssertSaysTheSessionRestarted(string text, string where)
    {
        Assert.True(text.Contains("did not survive", StringComparison.OrdinalIgnoreCase)
                    && text.Contains("restarted", StringComparison.OrdinalIgnoreCase),
            $"{where} must say that the session of the child restarted and that the child did not survive. Text: {text}");
        Assert.True(text.Contains("does not start the child again", StringComparison.OrdinalIgnoreCase),
            $"{where} must say that the runtime does not start the child again. Text: {text}");
    }

    // A failure in the replay of the session journal. Akka.Persistence logs it as an error with the cause, and then it
    // stops the actor.
    private static bool IsRecoveryFailure(Akka.Event.Error error)
        => error.Cause is not null
           && (error.Message?.ToString()?.Contains("receiveRecover", StringComparison.OrdinalIgnoreCase) == true
               || error.Cause.ToString().Contains("reviewed working context", StringComparison.Ordinal));

    // The file that the checkpointed child changes in its first round.
    private string CheckpointedFile => Path.Combine(TestPaths.WorkspacesDirectory, "project", "ChangedBeforeRestart.cs");

    // The recent files in the working context block of one parent model request.
    private static string[] RecentFilesOf(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> request)
        => request
            .Where(message => message.Text.Contains("[working-context]", StringComparison.Ordinal))
            .SelectMany(message => message.Text.Split('\n'))
            .Where(line => line.StartsWith("  - ", StringComparison.Ordinal))
            // The volatile block sits in brackets, so its last line ends with the closing bracket.
            .Select(line => line["  - ".Length..].Trim().TrimEnd(']'))
            .ToArray();

    private async Task<ChildRunEvent.TerminalRecorded> AwaitTerminalAsync(AcceptanceFault journal, ChildRunAccepted accepted)
    {
        await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(),
            evt => evt.RunId == accepted.Run.RunId), Deadline, cancellationToken: Cancellation);
        return Assert.Single(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(), evt => evt.RunId == accepted.Run.RunId);
    }

    /// <summary>
    /// Starts a child that changes one file in its first model response. The session records the checkpoint of that
    /// round. The child then waits in its second model call until the caller releases the returned gate.
    /// </summary>
    private async Task<(ChildRunAccepted Accepted, TaskCompletionSource SecondCall)> StartCheckpointedChildAsync(
        AcceptanceFault journal, SessionId sessionId, Akka.TestKit.TestProbe subscriber)
    {
        var (accepted, firstCall) = await StartChildAsync(sessionId, Alice, "one", changedFile: CheckpointedFile);
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
