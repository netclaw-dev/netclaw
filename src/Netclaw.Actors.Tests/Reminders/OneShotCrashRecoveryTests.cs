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

    // ── Review round 4: a failed status query must not restart the manager (SF-A) ──

    private const string StatusQueryAlert = "reminder.status.query_failed";

    // An Akka.Reminders query that throws would restart the manager. A restart stops every execution
    // actor, and the one-shot would run again after the 70-minute lease. The pass has no reason to ask
    // about a one-shot that has an active run, because the occurrence of an active run is open.
    [Fact]
    public async Task A_failed_status_query_during_a_sweep_cannot_stop_an_active_run()
    {
        await DrainStartupAsync();
        Pipeline.Mode = RunMode.Hold;
        var definition = NewOneShot("status-fails-during-run");
        await ScheduleAsync(definition);
        Advance(FirstFireDelay);
        var executionId = await AwaitDispatchAsync();

        ReminderStorage.FailNextStatusQueries(int.MaxValue);
        await SweepAsync();
        ReminderStorage.FailNextStatusQueries(0);

        // A restart of the manager stops the execution actor and clears the active run.
        Assert.True(
            (await StatusAsync(definition.Id, TeamCaller)).Executing,
            "A failed status query must not restart the manager. The run must stay active.");
        Assert.NotNull(Definitions.Get(definition.Id));
        Assert.False(TombstoneExists(definition.Id));
        Assert.DoesNotContain(Alerts.Alerts, a => a.Type == StatusQueryAlert);

        await CompleteRunAsync(executionId, definition.Id, RunOutcome.Success);
        await BarrierAsync();

        Assert.Equal("Completed", OutcomeOf(RequireTombstone(definition.Id)));
        Assert.Null(Definitions.Get(definition.Id));
        Assert.Single(await History.ReadAsync(definition.Id, 10));

        // The lease of the occurrence ends and more sweeps run. The one-shot does not run again.
        Advance(TimeSpan.FromMinutes(71));
        Advance(ReminderManagerActor.TerminalPruneInterval);
        await BarrierAsync();
        Assert.Equal(1, Pipeline.InvocationCount);
        Assert.Equal(1, Envelopes.Count);
    }

    // The test above proves the outcome. This test pins the cause: the sweep sends no query for the one-shot.
    [Fact]
    public async Task The_sweep_asks_for_no_status_while_a_run_is_active()
    {
        await DrainStartupAsync();
        Pipeline.Mode = RunMode.Hold;
        var definition = NewOneShot("active-run-no-query");
        await ScheduleAsync(definition);
        Advance(FirstFireDelay);
        await AwaitDispatchAsync();

        var queriesBefore = ReminderStorage.StatusQueryCount;
        await SweepAsync();

        Assert.Equal(queriesBefore, ReminderStorage.StatusQueryCount);
    }

    [Fact]
    public async Task A_failed_status_query_skips_that_oneshot_raises_an_alert_and_a_later_sweep_ends_it()
    {
        await DrainStartupAsync();
        var definition = await RunAckedWithoutTombstoneAsync("status-fails-idle", RunMode.Succeed);

        ReminderStorage.FailNextStatusQueries(int.MaxValue);
        await SweepAsync();

        // The sweep reached the end without a restart: the manager answers and the alert exists.
        Assert.Contains(
            Alerts.Alerts,
            a => a.Type == StatusQueryAlert && a.Source == definition.Id.Value);
        // The earlier settlement failure also logged an error for this id, so the text must say what failed here.
        await AwaitAssertAsync(
            () => Assert.Contains(
                LogsOfLevel(Akka.Event.LogLevel.ErrorLevel, definition.Id.Value),
                e => e.Message!.ToString()!.Contains("occurrence status", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        var stored = Definitions.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled);
        Assert.False(TombstoneExists(definition.Id));
        Assert.Equal(1, Pipeline.InvocationCount);

        // The store works again. The next sweep ends the one-shot, and nothing ran twice.
        ReminderStorage.FailNextStatusQueries(0);
        Advance(ReminderManagerActor.TerminalPruneInterval);
        await BarrierAsync();

        Assert.True(Definitions.Get(definition.Id) is null, "The next sweep must end the one-shot.");
        Assert.Equal("Completed", OutcomeOf(RequireTombstone(definition.Id)));
        Assert.Equal(1, Pipeline.InvocationCount);
        Assert.Equal(1, Envelopes.Count);
    }

    // The first query of the pass fails, whichever one-shot it belongs to. The pass must go on to the other.
    [Fact]
    public async Task A_failed_status_query_does_not_stop_the_pass_for_the_next_oneshot()
    {
        await DrainStartupAsync();
        var first = await RunAckedWithoutTombstoneAsync("status-fails-one", RunMode.Succeed);
        var second = await RunAckedWithoutTombstoneAsync("status-fails-two", RunMode.Succeed);

        ReminderStorage.FailNextStatusQueries(1);
        await SweepAsync();

        var both = new[] { first, second };
        var kept = Assert.Single(both, d => Definitions.Get(d.Id) is not null);
        var ended = Assert.Single(both, d => Definitions.Get(d.Id) is null);
        Assert.Equal("Completed", OutcomeOf(RequireTombstone(ended.Id)));
        var alert = Assert.Single(Alerts.Alerts, a => a.Type == StatusQueryAlert);
        Assert.Equal(kept.Id.Value, alert.Source);

        Advance(ReminderManagerActor.TerminalPruneInterval);
        await BarrierAsync();

        Assert.Null(Definitions.Get(kept.Id));
        Assert.Equal("Completed", OutcomeOf(RequireTombstone(kept.Id)));
        Assert.Equal(2, Pipeline.InvocationCount);
    }

    // The startup reconcile has more work than the status pass. A failed query must not cancel it.
    [Fact]
    public async Task A_failed_status_query_at_startup_still_cancels_orphans_restores_schedules_and_purges()
    {
        await DrainStartupAsync();
        var stuck = await RunAckedWithoutTombstoneAsync("status-fails-at-start", RunMode.Succeed);
        var orphan = new ReminderId("orphan-of-status-failure");
        await ScheduleDirectlyAsync(orphan, Clock.GetUtcNow() + TimeSpan.FromDays(1));
        Assert.True(await IsScheduledAsync(orphan));
        var expired = NewOneShot("expired-of-status-failure");
        // The restored one-shot must fall outside the sweep window below. A due one-shot would run during the
        // Advance and race the global InvocationCount check.
        var restorable = NewOneShot("restore-after-status-failure", fireIn: TimeSpan.FromDays(1));

        await StopManagerAsync();
        Definitions.Save(restorable);
        await SeedEndedAsync(expired, "Completed", endedAt: Clock.GetUtcNow() - TimeSpan.FromDays(13));
        Assert.True(TombstoneExists(expired.Id));
        ReminderStorage.FailNextStatusQueries(int.MaxValue);

        StartManagerWithoutDrain();
        await ReconcileOrFailAsync();
        await ReconcileOrFailAsync();

        Assert.False(await IsScheduledAsync(orphan), "The reconcile must cancel the orphan schedule.");
        Assert.True(await IsScheduledAsync(restorable.Id), "The reconcile must restore the schedule.");
        Assert.False(TombstoneExists(expired.Id), "The reconcile must purge the expired tombstone.");
        Assert.Empty(await History.ReadAsync(expired.Id, 10));
        Assert.NotNull(Definitions.Get(stuck.Id));
        Assert.Contains(
            Alerts.Alerts,
            a => a.Type == StatusQueryAlert && a.Source == stuck.Id.Value);

        ReminderStorage.FailNextStatusQueries(0);
        Advance(ReminderManagerActor.TerminalPruneInterval);
        await BarrierAsync();

        Assert.Null(Definitions.Get(stuck.Id));
        Assert.Equal("Completed", OutcomeOf(RequireTombstone(stuck.Id)));
        Assert.Single(await History.ReadAsync(stuck.Id, 10));
        Assert.Equal(1, Pipeline.InvocationCount);
    }

    private async Task ReconcileOrFailAsync()
    {
        object reply;
        try
        {
            reply = await Akka.Actor.Futures.Ask<object>(
                Manager, ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"The reconcile must not fail when a status query fails. It failed with: {ex.Message}");
        }

        Assert.IsType<ReminderManagerActor.ReconcileCompleted>(reply);
    }

    // ── Review round 4: the denied check reads the row of this occurrence (N1) and says when it cannot (N2) ──

    // A denied run is left without a tombstone, and an agent re-arms the one-shot. The new run is
    // acknowledged but its history row is missing (the disk was full again). The newest row on disk is
    // then the denial of the earlier run. It belongs to another occurrence, so the pass ignores it.
    [Fact]
    public async Task A_denied_row_of_an_earlier_run_does_not_fail_a_rearmed_oneshot_that_ran_ok()
    {
        await DrainStartupAsync();
        var first = await RunAckedWithoutTombstoneAsync("rearmed-after-denial", RunMode.Deny);
        var denied = Assert.Single(await History.ReadAsync(first.Id, 10));
        Assert.Equal("denied", denied.Status);

        var rearmed = first with
        {
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = Clock.GetUtcNow() + TimeSpan.FromHours(1)
            }
        };
        var saved = await SaveAsync(rearmed);
        Assert.True(saved.Success, saved.ErrorMessage);
        Assert.True(denied.FiredAt < rearmed.Schedule.FireAt, "The denied row must be older than the re-armed fire time.");

        // The new occurrence is acknowledged, and no history row is written for it.
        await StopManagerAsync();
        var probe = CreateTestProbe("stopped-process-after-rearm");
        RouteEnvelopesTo(probe.Ref);
        Advance(TimeSpan.FromHours(1));
        var envelope = await probe.ExpectMsgAsync<ReminderEnvelope<ReminderPayload>>(
            AskTimeout, cancellationToken: TestContext.Current.CancellationToken);
        await AckAsync(envelope);
        Assert.Equal("Delivered", await OccurrenceStatusAsync(first.Id, rearmed.Schedule.FireAt!.Value));

        await StartManagerAsync();

        Assert.True(Definitions.Get(first.Id) is null, "The one-shot must end.");
        var tombstone = RequireTombstone(first.Id);
        Assert.Equal("Completed", OutcomeOf(tombstone));
        Assert.Null(Text(tombstone, "lastError"));
        Assert.False(TryGet(tombstone, "instructions", out _));
        Assert.Equal(rearmed.Schedule.FireAtMs, Number(tombstone, "fireAtMs"));
        Assert.Equal(1, Pipeline.InvocationCount);
    }

    private string HistoryPath(ReminderId id)
        => Path.Combine(Paths.RemindersDirectory, $"{Uri.EscapeDataString(id.Value)}.history.jsonl");

    // A full disk can leave a partial last line, and ReminderHistoryStore.ReadAsync then returns no row.
    // The outcome stays the one from the occurrence status, but the operator must learn that the denial
    // check could not run.
    [Theory]
    [InlineData("restart")]
    [InlineData("sweep")]
    public async Task A_damaged_history_file_ends_the_oneshot_as_completed_and_logs_a_warning(string trigger)
    {
        await DrainStartupAsync();
        var definition = await RunAckedWithoutTombstoneAsync("history-damaged", RunMode.Succeed);
        await File.AppendAllTextAsync(HistoryPath(definition.Id), "{\"firedAt\":", TestContext.Current.CancellationToken);
        var warningsBefore = LogsOfLevel(Akka.Event.LogLevel.WarningLevel, definition.Id.Value).Count;

        await TriggerAsync(trigger);

        Assert.True(Definitions.Get(definition.Id) is null, "The one-shot must end.");
        var tombstone = RequireTombstone(definition.Id);
        Assert.Equal("Completed", OutcomeOf(tombstone));
        Assert.Null(Text(tombstone, "lastError"));
        await AwaitAssertAsync(
            () => Assert.Contains(
                LogsOfLevel(Akka.Event.LogLevel.WarningLevel, definition.Id.Value).Skip(warningsBefore),
                e => e.Message!.ToString()!.Contains("history", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("sweep")]
    public async Task A_readable_history_ends_the_oneshot_without_a_history_warning(string trigger)
    {
        await DrainStartupAsync();
        var definition = await RunAckedWithoutTombstoneAsync("history-readable", RunMode.Succeed);
        var warningsBefore = LogsOfLevel(Akka.Event.LogLevel.WarningLevel, definition.Id.Value).Count;

        await TriggerAsync(trigger);

        Assert.Equal("Completed", OutcomeOf(RequireTombstone(definition.Id)));
        Assert.DoesNotContain(
            LogsOfLevel(Akka.Event.LogLevel.WarningLevel, definition.Id.Value).Skip(warningsBefore),
            e => e.Message!.ToString()!.Contains("history", StringComparison.OrdinalIgnoreCase));
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
