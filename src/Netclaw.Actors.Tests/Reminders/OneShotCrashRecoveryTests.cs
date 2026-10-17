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
