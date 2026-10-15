// -----------------------------------------------------------------------
// <copyright file="ReminderFireAdmissionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable specs: a reminder ends at admission, so the session alone decides what happens to a fire.
/// <list type="bullet">
/// <item>A session keeps at most one waiting fire per reminder. A newer fire of that reminder is dropped.</item>
/// <item>A fire that runs, or that another reminder owns, never blocks a fire.</item>
/// <item>A redelivered fire is a duplicate and causes no side effect.</item>
/// <item>A restart-resume message never becomes an ordinary input.</item>
/// </list>
/// A spec that drops nothing (the guards) is green before the change. It stops a cure that drops too much.
/// </summary>
public sealed class ReminderFireAdmissionTests : ApprovalWaitTestBase
{
    private const string Build = "build-check";
    private const long FirstFireMs = 1_700_000_000_000;
    private const long TwelveMinutesMs = 12 * 60 * 1000;

    public ReminderFireAdmissionTests(ITestOutputHelper output) : base(output)
    {
    }

    public enum WaitsBehind
    {
        RunningTurn,
        OpenApproval,
        OpenApprovalThenRestart
    }

    public enum HowConsumed
    {
        StartedTheTurn,
        DrainedBehindATurn,
        RanAfterAnApproval
    }

    // Each way leaves an earlier fire pending in the journal while nothing in the session will ever run it.
    public enum HowOrphaned
    {
        CrashWhileFireWaits,
        CrashWhileFireRuns,
        GracefulRestartWithTwoRequesters,
        RestartReminderExpired,
        RestartMessageFoundAnotherTurnRunning
    }

    [Theory]
    [InlineData(WaitsBehind.RunningTurn)]
    [InlineData(WaitsBehind.OpenApproval)]
    [InlineData(WaitsBehind.OpenApprovalThenRestart)]
    public async Task A_fire_is_dropped_while_an_earlier_fire_of_the_same_reminder_waits(WaitsBehind behind)
    {
        // Arrange: the 10:00 fire waits. A 12-minute reminder fires again at 10:12.
        var sessionId = new SessionId($"test-channel/drop-newer-fire-{behind}");
        var waiting = await MakeSessionBusyAsync(sessionId, behind);
        var earlier = Key(Build, FirstFireMs);
        var newer = Key(Build, FirstFireMs + TwelveMinutesMs);
        await SendFireAsync(sessionId, earlier);
        if (behind == WaitsBehind.OpenApprovalThenRestart)
        {
            // The restart rebuilds the waiting fire from the journal. The drop rule must still see it.
            await StopSessionAsync(sessionId);
            waiting = waiting with { Subscriber = await JoinNewSubscriberAsync(sessionId) };
        }

        // Act: the session acknowledges the newer fire, so the reminder system sees success.
        await SendFireAsync(sessionId, newer);

        // Assert: only the earlier fire became an input, and only the earlier fire runs.
        Assert.Equal([earlier], await AdmittedSourceKeysAsync(sessionId));
        waiting.Release();
        await AwaitModelSawAsync(earlier);
        await AssertNoInputStaysPendingAsync(sessionId);
        Assert.False(ModelSaw(newer), "The dropped fire must never reach the model.");
    }

    [Fact]
    public async Task A_fire_is_admitted_while_the_earlier_fire_runs()
    {
        // Arrange: the 10:00 fire is the input of the running turn. It does not wait.
        var sessionId = new SessionId("test-channel/admit-while-earlier-fire-runs");
        await JoinNewSubscriberAsync(sessionId);
        var earlier = Key(Build, FirstFireMs);
        var newer = Key(Build, FirstFireMs + TwelveMinutesMs);
        var gate = await StartHeldTurnAsync(sessionId, FireText(earlier), ReminderSource(earlier));

        // Act
        await SendFireAsync(sessionId, newer);

        // Assert: the newer fire waits for its own turn. Both fires run.
        Assert.Equal([earlier, newer], await AdmittedSourceKeysAsync(sessionId));
        gate.TrySetResult();
        await AwaitModelSawAsync(earlier);
        await AwaitModelSawAsync(newer);
        await AssertNoInputStaysPendingAsync(sessionId);
    }

    [Fact]
    public async Task Fires_of_different_reminders_are_never_dropped()
    {
        // Arrange: "check" and "check-pr" share a name prefix. A prefix match would confuse them.
        var sessionId = new SessionId("test-channel/different-reminders-wait-together");
        var waiting = await MakeSessionBusyAsync(sessionId, WaitsBehind.RunningTurn);
        var keys = new[]
        {
            Key("check", FirstFireMs),
            Key("check-pr", FirstFireMs),
            Key("standup", FirstFireMs)
        };

        // Act
        foreach (var key in keys)
            await SendFireAsync(sessionId, key);

        // Assert: each reminder has its own waiting fire.
        Assert.Equal(keys, await AdmittedSourceKeysAsync(sessionId));
        waiting.Release();
        foreach (var key in keys)
            await AwaitModelSawAsync(key);
        await AssertNoInputStaysPendingAsync(sessionId);
    }

    [Theory]
    [InlineData("weekly:abc", "weekly:def")]
    [InlineData("weekly:", "weekly:1")]
    [InlineData("alpha", "beta")]
    public async Task A_key_that_is_not_a_fire_key_is_never_dropped(string earlier, string newer)
    {
        // Arrange: the session cannot tell which reminder owns such a key. It must admit the input.
        var sessionId = new SessionId($"test-channel/foreign-key-{earlier.Replace(':', '_')}-{newer.Replace(':', '_')}");
        var waiting = await MakeSessionBusyAsync(sessionId, WaitsBehind.RunningTurn);

        // Act
        await SendFireAsync(sessionId, earlier);
        await SendFireAsync(sessionId, newer);

        // Assert
        Assert.Equal([earlier, newer], await AdmittedSourceKeysAsync(sessionId));
        waiting.Release();
        await AwaitModelSawAsync(earlier);
        await AwaitModelSawAsync(newer);
        await AssertNoInputStaysPendingAsync(sessionId);
    }

    [Theory]
    [InlineData(HowConsumed.StartedTheTurn)]
    [InlineData(HowConsumed.DrainedBehindATurn)]
    [InlineData(HowConsumed.RanAfterAnApproval)]
    public async Task The_next_fire_is_admitted_once_the_earlier_fire_is_consumed(HowConsumed how)
    {
        // Arrange: the earlier fire runs. It is the input that started the turn, a drained input, or a waiting input.
        var sessionId = new SessionId($"test-channel/next-fire-after-{how}");
        var first = Key(Build, FirstFireMs);
        switch (how)
        {
            case HowConsumed.StartedTheTurn:
                await JoinNewSubscriberAsync(sessionId);
                await SendFireAsync(sessionId, first);
                break;
            case HowConsumed.DrainedBehindATurn:
                await JoinNewSubscriberAsync(sessionId);
                var gate = await StartHeldTurnAsync(sessionId, "Run the build", RequesterSource(Requester));
                await SendFireAsync(sessionId, first);
                gate.TrySetResult();
                break;
            case HowConsumed.RanAfterAnApproval:
                await ParkApprovalThenRecoverAsync(sessionId);
                await SendFireAsync(sessionId, first);
                Answer(sessionId, approve: true);
                break;
        }

        await AwaitModelSawAsync(first);
        await AssertNoInputStaysPendingAsync(sessionId);

        // Act and assert: nothing keeps the next fire blocked, and it runs.
        var second = Key(Build, FirstFireMs + TwelveMinutesMs);
        await SendFireAsync(sessionId, second);
        await AwaitModelSawAsync(second);
        await AssertNoInputStaysPendingAsync(sessionId);

        // Act and assert: a newer fire waits again behind a new turn. It is admitted, not dropped.
        var busyGate = await StartHeldTurnAsync(sessionId, "Run the build again", RequesterSource(Requester));
        var third = Key(Build, FirstFireMs + 2 * TwelveMinutesMs);
        await SendFireAsync(sessionId, third);
        Assert.Equal([first, second, third], await AdmittedSourceKeysAsync(sessionId));
        busyGate.TrySetResult();
        await AwaitModelSawAsync(third);
        await AssertNoInputStaysPendingAsync(sessionId);
    }

    [Fact]
    public async Task A_redelivered_fire_is_a_duplicate_while_the_fire_runs()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/redelivery-while-fire-runs");
        await JoinNewSubscriberAsync(sessionId);
        var fire = Key(Build, FirstFireMs);
        var gate = await StartHeldTurnAsync(sessionId, FireText(fire), ReminderSource(fire));

        // Act: Akka.Reminders sends the same fire again.
        await SendFireAsync(sessionId, fire);

        // Assert: the session acknowledged the copy. It stored no second input.
        Assert.Equal([fire], await AdmittedSourceKeysAsync(sessionId));
        gate.TrySetResult();
        await AwaitModelSawAsync(fire);
        await AssertNoInputStaysPendingAsync(sessionId);
        Assert.Single(FakeChat.ReceivedMessages, request => LastUserText(request) == FireText(fire));
    }

    [Fact]
    public async Task A_redelivered_fire_leaves_an_interrupted_tool_batch_alone()
    {
        // Arrange: the fire starts a turn. The daemon stops while its tool call runs. The tool call stays open in history.
        // The spec sends no join before the redelivery. A join runs the recovery abandonment after it replies, and that
        // write races the baseline read below. It also closes the open batch before the fire arrives. The redelivered
        // fire must wake the stopped session by itself, so the batch is still open when the fire reaches the session.
        var sessionId = new SessionId("test-channel/redelivery-after-interrupted-batch");
        var fire = Key(Build, FirstFireMs);
        ToolExecutor.BlockNextSuccessfulExecution("shell_execute");
        FakeChat.ToolCallsOnFirstCall =
        [
            new Microsoft.Extensions.AI.FunctionCallContent(CallId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];
        await JoinNewSubscriberAsync(sessionId);
        await SendFireAsync(sessionId, fire);
        await ToolExecutor.BlockedExecutionStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await StopSessionAsync(sessionId);
        ToolExecutor.FailBlockedExecution();
        var abandonedBefore = await AbandonedBatchCountAsync(sessionId);
        Assert.Equal(0, abandonedBefore);

        // Act: the stopped session receives the same fire again. The session wakes with the tool call still open.
        await SendFireAsync(sessionId, fire);

        // Assert: the session drops the copy with no side effect. It writes no closing result for the open call.
        Assert.Equal(abandonedBefore, await AbandonedBatchCountAsync(sessionId));
        Assert.Equal([fire], await AdmittedSourceKeysAsync(sessionId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_turn_records_the_key_of_its_own_reminder_only(bool reminderFirst)
    {
        // Arrange: a reminder turn and an operator turn run one after the other in one session.
        var sessionId = new SessionId($"test-channel/turn-keys-{(reminderFirst ? "reminder-first" : "operator-first")}");
        var subscriber = await JoinNewSubscriberAsync(sessionId);
        var fire = Key(Build, FirstFireMs);
        var reminder = (Text: FireText(fire), Source: ReminderSource(fire));
        var operatorInput = (Text: "Run the build", Source: RequesterSource(Requester));
        var (running, buffered) = reminderFirst ? (reminder, operatorInput) : (operatorInput, reminder);
        var gate = await StartHeldTurnAsync(sessionId, running.Text, running.Source);
        await SendAsync(sessionId, buffered.Text, buffered.Source);

        // Act
        gate.TrySetResult();
        await ExpectTurnsAsync(subscriber, 2);

        // Assert: the turn of the reminder names the fire. The turn of the operator names none.
        await AwaitAssertAsync(
            async () =>
            {
                var keys = (await ReadJournalAsync(sessionId))
                    .Select(entry => entry.Payload)
                    .OfType<TurnRecorded>()
                    .Select(turn => turn.SourceReminderId?.Value)
                    .ToArray();
                Assert.Equal(reminderFirst ? new[] { fire, null } : new[] { null, fire }, keys);
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(HowOrphaned.CrashWhileFireWaits)]
    [InlineData(HowOrphaned.CrashWhileFireRuns)]
    [InlineData(HowOrphaned.GracefulRestartWithTwoRequesters)]
    [InlineData(HowOrphaned.RestartReminderExpired)]
    [InlineData(HowOrphaned.RestartMessageFoundAnotherTurnRunning)]
    public async Task A_fire_is_admitted_when_an_earlier_fire_is_pending_and_nothing_will_run_it(HowOrphaned how)
    {
        // Arrange: the 10:00 fire stays pending in the journal. No restart message and no queue will ever run it.
        // The session did not "wait" for that fire, so it must not block the reminder.
        var sessionId = new SessionId($"test-channel/orphan-fire-{how}");
        var first = Key(Build, FirstFireMs);
        var release = await OrphanFireAsync(sessionId, first, how);
        Assert.NotEmpty(PendingInputIds(await ReadJournalAsync(sessionId)));

        // Act: the 10:12 fire and the 10:24 fire arrive.
        var second = Key(Build, FirstFireMs + TwelveMinutesMs);
        await SendFireAsync(sessionId, second);

        // Assert: the session stored the 10:12 fire and ran it. The same holds for the 10:24 fire.
        var admitted = await AdmittedSourceKeysAsync(sessionId);
        Assert.True(
            admitted.Contains(second),
            $"The session dropped the fire {second} for a pending fire that nothing will run ({how}). "
            + $"It stored [{string.Join(", ", admitted)}].");
        release();
        await AwaitModelSawAsync(second);
        var third = Key(Build, FirstFireMs + 2 * TwelveMinutesMs);
        await SendFireAsync(sessionId, third);
        await AwaitModelSawAsync(third);
    }

    [Fact]
    public async Task A_restart_resume_message_never_becomes_an_input()
    {
        // Arrange: a restart resumes an interrupted turn. The resumed turn waits on the model.
        var sessionId = new SessionId("test-channel/restart-resume-is-not-an-input");
        var (reminder, resumeGate) = await RestartAndResumeAsync(sessionId);
        var admittedBefore = await AdmittedCountAsync(sessionId);

        // Act and assert: the same restart message arrives again while the resumed turn runs.
        var duringTurn = await SendRestartMessageAsync(sessionId, reminder);
        Assert.IsType<CommandAck>(duringTurn);
        Assert.Equal(admittedBefore, await AdmittedCountAsync(sessionId));

        resumeGate.TrySetResult();
        await AwaitAssertAsync(
            async () => Assert.Empty(PendingInputIds(await ReadJournalAsync(sessionId))),
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        var callsBefore = FakeChat.CallCount;

        // Act and assert: the session restarts. A third copy arrives and finds nothing to resume. This is no failure.
        await StopSessionAsync(sessionId);
        await JoinNewSubscriberAsync(sessionId);
        var afterRestart = await SendRestartMessageAsync(sessionId, reminder);
        Assert.IsType<CommandAck>(afterRestart);
        Assert.Equal(admittedBefore, await AdmittedCountAsync(sessionId));
        Assert.Equal(callsBefore, FakeChat.CallCount);
        Assert.DoesNotContain(
            FakeChat.ReceivedMessages,
            request => UserTexts(request).Contains(reminder.Instructions));
    }

    [Fact]
    public async Task A_restart_resume_message_is_acknowledged_while_the_session_compacts()
    {
        // Arrange: the resumed turn ends and reports a full context window, so the session compacts. The summary call
        // is held, so the session stays in compaction.
        var sessionId = new SessionId("test-channel/restart-resume-during-compaction");
        var (reminder, resumeGate) = await RestartAndResumeAsync(sessionId);
        var watcher = CreateTestProbe("compaction-watcher");
        await JoinAsync(watcher, sessionId, OutputFilter.Full);
        var compactionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChat.ObservationResponseGate = compactionGate;
        FakeChat.PlannedUsageOverrides.Enqueue(new Microsoft.Extensions.AI.UsageDetails
        {
            InputTokenCount = 120_000,
            OutputTokenCount = 50,
            TotalTokenCount = 120_050
        });
        resumeGate.TrySetResult();
        await AwaitAssertAsync(
            () =>
            {
                Assert.Contains(
                    FakeChat.ReceivedMessages,
                    request => SystemText(request).Contains("You are a session summarizer", StringComparison.Ordinal));
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        var admittedBefore = await AdmittedCountAsync(sessionId);
        var callsBefore = FakeChat.CallCount;

        // Act: Akka.Reminders delivers the restart message again while the session compacts.
        var duringCompaction = await SendRestartMessageAsync(sessionId, reminder);

        // Assert: the session acknowledges the message and stores nothing. The compaction ends, and the model never sees the text.
        Assert.IsType<CommandAck>(duringCompaction);
        var admittedDuring = await AdmittedCountAsync(sessionId);
        Assert.True(
            admittedDuring == admittedBefore,
            $"A restart-resume message that arrives during compaction must not become an input. Stored inputs: {admittedBefore} before, {admittedDuring} after.");
        compactionGate.TrySetResult();
        await watcher.FishForMessageAsync(
            message => message is CompactionOutput,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        await AwaitAssertAsync(
            async () => Assert.Empty(PendingInputIds(await ReadJournalAsync(sessionId))),
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(admittedBefore, await AdmittedCountAsync(sessionId));
        Assert.DoesNotContain(
            FakeChat.ReceivedMessages.Skip(callsBefore),
            request => UserTexts(request).Contains(reminder.Instructions));
    }

    // Interrupts a turn with a daemon restart and resumes it with the restart reminder.
    // Returns the reminder and the gate that holds the resumed model call.
    private async Task<(ReminderDefinition Reminder, TaskCompletionSource ResumeGate)> RestartAndResumeAsync(
        SessionId sessionId)
    {
        await JoinNewSubscriberAsync(sessionId);
        await StartHeldTurnAsync(sessionId, "Finish the interrupted task", RequesterSource(Requester));
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        var drained = await SessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "daemon-stop"),
            TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        var reminder = Assert.IsType<ReminderDefinition>(drained.RestartReminder);
        await ExpectTerminatedAsync(session, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        await JoinNewSubscriberAsync(sessionId);
        var resumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callsBefore = FakeChat.CallCount;
        FakeChat.NextResponseGate = resumeGate;
        var first = await SendRestartMessageAsync(sessionId, reminder);
        Assert.IsType<CommandAck>(first);
        await AwaitModelCallAsync(callsBefore);
        return (reminder, resumeGate);
    }

    // Leaves the first fire pending in the journal with nothing to run it. Returns the action that opens any gate
    // the case still holds. The session is up, joined, and ready for the next fire when this method returns.
    private async Task<Action> OrphanFireAsync(SessionId sessionId, string first, HowOrphaned how)
    {
        await JoinNewSubscriberAsync(sessionId);
        switch (how)
        {
            case HowOrphaned.CrashWhileFireWaits:
            {
                var gate = await StartHeldTurnAsync(sessionId, "Run the build", RequesterSource(Requester));
                await SendFireAsync(sessionId, first);
                await StopSessionAsync(sessionId);
                gate.TrySetResult();
                await JoinNewSubscriberAsync(sessionId);
                return () => { };
            }
            case HowOrphaned.CrashWhileFireRuns:
            {
                var gate = await StartHeldTurnAsync(sessionId, FireText(first), ReminderSource(first));
                await StopSessionAsync(sessionId);
                gate.TrySetResult();
                await JoinNewSubscriberAsync(sessionId);
                return () => { };
            }
            case HowOrphaned.GracefulRestartWithTwoRequesters:
                // An operator turn runs and a fire waits behind it. The two inputs have different authority, so the
                // restart makes no restart reminder (D2 decision 6). The spec does not assert that, because a later
                // change can resume them. It asserts only that the journal still holds the pending fire.
                await StartHeldTurnAsync(sessionId, "Run the build", RequesterSource(Requester));
                await SendFireAsync(sessionId, first);
                await RestartGracefullyAsync(sessionId);
                await JoinNewSubscriberAsync(sessionId);
                return () => { };
            case HowOrphaned.RestartReminderExpired:
            {
                // The fire runs alone, so the restart makes a restart reminder. The daemon stays down longer than the
                // reminder lives, so the reminder system never sends it.
                await StartHeldTurnAsync(sessionId, FireText(first), ReminderSource(first));
                var prepared = await RestartGracefullyAsync(sessionId);
                Assert.NotNull(prepared.RestartReminder?.ExpiresAt);
                await JoinNewSubscriberAsync(sessionId);
                return () => { };
            }
            case HowOrphaned.RestartMessageFoundAnotherTurnRunning:
            {
                // The restart message arrives after an operator turn started. The message is a no-op then
                // (D2 decision 5), so the interrupted fire stays pending. A_restart_resume_message_never_becomes_an_input
                // proves that no-op, so this spec does not repeat it.
                await StartHeldTurnAsync(sessionId, FireText(first), ReminderSource(first));
                var prepared = await RestartGracefullyAsync(sessionId);
                var reminder = Assert.IsType<ReminderDefinition>(prepared.RestartReminder);
                await JoinNewSubscriberAsync(sessionId);
                var held = await StartHeldTurnAsync(sessionId, "Run the build", RequesterSource(Requester));
                Assert.IsType<CommandAck>(await SendRestartMessageAsync(sessionId, reminder));
                return () => held.TrySetResult();
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(how), how, null);
        }
    }

    // Asks the session to drain for a daemon restart and waits until the session actor stops.
    private async Task<DaemonRestartPrepared> RestartGracefullyAsync(SessionId sessionId)
    {
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        var prepared = await SessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "daemon-stop"),
            TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        await ExpectTerminatedAsync(session, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        return prepared;
    }

    private async Task<ISessionResponse> SendRestartMessageAsync(SessionId sessionId, ReminderDefinition reminder)
        => await SessionManager.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = reminder.Instructions,
            Source = RestartReminderSource(reminder)
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private sealed record BusySession(Akka.TestKit.TestProbe Subscriber, Action Release);

    // Puts the session in a state where a new reminder input must wait. Release ends the wait.
    private async Task<BusySession> MakeSessionBusyAsync(SessionId sessionId, WaitsBehind behind)
    {
        if (behind == WaitsBehind.RunningTurn)
        {
            var subscriber = await JoinNewSubscriberAsync(sessionId);
            var gate = await StartHeldTurnAsync(sessionId, "Run the build", RequesterSource(Requester));
            return new BusySession(subscriber, () => gate.TrySetResult());
        }

        var recovered = await ParkApprovalThenRecoverAsync(sessionId);
        return new BusySession(recovered, () => Answer(sessionId, approve: true));
    }

    // Sends a message that starts a turn, and holds the model call of that turn until the gate opens.
    private async Task<TaskCompletionSource> StartHeldTurnAsync(SessionId sessionId, string text, MessageSource source)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callsBefore = FakeChat.CallCount;
        FakeChat.NextResponseGate = gate;
        await SendAsync(sessionId, text, source);
        await AwaitModelCallAsync(callsBefore);
        return gate;
    }

    private async Task AwaitModelCallAsync(int callsBefore)
        => await AwaitAssertAsync(
            () =>
            {
                Assert.True(FakeChat.CallCount > callsBefore, "The held model call did not start.");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

    private static string Key(string reminderId, long fireMs) => $"{reminderId}:{fireMs}";

    private static string FireText(string key) => $"Fire {key}";

    private Task SendFireAsync(SessionId sessionId, string key)
        => SendAsync(sessionId, FireText(key), ReminderSource(key));

    private bool ModelSaw(string key)
        => FakeChat.ReceivedMessages.Any(request => UserTexts(request).Contains(FireText(key)));

    private async Task AwaitModelSawAsync(string key)
        => await AwaitAssertAsync(
            () =>
            {
                Assert.True(ModelSaw(key), $"The model never saw the fire {key}.");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

    // The source keys of every stored input that carries one. Only reminder fires carry a key in these specs.
    private async Task<string[]> AdmittedSourceKeysAsync(SessionId sessionId)
        => (await ReadJournalAsync(sessionId))
            .Select(entry => entry.Payload)
            .OfType<InputAdmitted>()
            .Select(admitted => admitted.SourceMessageId)
            .OfType<string>()
            .ToArray();

    private async Task<int> AdmittedCountAsync(SessionId sessionId)
        => (await ReadJournalAsync(sessionId)).Count(entry => entry.Payload is InputAdmitted);

    private async Task<int> AbandonedBatchCountAsync(SessionId sessionId)
        => (await ReadJournalAsync(sessionId)).Count(entry => entry.Payload is ToolBatchAbandoned);
}
