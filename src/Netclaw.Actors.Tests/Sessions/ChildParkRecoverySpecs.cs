// -----------------------------------------------------------------------
// <copyright file="ChildParkRecoverySpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// PR 2 of durable-child-consent-wait (durable-child-park-recovery, issue #2572): the child consent pause across a
/// restart (S5), the close of a continuation input on each admission path (#2651), and the recovery log of the parked
/// runs. A row that parks a child checks the park precondition first. Before PR 2 the park is actor-local, so such a
/// row fails there. These rows need the child fixture, so they are here and not in <see cref="RestartDrainSpecs"/>.
/// </summary>
public sealed class ChildParkRecoverySpecs(ITestOutputHelper output) : ChildParkTestBase(output)
{
    private const string AliceText = "Alice interrupted work.";
    private const string BatchCall = "turn-batch";

    // The drain waits for the model grace period and for the child wait (running-child-drain-park). The virtual clock
    // moves both forward, so no row waits for them in real time.
    protected override bool UseTestScheduler => true;

    /// <summary>
    /// S5, the pause across a restart. A child of Alice parks, so its open request pauses the session. Rows:
    /// <list type="bullet">
    /// <item>stashed-fire: the session crashes, and a fire wakes it. The fire waits in the stash during recovery.</item>
    /// <item>reminder-held: a fire waits for the pause. After a crash, a restart reminder arrives. It starts no turn
    /// (N1).</item>
    /// <item>notice: the open notice of the park waits in its turn when the session crashes. Recovery admits one new
    /// notice for the open request (#2617).</item>
    /// <item>automation-continuation: a drain stops a reminder turn after its tool batch. The restart reminder starts no
    /// turn for its continuation input while the request is open (#2651).</item>
    /// <item>passivated-fire: fire 1 of reminder M waits for the pause, and the session passivates. Fire 2 of M wakes
    /// the session. It replaces fire 1, so one fire of M runs.</item>
    /// <item>continuation-person and continuation-fire: as automation-continuation, but after the restart Alice writes,
    /// or a later fire of M arrives. That admission closes the continuation input in its own write and posts the
    /// restart notice once (#2651). The continuation never runs, also after the last answer.</item>
    /// <item>continuation-no-reminder: as automation-continuation, but no restart reminder arrives. Recovery does not
    /// queue the continuation, so the approval notice of the last answer closes it in its own write and posts the
    /// restart notice once. The continuation never runs (#2659).</item>
    /// <item>continuation-expired: as automation-continuation, but the expiry signal arrives in place of the restart
    /// reminder. It closes the continuation with one notice and resumes nothing. The child park stays (#2659).</item>
    /// </list>
    /// The shared postcondition: after the restart the prompt is still open and no automated turn runs. After Alice's
    /// answer the child resumes, and each held fire or continuation runs once in a turn. No close takes the place of
    /// that turn.
    /// Planted bug: a restart loses the pause, a stashed fire runs before the held inputs, a restart reminder starts a
    /// held input, or a crash gives no notice or two notices. #2659: recovery or an internal notice lets a continuation
    /// run with no restart reminder, the expiry signal runs or admits anything, or a continuation adds an empty user
    /// message to the model history.
    /// </summary>
    [Theory]
    [InlineData("stashed-fire")]
    [InlineData("reminder-held")]
    [InlineData("notice")]
    [InlineData("automation-continuation")]
    [InlineData("passivated-fire")]
    [InlineData("continuation-person")]
    [InlineData("continuation-fire")]
    [InlineData("continuation-no-reminder")]
    [InlineData("continuation-expired")]
    public async Task A_restart_keeps_the_child_consent_pause(string row)
    {
        // Arrange
        var id = new SessionId($"park/pause-{row}");
        var journal = await JournalAsync();
        ScriptParent();
        PlanChild($"park-{row}", [[Probe("p1")]]);
        await JoinRecorderAsync(id);
        TaskCompletionSource? held = null;
        Task? noticeEntered = null;
        var run = await StartAsync(id, $"park-{row}", beforeChildRuns: row switch
        {
            // The reminder turn runs its tool batch before the child parks. Its next model call waits.
            "automation-continuation" or "continuation-person" or "continuation-fire" or "continuation-no-reminder"
                or "continuation-expired"
                => async () => held = await HoldAfterToolBatchAsync(id, FireText, Fire(Sweep, FireMs)),
            "notice" => () =>
            {
                held = NewGate();
                noticeEntered = ParentGate.Arm(request => FreshUserTexts(request).Any(text =>
                    text.StartsWith(OpenNoticeHeading, StringComparison.Ordinal)), held);
                return Task.CompletedTask;
            },
            _ => null
        });
        var prompt = (await AwaitPromptsAsync(run.Run.RunId, 1))[0];
        await AssertParkedAsync(journal, id, run, 1);

        // Act: the stop of the session
        ReminderDefinition? reminder = null;
        var notices = ConsentNotices(journal, id).Count;
        switch (row)
        {
            case "reminder-held":
                AssertAck(await SendFireAsync(id, FireMs));
                await RestartSessionAsync(id);
                break;
            case "stashed-fire":
                await StopAsync(await ResolveSessionAsync(id));
                AssertAck(await SendFireAsync(id, FireMs));
                break;
            case "notice":
                await noticeEntered!.WaitAsync(Deadline, Cancellation);
                notices = ConsentNotices(journal, id).Count;
                await RestartSessionAsync(id);
                break;
            case "automation-continuation" or "continuation-person" or "continuation-fire" or "continuation-no-reminder"
                or "continuation-expired":
                reminder = await DrainAsync(id);
                break;
            case "passivated-fire":
                AssertAck(await SendFireAsync(id, FireMs));
                await PassivateAsync(id);
                AssertAck(await SendFireAsync(id, FireMs + 60_000));
                break;
        }
        held?.TrySetResult();
        await JoinRecorderAsync(id);
        var requestsAtRestart = Clients.Parent.ReceivedMessages.Count;
        ISessionResponse? reply = row switch
        {
            "reminder-held" => await SendStrayRestartReminderAsync(id),
            "automation-continuation" => await SendRestartReminderAsync(id, reminder!),
            "continuation-expired" => await SendRestartReminderAsync(id, reminder! with { Instructions = RestartExpiredInstruction }),
            _ => null
        };
        if (reply is not null)
            AssertAck(reply);
        if (row == "continuation-expired")
        {
            Assert.True(reply is RestartResumeNotNeeded, $"The expiry signal must resume nothing. The session replied {reply}.");
            Assert.DoesNotContain(journal.Committed.OfType<InputAdmitted>(), input => input.UserMessage.Content == RestartExpiredInstruction);
        }
        var closesContinuation = row is "continuation-person" or "continuation-fire" or "continuation-expired";
        var closedByLastAnswer = row == "continuation-no-reminder";
        if (row == "continuation-person")
            await SendAsync(id, AliceText, AliceSource);
        else if (row == "continuation-fire")
            AssertAck(await SendFireAsync(id, FireMs + 60_000));
        if (closesContinuation)
            await AwaitAssertAsync(() => Assert.Equal(1, Outputs<TextOutput>().Count(output =>
                    output.Text == RestartNoticeRuleTests.ExpectedNotice("daemon-stop")
                    || output.Text == RestartNoticeRuleTests.ExpectedNotice("config-reload"))),
                Deadline, cancellationToken: Cancellation);

        // Assert: the pause came back. The prompt is open, and no automated turn runs.
        await BarrierAsync(id, run);
        Assert.DoesNotContain(Outputs<ToolInteractionClosed>(), closed => closed.CallId == prompt.CallId);
        // A person's turn is allowed while the pause holds, and its history holds the text of the stopped fire turn.
        if (row != "continuation-person")
            Assert.DoesNotContain(Clients.Parent.ReceivedMessages.Skip(requestsAtRestart), CarriesFire);
        if (row == "notice")
        {
            // The notice that waited in its turn closes with no turn, and one new notice lists the open request.
            var pending = ConsentNotices(journal, id)[notices - 1];
            Assert.Equal(1, ConsumptionCount(journal, pending.InputId));
            Assert.Contains(journal.Committed.OfType<InputClosed>(), closed => closed.InputIds.Contains(pending.InputId));
            var after = ConsentNotices(journal, id).Skip(notices).ToArray();
            Assert.StartsWith(OpenNoticeHeading, Assert.Single(after).UserMessage.Content, StringComparison.Ordinal);
        }

        // Act: the last answer
        AssertAck(await AnswerAsync(id, prompt.CallId, Alice));

        // Assert: the child resumes once, and each held fire or continuation runs once.
        var terminal = await AssertRunInvariantsAsync(journal, id, run, probeRuns: 1);
        Assert.True(terminal.Result.Success, terminal.Result.Output);
        if (closedByLastAnswer)
            await AwaitAssertAsync(() =>
            {
                var notices = Outputs<TextOutput>().Count(output =>
                    output.Text == RestartNoticeRuleTests.ExpectedNotice("daemon-stop")
                    || output.Text == RestartNoticeRuleTests.ExpectedNotice("config-reload"));
                Assert.True(notices == 1, $"The last answer posted {notices} restart notices. A continuation that no "
                                          + "restart reminder queued must close with one notice and never run.");
            }, Deadline, cancellationToken: Cancellation);
        // A continuation that a later admission closed never runs. Each other held input runs in a turn.
        bool Runs(InputAdmitted input) => !((closesContinuation || closedByLastAnswer) && SessionInputFlow.IsContinuation(input));
        await AwaitAssertAsync(() =>
        {
            if (row == "passivated-fire")
                Assert.True(HeldInputs(journal, id).Sum(input => TurnRuns(journal, input.InputId)) > 0, "No fire of M ran after the last answer.");
            else
                Assert.All(HeldInputs(journal, id).Where(Runs), input => Assert.True(TurnRuns(journal, input.InputId) > 0,
                    $"The held input {input.InputId} did not run in a turn."));
        }, Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, id);
        var waited = HeldInputs(journal, id);
        if (row == "passivated-fire")
        {
            Assert.Equal(1, waited.Sum(input => TurnRuns(journal, input.InputId)));
            return;
        }
        Assert.All(waited, input =>
        {
            var closes = journal.Committed.OfType<InputClosed>().Count(closed => closed.InputIds.Contains(input.InputId));
            Assert.Equal(Runs(input) ? (1, 0) : (0, 1), (TurnRuns(journal, input.InputId), closes));
        });
        // A continuation input adds no user message to the model history (#2632, #2659).
        Assert.DoesNotContain(Clients.Parent.ReceivedMessages, request => request.Any(static message =>
            message.Role == Microsoft.Extensions.AI.ChatRole.User && string.IsNullOrEmpty(message.Text)));
    }

    /// <summary>
    /// The #2651 contract on the admission paths that need a child. A drain stops Alice's turn after its tool batch and
    /// journals one continuation input. After the restart and before the restart reminder, the session admits a later
    /// input. Rows:
    /// <list type="bullet">
    /// <item>ConsentNoticeBeforeReminder: Alice answers the open parked request, and the session admits the closing
    /// approval notice.</item>
    /// <item>ReplacedFireBeforeReminder: the pause holds fire 1 of reminder M. Fire 2 of M replaces it. The drain writes
    /// a continuation input only when no input is pending, and a fire after the restart closes the continuation on the
    /// ordinary path. So the row writes fire 1 into the journal after the drain, as a copy of an earlier fire of M.</item>
    /// <item>LostChildNoticeBeforeReminder: a child runs at the drain. Recovery records it as lost (owner_restarted) and
    /// admits its result notice. This path is the ordinary admission, so the row can pass before PR 2.</item>
    /// </list>
    /// The shared postcondition: the write that admits the later input also closes the continuation input, the session
    /// posts the restart notice once, the restart reminder later resumes nothing, and the stopped turn never runs.
    /// Planted bug: the notice or the replacing fire is admitted, and the continuation stays pending. The reminder then
    /// runs Alice's stopped turn after the newer input.
    /// </summary>
    [Theory]
    [InlineData("ConsentNoticeBeforeReminder")]
    [InlineData("ReplacedFireBeforeReminder")]
    [InlineData("LostChildNoticeBeforeReminder")]
    public async Task A_later_admission_closes_the_continuation_in_its_own_write(string row)
    {
        // Arrange
        var id = new SessionId($"park/continuation-{row}");
        var journal = await JournalAsync();
        ScriptParent();
        var lostChild = row == "LostChildNoticeBeforeReminder";
        // A child that is lost at the drain runs a tool that waits. Each other child parks.
        PlanChild($"park-{row}", lostChild ? [[SiblingCall("child-tool")]] : [[Probe("p1")]]);
        await JoinRecorderAsync(id);
        var childTool = lostChild ? Sibling.HoldNext() : Task.CompletedTask;
        if (row == "ReplacedFireBeforeReminder")
            AssertAck(await SendFireAsync(id, FireMs - 60_000));
        var run = await StartAsync(id, $"park-{row}");
        ToolInteractionRequest? prompt = null;
        if (lostChild)
            await childTool.WaitAsync(Deadline, Cancellation);
        else
        {
            prompt = (await AwaitPromptsAsync(run.Run.RunId, 1))[0];
            await AssertParkedAsync(journal, id, run, 1);
        }
        var held = await HoldAfterToolBatchAsync(id, AliceText, AliceSource);
        var reminder = await DrainAsync(id);
        held.TrySetResult();
        Sibling.Release();
        var continuation = Assert.Single(journal.Committed.OfType<InputAdmitted>(), input => input.SessionId == id && SessionInputFlow.IsContinuation(input));
        if (row == "ReplacedFireBeforeReminder")
        {
            var earlier = Assert.Single(FireAdmissions(journal, id, FireKey(Sweep, FireMs - 60_000)));
            await AppendToJournalAsync(id, earlier with
            {
                InputId = InputId.New(), SourceMessageId = FireKey(Sweep, FireMs), AdmittedAtMs = earlier.AdmittedAtMs + 1
            });
        }
        var drainEnd = journal.Committed.Count;

        // Act: a later admission before the restart reminder
        await JoinRecorderAsync(id);
        switch (row)
        {
            case "ConsentNoticeBeforeReminder":
                AssertAck(await AnswerAsync(id, prompt!.CallId, Alice));
                break;
            case "ReplacedFireBeforeReminder":
                AssertAck(await SendFireAsync(id, FireMs + 60_000));
                break;
        }
        InputAdmitted? later = null;
        await AwaitAssertAsync(() => later = Assert.Single(journal.Committed.Skip(drainEnd).OfType<InputAdmitted>(), input => row switch
        {
            "ConsentNoticeBeforeReminder" => input.TurnContext.SourceKind == NoticeKind,
            "ReplacedFireBeforeReminder" => input.SourceMessageId == FireKey(Sweep, FireMs + 60_000),
            _ => input.ChildNotice?.RunId == run.Run.RunId
        }), Deadline, cancellationToken: Cancellation);

        // Assert
        var events = journal.Committed.ToArray();
        var admittedAt = Array.FindIndex(events, evt => evt is InputAdmitted admitted && admitted.InputId == later!.InputId);
        var close = admittedAt > 0 ? events[admittedAt - 1] as InputClosed : null;
        Assert.True(close?.InputIds.Contains(continuation.InputId) == true,
            "The write that admits the later input must close the continuation input.");
        if (row == "ReplacedFireBeforeReminder")
            Assert.Contains(Assert.Single(FireAdmissions(journal, id, FireKey(Sweep, FireMs))).InputId, close!.InputIds);
        await AwaitAssertAsync(() => Assert.Equal(1, Outputs<TextOutput>().Count(output =>
                output.Text == RestartNoticeRuleTests.ExpectedNotice("daemon-stop")
                || output.Text == RestartNoticeRuleTests.ExpectedNotice("config-reload"))),
            Deadline, cancellationToken: Cancellation);
        Assert.IsType<RestartResumeNotNeeded>(await SendRestartReminderAsync(id, reminder));
        await BarrierAsync(id);
        Assert.DoesNotContain(journal.Committed.ToArray(), evt => evt is TurnRecorded turn && turn.ConsumedInputIds.Contains(continuation.InputId)
                                                                 || evt is ToolBatchStarted batch && batch.ConsumedInputIds.Contains(continuation.InputId));
    }

    /// <summary>
    /// Recovery logs one line with the count and the total size of the parked runs (PR 2 design D4), and the line holds
    /// no park content. Rows: two children of Alice park; one child ends before the crash and no run is parked, so
    /// recovery writes no park line.
    /// Planted bug: recovery logs nothing, logs one line for each park, logs park content, or logs "count=0".
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Recovery_logs_the_count_and_bytes_of_the_parked_runs(int parks)
    {
        // Arrange
        var id = new SessionId($"park/log-{parks}");
        var journal = await JournalAsync();
        ScriptParent();
        PlanChild("park-log-a", parks == 0 ? [] : [[Probe("p1")]]);
        PlanChild("park-log-b", [[Probe("p1")]]);
        await JoinRecorderAsync(id);
        if (parks == 0)
        {
            var ended = await StartAsync(id, "park-log-a");
            await AwaitAssertAsync(() => Assert.NotEmpty(TerminalsOf(journal, ended.Run.RunId)), Deadline, cancellationToken: Cancellation);
            await SettleNoticesAsync(journal, id);
        }
        else
        {
            ChildRunAccepted? second = null;
            // Both children start before the first park, because the pause refuses a new child.
            var first = await StartAsync(id, "park-log-a", beforeChildRuns: async () => second = await StartAsync(id, "park-log-b", "child-b"));
            foreach (var run in new[] { first, second! })
            {
                await AwaitPromptsAsync(run.Run.RunId, 1);
                await AssertParkedAsync(journal, id, run, 1);
            }
        }
        await StopAsync(await ResolveSessionAsync(id));

        // Act: the join recovers the session. The park line is a recovery line, so the join line ends the search.
        var lines = await RecoveryLogLinesAsync(id, "child_parks_recovered", () => JoinRecorderAsync(id));

        // Assert
        if (parks == 0)
            Assert.Empty(lines);
        else
            Assert.Matches(@"child_parks_recovered count=2 bytes=[1-9][0-9]*$", Assert.Single(lines));
    }

    /// <summary>
    /// A restart that ends the pause: recovery queues the held inputs at once. The journal holds the child request of a
    /// 0.28.0-beta.2 live wait: a request beside an accepted run, and no park. Recovery records the run as lost and
    /// withdraws the request, so the pause ends at recovery. Alice's input P waited in a turn that the crash stopped, and
    /// fire F waited for the pause. The restart reminder wakes the session. P runs before F, and F runs once.
    /// Planted bug: recovery starts F before the stashed restart reminder resumes P, or F runs twice.
    /// </summary>
    [Fact]
    public async Task A_restart_that_withdraws_a_beta_2_request_ends_the_pause_and_runs_the_person_first()
    {
        // Arrange: the journal stores the park as its request alone, the shape of a beta.2 live wait.
        var id = new SessionId("park/beta2-pause");
        var journal = await JournalAsync();
        ScriptParent();
        PlanChild("park-beta2", [[Probe("p1")]]);
        journal.RewriteNextStored(static evt => evt is ChildRunEvent.Parked parked ? parked.Requests[0] : null);
        await JoinRecorderAsync(id);
        var run = await StartAsync(id, "park-beta2");
        await AwaitPromptsAsync(run.Run.RunId, 1);
        Assert.Single(journal.Committed.OfType<ToolApprovalRequested>(), request => request.SourceChildRunId == run.Run.RunId);
        var busy = await HoldTurnAsync(journal, id, AliceText, AliceSource);
        AssertAck(await SendFireAsync(id));
        await StopAsync(await ResolveSessionAsync(id));
        busy.TrySetResult();

        // Act: the restart reminder wakes the session.
        AssertAck(await SendStrayRestartReminderAsync(id));

        // Assert
        var person = Assert.Single(journal.Committed.OfType<InputAdmitted>(), input => input.UserMessage.Content == AliceText);
        var fire = Assert.Single(FireAdmissions(journal, id, FireKey(Sweep, FireMs)));
        await AwaitAssertAsync(() => Assert.Equal(1, TurnRuns(journal, fire.InputId)), Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, id);
        Assert.Equal(1, TurnRuns(journal, fire.InputId));
        Assert.Equal(1, TurnRuns(journal, person.InputId));
        Assert.True(TurnAt(journal, person.InputId) < TurnAt(journal, fire.InputId),
            "P was admitted first and the restart reminder resumes it, so its turn record must come before the turn of F.");
    }

    // The turn of the text calls the sibling tool, and its next model call waits at the gate. A drain then stops the
    // turn after its tool batch and journals one continuation input for it (#2632).
    private async Task<TaskCompletionSource> HoldAfterToolBatchAsync(SessionId id, string text, MessageSource source)
    {
        var script = Clients.Parent.ResponseContentsSelector;
        Clients.Parent.ResponseContentsSelector = request =>
            FreshUserTexts(request).Contains(text) ? [SiblingCall(BatchCall)] : script?.Invoke(request);
        var hold = NewGate();
        var entered = ParentGate.Arm(request => HasResult(request, BatchCall), hold);
        await SendAsync(id, text, source);
        await entered.WaitAsync(Deadline, Cancellation);
        return hold;
    }

    // The daemon drains the session. The drain waits for the model grace period, and then the session stops.
    private async Task<ReminderDefinition> DrainAsync(SessionId id)
    {
        var session = await ResolveSessionAsync(id);
        var stopped = CreateTestProbe();
        stopped.Watch(session);
        var drain = AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(id, "daemon-stop"));
        await AwaitAssertAsync(() =>
        {
            AdvanceScheduler(TimeSpan.FromSeconds(1));
            Assert.True(drain.IsCompleted, "The restart drain did not finish.");
        }, Deadline, TimeSpan.FromMilliseconds(10), Cancellation);
        var drained = await drain;
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
        Assert.NotNull(drained.RestartReminder);
        return drained.RestartReminder;
    }

    // A second delivery of a restart reminder. Akka.Reminders can deliver a reminder again.
    private Task<ISessionResponse> SendStrayRestartReminderAsync(SessionId id) => AskManagerAsync<ISessionResponse>(
        new SendUserMessage
        {
            SessionId = id, Content = SessionExecutionLifetime.RestartReminderInstruction,
            Source = Reminder("restart-resume-second-delivery:1")
        });

    // The fires of reminder M and the continuation inputs of the session, in journal order.
    private static IReadOnlyList<InputAdmitted> HeldInputs(AcceptanceFault journal, SessionId id)
        => journal.Committed.OfType<InputAdmitted>().Where(input => input.SessionId == id
            && (input.SourceMessageId?.StartsWith($"{Sweep}:", StringComparison.Ordinal) == true || SessionInputFlow.IsContinuation(input))).ToArray();

    // The journal position of the first turn that consumed the input.
    private static int TurnAt(AcceptanceFault journal, InputId inputId) => IndexOfEvent(journal, evt =>
        evt is TurnRecorded turn && turn.ConsumedInputIds.Contains(inputId)
        || evt is ToolBatchStarted batch && batch.ConsumedInputIds.Contains(inputId));

    // A model request of a turn that the held fire started, or of its continuation.
    private static bool CarriesFire(IReadOnlyList<ChatMessage> request)
        => request.Any(message => message.Role == Microsoft.Extensions.AI.ChatRole.User && message.Text == FireText);
}
