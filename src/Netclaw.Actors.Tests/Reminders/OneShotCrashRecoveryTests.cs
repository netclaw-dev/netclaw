// -----------------------------------------------------------------------
// <copyright file="OneShotCrashRecoveryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Akka.Reminders;
using Netclaw.Actors.Reminders;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// The five crash and restart cases of change <c>oneshot-reminder-autodelete</c> (tasks 5.1 to 5.5).
/// Each test writes the on-disk and Akka.Reminders state that a stopped process leaves behind. Then it
/// starts a manager, or lets the scheduler deliver the occurrence, and checks that the one-shot neither
/// runs again nor loses its outcome. The Akka.Reminders storage is in memory and lives as long as the
/// actor system, so a restarted manager sees the same durable occurrences.
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public sealed class OneShotCrashRecoveryTests : OneShotReminderTestBase
{
    public OneShotCrashRecoveryTests(ITestOutputHelper output) : base(output) { }

    // ── 5.1 and 5.3: the tombstone exists, the acknowledgement and the delete did not happen ──

    [Theory]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public async Task Fire_with_a_matching_tombstone_does_not_run(string outcome)
    {
        await DrainStartupAsync();
        var definition = NewOneShot("late-fire");
        await ScheduleAsync(definition);
        WriteTombstone(definition, outcome, Clock.GetUtcNow());
        var tombstoneText = await File.ReadAllTextAsync(TombstonePath(definition.Id), TestContext.Current.CancellationToken);

        Advance(FirstFireDelay);
        await AwaitEnvelopesHandledAsync(1);

        Assert.Equal(0, Pipeline.InvocationCount);
        Assert.True(Definitions.Get(definition.Id) is null, "The definition of an ended one-shot must be deleted at the fire.");
        Assert.False(await IsScheduledAsync(definition.Id));
        // The manager settled the occurrence. Whether it cancels the schedule before or after the
        // acknowledgement decides which of the two terminal statuses Akka.Reminders keeps.
        Assert.Contains(
            await OccurrenceStatusAsync(definition.Id, definition.Schedule.FireAt!.Value),
            new[] { "Delivered", "Cancelled" });
        Assert.Equal(
            tombstoneText,
            await File.ReadAllTextAsync(TombstonePath(definition.Id), TestContext.Current.CancellationToken));
        Assert.Empty(await History.ReadAsync(definition.Id, 10));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public async Task Restart_with_a_matching_tombstone_deletes_the_schedule_and_the_definition(
        string outcome)
    {
        await DrainStartupAsync();
        var definition = NewOneShot("stopped-before-delete");
        await ScheduleAsync(definition);
        var tombstoneText = string.Empty;

        await RestartManagerAsync(() =>
        {
            WriteTombstone(definition, outcome, Clock.GetUtcNow());
            tombstoneText = File.ReadAllText(TombstonePath(definition.Id));
        });

        Assert.True(Definitions.Get(definition.Id) is null, "Reconciliation must delete the definition of an ended one-shot.");
        Assert.False(await IsScheduledAsync(definition.Id), "Reconciliation must cancel the schedule of an ended one-shot.");
        Assert.Equal(
            tombstoneText,
            await File.ReadAllTextAsync(TombstonePath(definition.Id), TestContext.Current.CancellationToken));
        Advance(FirstFireDelay);
        await BarrierAsync();
        Assert.Equal(0, Envelopes.Count);
        Assert.Equal(0, Pipeline.InvocationCount);
    }

    // ── 5.2: the occurrence is acknowledged, the tombstone is not written ──

    [Theory]
    [InlineData("Delivered", "Completed")]
    [InlineData("Cancelled", "Failed")]
    public async Task Restart_ends_a_oneshot_from_the_durable_occurrence_status(string status, string outcome)
    {
        await DrainStartupAsync();
        var definition = NewOneShot("acked-before-tombstone");
        await ScheduleAsync(definition);

        // The scheduler delivers the occurrence to a probe, as if the process stopped before it
        // wrote the tombstone. Then the test settles the occurrence in Akka.Reminders by hand.
        await StopManagerAsync();
        var probe = CreateTestProbe("stopped-process");
        RouteEnvelopesTo(probe.Ref);
        Advance(FirstFireDelay);
        var envelope = await probe.ExpectMsgAsync<ReminderEnvelope<ReminderPayload>>(
            AskTimeout, cancellationToken: TestContext.Current.CancellationToken);
        if (status == "Delivered")
            await AckAsync(envelope);
        else
            await CancelScheduleAsync(definition.Id);
        Assert.Equal(status, await OccurrenceStatusAsync(definition.Id, definition.Schedule.FireAt!.Value));

        await StartManagerAsync();

        Assert.True(Definitions.Get(definition.Id) is null, "Reconciliation must end a one-shot whose occurrence is terminal.");
        var tombstone = RequireTombstone(definition.Id);
        Assert.Equal(outcome, OutcomeOf(tombstone));
        Assert.Equal(definition.Schedule.FireAtMs, Number(tombstone, "fireAtMs"));
        Assert.Equal(outcome == "Failed", TryGet(tombstone, "instructions", out _));
        Assert.Equal(0, Pipeline.InvocationCount);
    }

    // ── 5.4: state from the previous version ──

    [Fact]
    public async Task Old_failed_definition_is_acknowledged_at_redelivery_and_ended_by_the_sweep()
    {
        await DrainStartupAsync();
        var definition = NewOneShot("old-failed");
        await ScheduleAsync(definition);
        // The previous version saved this state at the fifth failure and stopped before the negative acknowledgement.
        Definitions.Save(Definitions.Get(definition.Id)! with
        {
            Enabled = false,
            ConsecutiveFailures = ReminderManagerActor.FailurePauseThreshold,
            TerminalOutcome = ReminderTerminalOutcome.Failed
        });

        Advance(FirstFireDelay);
        await AwaitEnvelopesHandledAsync(1);
        Assert.Equal(0, Pipeline.InvocationCount);
        Assert.Contains(
            await OccurrenceStatusAsync(definition.Id, definition.Schedule.FireAt!.Value),
            new[] { "Delivered", "Cancelled" });

        await SweepAsync();

        Assert.True(Definitions.Get(definition.Id) is null, "The sweep must end a leftover Failed one-shot.");
        var tombstone = RequireTombstone(definition.Id);
        Assert.Equal("Failed", OutcomeOf(tombstone));
        Assert.Equal(DefaultPrompt, Text(tombstone, "instructions"));
        Assert.False(string.IsNullOrWhiteSpace(Text(tombstone, "lastError")));
    }

    // ── 5.5: a re-arm stopped after it scheduled the new occurrence ──

    [Fact]
    public async Task Fire_of_an_orphan_schedule_of_an_ended_oneshot_runs_nothing_and_is_not_an_error()
    {
        await DrainStartupAsync();
        var ended = NewOneShot("rearm-stopped");
        await SeedEndedAsync(ended, "Completed");
        var tombstoneText = await File.ReadAllTextAsync(TombstonePath(ended.Id), TestContext.Current.CancellationToken);
        // The re-arm scheduled the new occurrence, and then the process stopped before it deleted the
        // old data and saved the new definition.
        await ScheduleDirectlyAsync(ended.Id, Clock.GetUtcNow() + TimeSpan.FromMinutes(10));

        Advance(TimeSpan.FromMinutes(10));
        await AwaitEnvelopesHandledAsync(1);

        Assert.Equal(0, Pipeline.InvocationCount);
        Assert.False(await IsScheduledAsync(ended.Id));
        Assert.Null(Definitions.Get(ended.Id));
        Assert.Equal(
            tombstoneText,
            await File.ReadAllTextAsync(TombstonePath(ended.Id), TestContext.Current.CancellationToken));
        Assert.Single(await History.ReadAsync(ended.Id, 10));
        // A fire after the end is expected after a crash. It is not an error (design D4).
        Assert.Empty(LogsOfLevel(Akka.Event.LogLevel.ErrorLevel, ended.Id.Value));
    }

    [Fact]
    public async Task Restart_cancels_the_orphan_schedule_of_an_ended_oneshot_and_keeps_its_data()
    {
        await DrainStartupAsync();
        var ended = NewOneShot("rearm-stopped-2");
        await SeedEndedAsync(ended, "Completed");
        await ScheduleDirectlyAsync(ended.Id, Clock.GetUtcNow() + TimeSpan.FromMinutes(10));
        Assert.True(await IsScheduledAsync(ended.Id));

        await RestartManagerAsync();

        Assert.False(await IsScheduledAsync(ended.Id), "Reconciliation must cancel the orphan schedule.");
        Assert.True(TombstoneExists(ended.Id));
        Assert.Single(await History.ReadAsync(ended.Id, 10));
        Advance(TimeSpan.FromMinutes(10));
        await BarrierAsync();
        Assert.Equal(0, Pipeline.InvocationCount);
    }

    // ── 5.2 in a running daemon: the 12-hour sweep ends what the restart ends ──

    // The tombstone write failed after a run, and Netclaw acknowledged the occurrence (design D3). The
    // durable status Delivered is then the only record of the outcome, and Akka.Reminders deletes it
    // after 12 days. A daemon that runs for weeks must not wait for a restart.
    private async Task<ReminderDefinition> RunAckedWithoutTombstoneAsync(string id, RunMode mode)
    {
        Pipeline.Mode = mode;
        var definition = NewOneShot(id);
        await ScheduleAsync(definition);
        var blocker = Path.Combine(EndedDirectory, $"{Uri.EscapeDataString(id)}.json.tmp");
        Directory.CreateDirectory(blocker);

        Advance(FirstFireDelay);
        await AwaitSettledRunAsync(definition.Id, 1);

        Assert.Equal("Delivered", await OccurrenceStatusAsync(definition.Id, definition.Schedule.FireAt!.Value));
        Assert.NotNull(Definitions.Get(definition.Id));
        Assert.False(TombstoneExists(definition.Id));
        // The disk works again, but the daemon does not restart.
        Directory.Delete(blocker);
        return definition;
    }

    [Fact]
    public async Task The_sweep_ends_a_oneshot_that_ran_and_was_acknowledged_without_a_tombstone()
    {
        await DrainStartupAsync();
        var definition = await RunAckedWithoutTombstoneAsync("acked-then-swept", RunMode.Succeed);

        Advance(ReminderManagerActor.TerminalPruneInterval);
        await BarrierAsync();

        Assert.True(
            Definitions.Get(definition.Id) is null,
            "The sweep must end a one-shot whose durable occurrence status is Delivered.");
        Assert.False(await IsScheduledAsync(definition.Id));
        var tombstone = RequireTombstone(definition.Id);
        Assert.Equal("Completed", OutcomeOf(tombstone));
        Assert.Equal(definition.Schedule.FireAtMs, Number(tombstone, "fireAtMs"));
        Assert.False(TryGet(tombstone, "instructions", out _));
        Assert.Single(await History.ReadAsync(definition.Id, 10));

        // No further sweep, and no later time, runs it again.
        Advance(ReminderManagerActor.TerminalPruneInterval);
        Advance(ReminderManagerActor.TerminalPruneInterval);
        await BarrierAsync();
        Assert.Equal(1, Pipeline.InvocationCount);
        Assert.Equal(1, Envelopes.Count);
    }

    // The occurrence status of an active run is AwaitingAck. A sweep that ended it would hide the run.
    [Fact]
    public async Task The_sweep_leaves_a_oneshot_with_an_active_run_alone()
    {
        await DrainStartupAsync();
        Pipeline.Mode = RunMode.Hold;
        var definition = NewOneShot("running-while-swept");
        await ScheduleAsync(definition);
        Advance(FirstFireDelay);
        var executionId = await AwaitDispatchAsync();

        await SweepAsync();

        Assert.NotNull(Definitions.Get(definition.Id));
        Assert.False(TombstoneExists(definition.Id));
        Assert.True((await StatusAsync(definition.Id, TeamCaller)).Executing);

        await CompleteRunAsync(executionId, definition.Id, RunOutcome.Success);
        await BarrierAsync();

        Assert.Equal("Completed", OutcomeOf(RequireTombstone(definition.Id)));
        Assert.Null(Definitions.Get(definition.Id));
    }

    // A denied run is a failure of the one-shot (design D1a). When the tombstone write failed, the history
    // row with the status denied is the only durable trace, because the occurrence status is Delivered for
    // a success and for a denial.
    [Theory]
    [InlineData("restart")]
    [InlineData("sweep")]
    public async Task A_denied_run_that_was_acknowledged_without_a_tombstone_ends_failed_with_the_denial(string trigger)
    {
        await DrainStartupAsync();
        var definition = await RunAckedWithoutTombstoneAsync("denied-then-ended", RunMode.Deny);
        Assert.Equal("denied", Assert.Single(await History.ReadAsync(definition.Id, 10)).Status);

        await TriggerAsync(trigger);

        Assert.True(Definitions.Get(definition.Id) is null, "The one-shot must end.");
        Assert.False(await IsScheduledAsync(definition.Id));
        var tombstone = RequireTombstone(definition.Id);
        Assert.Equal("Failed", OutcomeOf(tombstone));
        Assert.Contains(DeniedText, Text(tombstone, "lastError"), StringComparison.Ordinal);
        Assert.Equal(DefaultPrompt, Text(tombstone, "instructions"));
        Assert.Equal(1, Pipeline.InvocationCount);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("sweep")]
    public async Task A_successful_run_that_was_acknowledged_without_a_tombstone_still_ends_completed(string trigger)
    {
        await DrainStartupAsync();
        var definition = await RunAckedWithoutTombstoneAsync("ok-then-ended", RunMode.Succeed);

        await TriggerAsync(trigger);

        Assert.True(Definitions.Get(definition.Id) is null, "The one-shot must end.");
        var tombstone = RequireTombstone(definition.Id);
        Assert.Equal("Completed", OutcomeOf(tombstone));
        Assert.Null(Text(tombstone, "lastError"));
        Assert.False(TryGet(tombstone, "instructions", out _));
    }

    // The recovery from the durable status must not overwrite a tombstone file that Netclaw cannot read.
    // The file may hold the real outcome. The end waits for a repair, which must happen within about 12
    // hours: Akka.Reminders gives up the delivery after ten attempts, and then the fire path cannot help.
    [Theory]
    [InlineData("restart")]
    [InlineData("sweep")]
    public async Task The_end_from_the_durable_status_skips_a_corrupt_tombstone_and_a_repair_lets_it_end(string trigger)
    {
        await DrainStartupAsync();
        var definition = await RunAckedWithoutTombstoneAsync("acked-corrupt", RunMode.Succeed);
        const string damaged = "{ not json";
        await File.WriteAllTextAsync(TombstonePath(definition.Id), damaged, TestContext.Current.CancellationToken);

        await TriggerAsync(trigger);

        var stored = Definitions.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled);
        Assert.Equal(
            damaged,
            await File.ReadAllTextAsync(TombstonePath(definition.Id), TestContext.Current.CancellationToken));
        Assert.Contains(Alerts.Alerts, a => a.Type == "reminder.tombstone.invalid" && a.Source == definition.Id.Value);
        Assert.Equal(1, Pipeline.InvocationCount);

        // The operator deletes the file. The next pass ends the one-shot.
        File.Delete(TombstonePath(definition.Id));
        await TriggerAsync(trigger);

        Assert.True(Definitions.Get(definition.Id) is null, "A repaired file must let the one-shot end.");
        Assert.Equal("Completed", OutcomeOf(RequireTombstone(definition.Id)));
        Assert.Equal(1, Pipeline.InvocationCount);
    }

    private Task TriggerAsync(string trigger)
    {
        if (trigger == "restart")
            return RestartManagerAsync();

        Advance(ReminderManagerActor.TerminalPruneInterval);
        return BarrierAsync();
    }

    // ── A tombstone that Netclaw cannot read ──

    // Reads treat an unreadable tombstone as absent. A fire is not a read: it decides whether a one-shot
    // runs, so it must fail closed. The tombstone may belong to a run that already succeeded.
    [Fact]
    public async Task A_corrupt_tombstone_stops_the_fire_and_a_repair_lets_the_run_happen()
    {
        await DrainStartupAsync();
        // A run that started would stay active here, so the status below shows it at once.
        Pipeline.Mode = RunMode.Hold;
        var definition = NewOneShot("corrupt-fire");
        await ScheduleAsync(definition);
        Directory.CreateDirectory(EndedDirectory);
        const string damaged = "{ not json";
        await File.WriteAllTextAsync(TombstonePath(definition.Id), damaged, TestContext.Current.CancellationToken);

        Advance(FirstFireDelay);
        await AwaitEnvelopesHandledAsync(1);

        var status = await StatusAsync(definition.Id, TeamCaller);
        Assert.False(status.Executing, "A fire with an unreadable tombstone must not start the run.");
        Assert.Equal(0, Pipeline.InvocationCount);
        Assert.NotEqual("Delivered", await OccurrenceStatusAsync(definition.Id, definition.Schedule.FireAt!.Value));
        var stored = Definitions.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled);
        Assert.Equal(
            damaged,
            await File.ReadAllTextAsync(TombstonePath(definition.Id), TestContext.Current.CancellationToken));
        Assert.Contains(Alerts.Alerts, a => a.Type == "reminder.tombstone.invalid" && a.Source == definition.Id.Value);
        Assert.NotEmpty(LogsOfLevel(Akka.Event.LogLevel.ErrorLevel, definition.Id.Value));

        // The operator deletes the file. The acknowledgement lease expires, and Akka.Reminders delivers the
        // occurrence again. The test scheduler runs the timers that Akka.Reminders sets in reaction to the
        // expiry only when the clock moves again, so each poll moves the clock by one minute.
        File.Delete(TombstonePath(definition.Id));
        Pipeline.Mode = RunMode.Succeed;
        await AwaitAssertAsync(() =>
        {
            Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(1, Pipeline.InvocationCount);
        }, TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
        await AwaitSettledRunAsync(definition.Id, 1);

        Assert.Null(Definitions.Get(definition.Id));
        Assert.Equal("Completed", OutcomeOf(RequireTombstone(definition.Id)));
    }
}

/// <summary>
/// An unreadable tombstone holds the fire, and Akka.Reminders returns the occurrence only while it has
/// attempts left. The operator must repair the file inside that window (about 12 hours with ten
/// attempts). This class uses three attempts so that the window is short in virtual time.
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public sealed class OneShotCorruptTombstoneWindowTests : OneShotReminderTestBase
{
    public OneShotCorruptTombstoneWindowTests(ITestOutputHelper output) : base(output) { }

    protected override int MaxDeliveryAttempts => 3;

    [Fact]
    public async Task A_repair_after_the_attempt_window_ends_the_oneshot_as_failed_and_does_not_run_it()
    {
        await DrainStartupAsync();
        var definition = NewOneShot("late-repair");
        await ScheduleAsync(definition);
        Directory.CreateDirectory(EndedDirectory);
        const string damaged = "{ not json";
        await File.WriteAllTextAsync(TombstonePath(definition.Id), damaged, TestContext.Current.CancellationToken);

        // Each lease expiry returns the occurrence. The manager holds every attempt. The test scheduler
        // runs the timers that Akka.Reminders sets in reaction to an expiry only when the clock moves
        // again, so each poll moves the clock by one minute.
        Advance(FirstFireDelay);
        await AwaitAssertAsync(async () =>
        {
            Advance(TimeSpan.FromMinutes(1));
            Assert.Equal("Failed", await OccurrenceStatusAsync(definition.Id, definition.Schedule.FireAt!.Value));
        }, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
        await BarrierAsync();

        // The window is over, and the file is still damaged: the pass alerts and ends nothing.
        await SweepAsync();
        Assert.NotNull(Definitions.Get(definition.Id));
        Assert.Equal(
            damaged,
            await File.ReadAllTextAsync(TombstonePath(definition.Id), TestContext.Current.CancellationToken));

        File.Delete(TombstonePath(definition.Id));
        await SweepAsync();

        Assert.True(Definitions.Get(definition.Id) is null, "A repaired file must let the pass end the one-shot.");
        var tombstone = RequireTombstone(definition.Id);
        Assert.Equal("Failed", OutcomeOf(tombstone));
        Assert.Equal(DefaultPrompt, Text(tombstone, "instructions"));
        Assert.Equal(0, Pipeline.InvocationCount);
    }
}
