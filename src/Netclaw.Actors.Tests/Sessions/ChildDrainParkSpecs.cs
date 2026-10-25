// -----------------------------------------------------------------------
// <copyright file="ChildDrainParkSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// running-child-drain-park (issue #2655): a child run that runs at a graceful drain parks at a safe point, and it
/// resumes once after the restart under its recorded authority. Each row checks the drain-park precondition
/// (<see cref="ChildParkTestBase.AssertDrainParkAsync"/>) after the drain acknowledgement, and the postcondition
/// (<see cref="ChildParkTestBase.AssertDrainResumeAsync"/>) after the run ended. The class runs on the virtual
/// scheduler, so a row moves the child wait forward itself.
/// Source: background-subagent-runs and session-resume deltas, design D1 to D7 of running-child-drain-park.
/// </summary>
public sealed class ChildDrainParkSpecs(ITestOutputHelper output) : ChildParkTestBase(output)
{
    private const string BobText = "Bob writes after the restart.";

    protected override bool UseTestScheduler => true;

    /// <summary>
    /// Rows:
    /// <list type="bullet">
    /// <item>batch-end: the drain starts while a held sibling batch runs, and the batch then ends. The child parks with
    /// 0 requests and the result of the call, at round 1. The restart reminder wakes the session, the child resumes
    /// at round 1 with a model call and completes. The sibling ran once.</item>
    /// <item>model-call: the drain starts while the child waits for its first model reply. The child parks at once with
    /// the task message only, and the held reply does not enter the park. After the restart the child resumes with a
    /// model call and completes.</item>
    /// <item>consent: the drain starts during a batch with a sibling call and a consent call. One consent park with 1
    /// request, and no second park. After the restart Alice approves, and the consent call runs once.</item>
    /// <item>automation: a reminder turn with no interactive approval starts the child. The drain park records, the run
    /// does not end, and no prompt shows. The child resumes after the restart and completes.</item>
    /// <item>missed-wait: the sibling stays held past the child wait. One Warning names 1 live child, and the
    /// acknowledgement carries the reminder before the drain deadline. After the restart the run is lost with
    /// owner_restarted, and the sibling ran once. No Parked record.</item>
    /// <item>other-requester: Bob writes after the restart, and his turn is open when the resumed child runs its next
    /// call. That call runs under Alice's recorded authority only.</item>
    /// <item>crash-resumed: the session stops after the Resumed record commits and before the child ends. The next
    /// recovery records the run as lost and starts no child.</item>
    /// <item>missed-wait-expired: as missed-wait, so the restart reminder covers the child run only. The restart takes
    /// longer than the reminder lifetime. A join wakes the session, and the notice turn of the lost child runs. Alice
    /// writes during that turn, and then the expiry signal arrives. The signal has no stopped turn to close: it must not
    /// close Alice's input and must post no restart notice. Alice's input runs (#2659).</item>
    /// </list>
    /// Planted bug: the drain kills a running child, the acknowledgement does not wait for the park record, a parked call
    /// runs twice, a resumed drain park replays its batch, a model reply that came after the park enters the history, an
    /// automation park fails, a stuck child holds the drain past the deadline, the resume takes Bob's authority, or a
    /// crash after the resume record starts the child again.
    /// </summary>
    [Theory]
    [InlineData("batch-end")]
    [InlineData("model-call")]
    [InlineData("consent")]
    [InlineData("automation")]
    [InlineData("missed-wait")]
    [InlineData("other-requester")]
    [InlineData("crash-resumed")]
    [InlineData("missed-wait-expired")]
    public async Task A_running_child_parks_at_a_graceful_drain_and_resumes_after_the_restart(string row)
    {
        // Arrange
        var id = new SessionId($"drain-park/{row}");
        var journal = await JournalAsync();
        ScriptParent();
        var label = $"park-{row}";
        PlanChild(label, row switch
        {
            "consent" => [[Probe("p1"), SiblingCall("s1")]],
            "other-requester" or "crash-resumed" => [[SiblingCall("s1")], [SiblingCall("s2")]],
            _ => [[SiblingCall("s1")]]
        });
        await JoinRecorderAsync(id);
        var firstSibling = row == "model-call" ? Task.CompletedTask : Sibling.HoldNext();
        DrainStart? drain = null;
        var run = await StartAsync(id, label, source: row == "automation" ? Reminder("drain-sweep:1") with { ChannelType = ChannelType.Reminder } : null,
            beforeChildRuns: row != "model-call" ? null : async () =>
            {
                // The child holds the gate, so its first model call waits for the reply when the drain starts.
                await AwaitAssertAsync(() => Assert.Null(Clients.A.NextResponseGate), Deadline, cancellationToken: Cancellation);
                drain = await BeginDrainAsync(id);
            });

        // Act: the drain
        if (row != "model-call")
        {
            await firstSibling.WaitAsync(Deadline, Cancellation);
            drain = await BeginDrainAsync(id);
        }
        if (row is "missed-wait" or "missed-wait-expired")
        {
            Assert.False(drain!.Ack.IsCompleted, "The session acknowledged the drain while a child runs inside the child wait.");
            await EventFilter.Warning(new Regex(@"child_wait_ended live_children=1$")).ExpectAsync(1,
                () =>
                {
                    AdvanceScheduler(SessionExecutionLifetime.ChildWait);
                    return Task.CompletedTask;
                }, Deadline, Cancellation);
        }
        else
            Sibling.Release();
        var park = await AssertDrainParkAsync(journal, id, run, row switch
        {
            "missed-wait" or "missed-wait-expired" => null,
            "consent" => 1,
            _ => 0
        }, drain!);
        var reminder = (await drain!.Ack).RestartReminder;
        Sibling.Release();
        switch (row)
        {
            case "batch-end":
                Assert.Equal(1, park.Park!.Park.LoopGuard.ToolIterationCount);
                Assert.Contains(park.Park.Park.Transcript, message => message.Role == ChatRole.Tool && message.ToolCallId?.Value == "s1");
                break;
            case "model-call":
                Assert.Equal(0, park.Park!.Park.LoopGuard.ToolIterationCount);
                Assert.Equal(ChatRole.User, Assert.Single(park.Park.Park.Transcript).Role);
                break;
            case "automation":
                Assert.Empty(PromptsOf(run.Run.RunId));
                break;
        }

        // Act: the restart
        var crashCalls = 0;
        switch (row)
        {
            case "consent":
                await JoinRecorderAsync(id);
                AssertAck(await AnswerAsync(id, new ToolCallId(Assert.Single(RequestsOf(journal, run.Run.RunId)).CallId), Alice));
                break;
            case "other-requester":
                // The resumed child holds its first model call until Bob's turn is open.
                var childGate = NewGate();
                Clients.A.NextResponseGate = childGate;
                var bobHold = NewGate();
                var bobEntered = ParentGate.Arm(request => FreshUserTexts(request).Contains(BobText), bobHold);
                await SendAsync(id, BobText, OperatorWithTargets(Bob, BobTarget));
                await bobEntered.WaitAsync(Deadline, Cancellation);
                await JoinRecorderAsync(id);
                childGate.TrySetResult();
                await AwaitAssertAsync(() => Assert.Contains(Sibling.Calls, static call => call.CallId == "s2"), Deadline,
                    cancellationToken: Cancellation);
                bobHold.TrySetResult();
                break;
            case "missed-wait-expired":
                await WriteDuringTheLostNoticeTurnThenExpireAsync(journal, id, run, reminder!);
                break;
            case "crash-resumed":
                var secondRound = Sibling.HoldNext();
                AssertAck(await SendRestartReminderAsync(id, reminder!));
                await secondRound.WaitAsync(Deadline, Cancellation);
                await StopAsync(await ResolveSessionAsync(id));
                crashCalls = Clients.A.CallCount;
                Sibling.Release();
                await JoinRecorderAsync(id);
                break;
            default:
                AssertAck(await SendRestartReminderAsync(id, reminder!));
                await JoinRecorderAsync(id);
                break;
        }

        // Assert
        var terminal = await AssertDrainResumeAsync(journal, id, run, park, probeRuns: row == "consent" ? 1 : 0);
        Assert.Equal(row is "other-requester" or "crash-resumed" ? new[] { "s1", "s2" } : ["s1"],
            Sibling.Calls.Select(static call => call.CallId).ToArray());
        switch (row)
        {
            case "missed-wait" or "missed-wait-expired" or "crash-resumed":
                Assert.True(terminal.Lost);
                Assert.Equal(SubAgentOutcomeReason.OwnerRestarted, terminal.Result.OutcomeReason);
                if (row == "crash-resumed")
                    Assert.Equal(crashCalls, Clients.A.CallCount);
                break;
            default:
                Assert.True(terminal.Result.Success, terminal.Result.Output);
                break;
        }
        if (row == "consent")
            Assert.Single(journal.Committed.OfType<ChildRunEvent.Parked>(), evt => evt.RunId == run.Run.RunId);
        if (row == "automation")
            Assert.Empty(PromptsOf(run.Run.RunId));
    }

    /// <summary>
    /// The drain acknowledgement comes from the journal callback of the last live child record (design D4), during
    /// the child wait. No row moves the virtual clock, so the child wait never ends. Rows:
    /// <list type="bullet">
    /// <item>park-too-large: the child changes a file, and its drain park is above the size cap. The refused park
    /// ends the run as a restart loss: Partial with owner_restarted, the file activity of the park, and the
    /// "effects uncertain" text of a lost run. The park did not ask for consent, so "The parked calls did not run."
    /// is false.</item>
    /// <item>cancelled: Alice cancels the run during the child wait. The run ends cancelled, and the terminal record
    /// releases the drain.</item>
    /// <item>held-park: the journal holds the write of the drain park. The session does not acknowledge while the
    /// write is held.</item>
    /// <item>park-rejected: the journal rejects the write of the drain park. The run ends as a restart loss, as a
    /// refused drain park does (design R6): Partial with owner_restarted and the "effects uncertain" text (#2659 gap B).
    /// The row checks the outcome and not the file activity, because its child changes no file.</item>
    /// </list>
    /// Planted bug: the terminal record does not continue the drain (it waits for the child wait), the session
    /// acknowledges before the park record commits, or a refused drain park says that nothing ran.
    /// </summary>
    [Theory]
    [InlineData("park-too-large")]
    [InlineData("cancelled")]
    [InlineData("held-park")]
    [InlineData("park-rejected")]
    public async Task A_drain_acknowledges_from_the_journal_callback_of_the_last_child_record(string row)
    {
        // Arrange: the drain starts while the child runs a held sibling batch.
        var id = new SessionId($"drain-park/ack-{row}");
        var journal = await JournalAsync();
        ScriptParent();
        var label = $"park-ack-{row}";
        var changed = Path.Combine(TestPaths.WorkspacesDirectory, "changed.txt");
        PlanChild(label, row == "park-too-large"
            ? [[new TextContent(new string('x', ChildRunPark.MaxBytes + 1)), SiblingCall("s1"), ChangeFile("f1", changed)]]
            : [[SiblingCall("s1")]]);
        await JoinRecorderAsync(id);
        var sibling = Sibling.HoldNext();
        var run = await StartAsync(id, label);
        await sibling.WaitAsync(Deadline, Cancellation);
        var session = await ResolveSessionAsync(id);
        var stopped = CreateTestProbe();
        stopped.Watch(session);
        var reply = new ReplyRecorder((ExtendedActorSystem)Sys);
        SessionManager.Tell(new PrepareForDaemonRestart(id, "daemon-stop"), reply);
        await AwaitAssertAsync(async () => Assert.Equal("Passivating", await PhaseOfAsync(id)), Deadline,
            cancellationToken: Cancellation);

        // Act: the child record during the child wait
        switch (row)
        {
            case "cancelled":
                await session.Ask<string>(new ChildRunControl(run.Run.RunId, true, run.Run.OriginalContext), Deadline, Cancellation);
                Sibling.Release();
                break;
            case "park-rejected":
                journal.RejectWritesOf(static evt => evt is ChildRunEvent.Parked);
                Sibling.Release();
                break;
            case "held-park":
                journal.HoldNext(static evt => evt is ChildRunEvent.Parked);
                Sibling.Release();
                await journal.HoldEntered.Task.WaitAsync(Deadline, Cancellation);
                Assert.False(reply.Ack.Task.IsCompleted, "The session acknowledged the drain before the park record committed.");
                journal.ReleaseHold.TrySetResult();
                break;
            default:
                Sibling.Release();
                break;
        }

        // Assert: the acknowledgement follows the committed record of the child.
        var ack = await reply.Ack.Task.WaitAsync(Deadline, Cancellation);
        Assert.NotNull(ack.RestartReminder);
        Assert.Equal(row == "held-park" ? 1 : 0, journal.Committed.OfType<ChildRunEvent.Parked>().Count(evt => evt.RunId == run.Run.RunId));
        Assert.Equal(row == "held-park" ? 0 : 1, TerminalsOf(journal, run.Run.RunId).Count);
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
        AssertAck(await SendRestartReminderAsync(id, ack.RestartReminder));
        await JoinRecorderAsync(id);
        var terminal = await AssertRunInvariantsAsync(journal, id, run, probeRuns: 0);
        switch (row)
        {
            case "park-too-large" or "park-rejected":
                Assert.Equal(SubAgentRunOutcome.Partial, terminal.Result.Outcome);
                Assert.Equal(SubAgentOutcomeReason.OwnerRestarted, terminal.Result.OutcomeReason);
                if (row == "park-too-large")
                    Assert.Contains(changed, terminal.Result.WorkingContext?.ConfirmedChangedFiles ?? []);
                Assert.Contains(ChildRunDocuments.RestartLoss, terminal.Result.Output, StringComparison.Ordinal);
                Assert.Contains("uncertain", terminal.Result.Output, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("The parked calls did not run.", terminal.Result.Output, StringComparison.Ordinal);
                break;
            case "cancelled":
                Assert.IsType<ChildRunCompletion.Cancelled>(terminal.Result.Completion);
                break;
            default:
                Assert.True(terminal.Result.Success, terminal.Result.Output);
                break;
        }
    }

    // The join wakes the session, and recovery records the run as lost. Its notice turn holds its model call while Alice
    // writes, and the expiry signal arrives. Alice's input must stay open and run after the notice turn.
    private async Task WriteDuringTheLostNoticeTurnThenExpireAsync(AcceptanceFault journal, SessionId id, ChildRunAccepted run,
        Netclaw.Actors.Reminders.ReminderDefinition reminder)
    {
        const string aliceText = "Alice writes during the notice turn.";
        var noticeHold = NewGate();
        var noticeEntered = ParentGate.Arm(request => FreshUserTexts(request).Any(text =>
            text.Contains(run.Run.RunId.Value, StringComparison.Ordinal)), noticeHold);
        await JoinRecorderAsync(id);
        await noticeEntered.WaitAsync(Deadline, Cancellation);
        await SendAsync(id, aliceText, AliceSource);
        var reply = await SendRestartReminderAsync(id, reminder with { Instructions = RestartExpiredInstruction });
        Assert.True(reply is RestartResumeNotNeeded, $"The expiry signal must resume nothing. The session replied {reply}.");
        await BarrierAsync(id, run);
        var alice = Assert.Single(journal.Committed.OfType<InputAdmitted>(), input => input.UserMessage.Content == aliceText);
        Assert.False(journal.Committed.OfType<InputClosed>().Any(close => close.InputIds.Contains(alice.InputId)),
            "The expiry signal of a child-only reminder closed Alice's new input.");
        Assert.DoesNotContain(Outputs<TextOutput>(), output => output.Text == RestartNoticeRuleTests.ExpectedNotice("daemon-stop")
                                                             || output.Text == RestartNoticeRuleTests.ExpectedNotice("config-reload"));
        noticeHold.TrySetResult();
        await AwaitAssertAsync(() => Assert.True(TurnRuns(journal, alice.InputId) > 0, "Alice's input did not run in a turn."),
            Deadline, cancellationToken: Cancellation);
    }

    // Records the drain reply in Tell, on the thread of the sender. A TestProbe takes a reply through its own mailbox,
    // so it cannot show that the reply came before a held journal write.
    private sealed class ReplyRecorder(ExtendedActorSystem system) : MinimalActorRef
    {
        public TaskCompletionSource<DaemonRestartPrepared> Ack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ActorPath Path { get; } = system.Provider.TempPath();
        public override IActorRefProvider Provider => system.Provider;

        protected override void TellInternal(object message, IActorRef sender)
        {
            if (message is DaemonRestartPrepared prepared)
                Ack.TrySetResult(prepared);
        }
    }
}
