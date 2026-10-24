// -----------------------------------------------------------------------
// <copyright file="ChildParkRecoverySpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
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
    private const string FireText = "Sweep the open pull requests.";
    private const string Sweep = "daily-pr-sweep";
    private const long FireMs = 1_761_055_200_000;
    private const string AliceText = "Alice interrupted work.";
    private const string BatchCall = "turn-batch";

    /// <summary>
    /// S5, the pause across a restart. A child of Alice parks, so its open request pauses the session. Rows:
    /// <list type="bullet">
    /// <item>held-fire: a reminder fire waits for the pause, and the session crashes.</item>
    /// <item>stashed-fire: the session crashes, and a fire wakes it. The fire waits in the stash during recovery.</item>
    /// <item>reminder-held: a fire waits for the pause. After a crash, a restart reminder arrives. It starts no turn
    /// (N1).</item>
    /// <item>reminder-person: a fire waits, and Alice's turn runs. A drain stops her turn. After the restart, the restart
    /// reminder resumes her turn while the pause holds, and the fire still waits.</item>
    /// <item>notice: the open notice of the park waits in its turn when the session crashes. Recovery admits one new
    /// notice for the open request (#2617).</item>
    /// <item>automation-continuation: a drain stops a reminder turn after its tool batch. The restart reminder starts no
    /// turn for its continuation input while the request is open (#2651).</item>
    /// <item>passivated-fire: fire 1 of reminder M waits for the pause, and the session passivates. Fire 2 of M wakes
    /// the session. It replaces fire 1, so one fire of M runs.</item>
    /// </list>
    /// The shared postcondition: after the restart the prompt is still open and no automated turn runs. After Alice's
    /// answer the child resumes, and each held fire or continuation runs once in a turn. No close takes the place of
    /// that turn.
    /// Planted bug: a restart loses the pause, a stashed fire runs before the held inputs, a restart reminder starts a
    /// held input, or a crash gives no notice or two notices.
    /// </summary>
    [Theory]
    [InlineData("held-fire")]
    [InlineData("stashed-fire")]
    [InlineData("reminder-held")]
    [InlineData("reminder-person")]
    [InlineData("notice")]
    [InlineData("automation-continuation")]
    [InlineData("passivated-fire")]
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
            "automation-continuation" => async () => held = await HoldAfterToolBatchAsync(id, FireText, Fire(Sweep, FireMs)),
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
            case "held-fire" or "reminder-held":
                AssertAck(await SendFireAsync(id, FireMs));
                await RestartSessionAsync(id);
                break;
            case "stashed-fire":
                await StopAsync(await ResolveSessionAsync(id));
                AssertAck(await SendFireAsync(id, FireMs));
                break;
            case "reminder-person":
                AssertAck(await SendFireAsync(id, FireMs));
                held = await HoldTurnAsync(journal, id, AliceText, AliceSource);
                reminder = await DrainAsync(id);
                break;
            case "notice":
                await noticeEntered!.WaitAsync(Deadline, Cancellation);
                notices = ConsentNotices(journal, id).Count;
                await RestartSessionAsync(id);
                break;
            case "automation-continuation":
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
            "reminder-person" or "automation-continuation" => await SendRestartReminderAsync(id, reminder!),
            _ => null
        };
        if (row == "reminder-person")
        {
            Assert.True(reply is CommandAck and not RestartResumeNotNeeded, $"The restart reminder did not resume Alice's turn: {reply}.");
            var person = Assert.Single(journal.Committed.OfType<InputAdmitted>(), input => input.UserMessage.Content == AliceText);
            await AwaitAssertAsync(() => Assert.True(ConsumptionCount(journal, person.InputId) > 0,
                "A person's turn must run while the pause holds."), Deadline, cancellationToken: Cancellation);
        }
        else if (reply is not null)
            AssertAck(reply);

        // Assert: the pause came back. The prompt is open, and no automated turn runs.
        await BarrierAsync(id, run);
        Assert.DoesNotContain(Outputs<ToolInteractionClosed>(), closed => closed.CallId == prompt.CallId);
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
        await AwaitAssertAsync(() =>
        {
            if (row == "passivated-fire")
                Assert.True(HeldInputs(journal, id).Sum(input => TurnRuns(journal, input)) > 0, "No fire of M ran after the last answer.");
            else
                Assert.All(HeldInputs(journal, id), input => Assert.True(TurnRuns(journal, input) > 0,
                    $"The held input {input.InputId} did not run in a turn."));
        }, Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, id);
        var waited = HeldInputs(journal, id);
        if (row == "passivated-fire")
            Assert.Equal(1, waited.Sum(input => TurnRuns(journal, input)));
        else
        {
            Assert.All(waited, input => Assert.Equal(1, TurnRuns(journal, input)));
            Assert.All(waited, input => Assert.DoesNotContain(journal.Committed.OfType<InputClosed>(),
                closed => closed.InputIds.Contains(input.InputId)));
        }
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
        var continuation = Assert.Single(journal.Committed.OfType<InputAdmitted>(), input => input.SessionId == id && IsContinuation(input));
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
    /// Recovery logs one line with the count and the total size of the parked runs (PR 2 design D4). The line holds no
    /// park content. Two children of Alice park, and the session crashes.
    /// Planted bug: recovery logs nothing, logs one line for each park, or logs park content.
    /// </summary>
    [Fact]
    public async Task Recovery_logs_the_count_and_bytes_of_the_parked_runs()
    {
        // Arrange
        var id = new SessionId("park/log-two");
        var journal = await JournalAsync();
        ScriptParent();
        PlanChild("park-log-a", [[Probe("p1")]]);
        PlanChild("park-log-b", [[Probe("p1")]]);
        await JoinRecorderAsync(id);
        ChildRunAccepted? second = null;
        // Both children start before the first park, because the pause refuses a new child.
        var first = await StartAsync(id, "park-log-a", beforeChildRuns: async () => second = await StartAsync(id, "park-log-b", "child-b"));
        foreach (var run in new[] { first, second! })
        {
            await AwaitPromptsAsync(run.Run.RunId, 1);
            await AssertParkedAsync(journal, id, run, 1);
        }
        await StopAsync(await ResolveSessionAsync(id));

        // Act + Assert
        await EventFilter.Info(new Regex(@"child_parks_recovered count=2 bytes=[1-9][0-9]*$")).ExpectAsync(1,
            () => JoinRecorderAsync(id), Deadline, Cancellation);
    }

    /// <summary>
    /// A session with no parked run writes no park line at recovery. Its child ended before the crash.
    /// Planted bug: recovery logs "count=0".
    /// </summary>
    [Fact]
    public async Task Recovery_with_no_parks_writes_no_park_line()
    {
        // Arrange
        var id = new SessionId("park/log-none");
        var journal = await JournalAsync();
        ScriptParent();
        PlanChild("park-log-none");
        await JoinRecorderAsync(id);
        var run = await StartAsync(id, "park-log-none");
        await AwaitAssertAsync(() => Assert.NotEmpty(TerminalsOf(journal, run.Run.RunId)), Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, id);
        await StopAsync(await ResolveSessionAsync(id));

        // Act + Assert
        await EventFilter.Info(contains: "child_parks_recovered").ExpectAsync(0, () => JoinRecorderAsync(id), Deadline, Cancellation);
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
        var drained = await AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(id, "daemon-stop"));
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
        Assert.NotNull(drained.RestartReminder);
        return drained.RestartReminder;
    }

    private Task<ISessionResponse> SendFireAsync(SessionId id, long fireMs) => AskManagerAsync<ISessionResponse>(
        new SendUserMessage { SessionId = id, Content = FireText, Source = Fire(Sweep, fireMs) });

    // A second delivery of a restart reminder. Akka.Reminders can deliver a reminder again.
    private Task<ISessionResponse> SendStrayRestartReminderAsync(SessionId id) => AskManagerAsync<ISessionResponse>(
        new SendUserMessage
        {
            SessionId = id, Content = SessionExecutionLifetime.RestartReminderInstruction,
            Source = Reminder("restart-resume-second-delivery:1")
        });

    // The continuation input of a stopped turn has no text (#2632).
    private static bool IsContinuation(InputAdmitted input) => string.IsNullOrEmpty(input.UserMessage.Content);

    // The fires of reminder M and the continuation inputs of the session, in journal order.
    private static IReadOnlyList<InputAdmitted> HeldInputs(AcceptanceFault journal, SessionId id)
        => journal.Committed.OfType<InputAdmitted>().Where(input => input.SessionId == id
            && (input.SourceMessageId?.StartsWith($"{Sweep}:", StringComparison.Ordinal) == true || IsContinuation(input))).ToArray();

    // How many turns consumed the input: a turn record or a tool batch. A close is not a turn.
    private static int TurnRuns(AcceptanceFault journal, InputAdmitted input) => journal.Committed.ToArray().Count(evt =>
        evt is TurnRecorded turn && turn.ConsumedInputIds.Contains(input.InputId)
        || evt is ToolBatchStarted batch && batch.ConsumedInputIds.Contains(input.InputId));

    // A model request of a turn that the held fire started, or of its continuation.
    private static bool CarriesFire(IReadOnlyList<ChatMessage> request)
        => request.Any(message => message.Role == Microsoft.Extensions.AI.ChatRole.User && message.Text == FireText);
}
