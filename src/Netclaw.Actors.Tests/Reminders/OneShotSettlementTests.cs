// -----------------------------------------------------------------------
// <copyright file="OneShotSettlementTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Netclaw.Actors.Reminders;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// A settlement ends only the occurrence that it ran (design D5), and a tombstone that matches its
/// definition wins over the definition (design D4). A run stays active here with
/// <see cref="OneShotReminderTestBase.RunMode.Hold"/>, so each test changes the reminder during the run
/// and then reports the run outcome to the manager.
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public sealed class OneShotSettlementTests : OneShotReminderTestBase
{
    public OneShotSettlementTests(ITestOutputHelper output) : base(output) { }

    private async Task<(ReminderDefinition Definition, Guid Run)> StartHeldRunAsync(string id)
    {
        await DrainStartupAsync();
        Pipeline.Mode = RunMode.Hold;
        var definition = NewOneShot(id);
        await ScheduleAsync(definition);
        Advance(FirstFireDelay);
        return (definition, await AwaitDispatchAsync());
    }

    private async Task<ReminderDefinition> RearmAsync(ReminderDefinition original)
    {
        var replacement = original with
        {
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = Clock.GetUtcNow() + TimeSpan.FromMinutes(10)
            }
        };
        var saved = await SaveAsync(replacement);
        Assert.True(saved.Success, saved.ErrorMessage);
        return replacement;
    }

    // ── D5: the settled occurrence is the current one ──

    [Fact]
    public async Task Rearm_during_the_run_keeps_the_new_definition()
    {
        var (original, run) = await StartHeldRunAsync("ci-poll");
        var replacement = await RearmAsync(original);

        await CompleteRunAsync(run, original.Id, RunOutcome.Success);

        var stored = Definitions.Get(original.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled, "The settlement of the old run disabled the new definition.");
        Assert.Equal(replacement.Schedule.FireAtMs, stored.Schedule.FireAtMs);
        Assert.Null(stored.TerminalOutcome);
        Assert.True(await IsScheduledAsync(original.Id), "The settlement of the old run cancelled the new schedule.");
        Assert.False(TombstoneExists(original.Id));
        Assert.Single(await History.ReadAsync(original.Id, 10));
    }

    [Theory]
    [InlineData(RunOutcome.Success)]
    [InlineData(RunOutcome.Failure)]
    [InlineData(RunOutcome.Denied)]
    public async Task No_run_outcome_changes_a_definition_that_replaced_the_one_that_ran(RunOutcome outcome)
    {
        var (original, run) = await StartHeldRunAsync("ci-poll");
        var replacement = await RearmAsync(original);
        var before = Definitions.Get(original.Id);

        await CompleteRunAsync(run, original.Id, outcome);

        Assert.Equal(before, Definitions.Get(original.Id));
        Assert.Equal(replacement.Schedule.FireAtMs, Definitions.Get(original.Id)!.Schedule.FireAtMs);
        Assert.True(await IsScheduledAsync(original.Id));
        Assert.False(TombstoneExists(original.Id));
        Assert.Single(await History.ReadAsync(original.Id, 10));

        // A failed run of the old definition still tells the owner, but it does not count against the new one.
        var failureAlerts = Alerts.Alerts
            .Where(a => a.Type == "reminder.execution.failed" && a.Source == original.Id.Value)
            .ToArray();
        if (outcome == RunOutcome.Failure)
            Assert.Single(failureAlerts);
        else
            Assert.Empty(failureAlerts);
        Assert.DoesNotContain(Alerts.Alerts, a => a.Type == "reminder.auto_disabled");
    }

    [Fact]
    public async Task Settled_occurrence_that_matches_the_definition_ends_it()
    {
        var (definition, run) = await StartHeldRunAsync("ci-poll");

        await CompleteRunAsync(run, definition.Id, RunOutcome.Success);

        Assert.True(Definitions.Get(definition.Id) is null, "The definition of the settled occurrence still exists.");
        Assert.Equal("Completed", OutcomeOf(RequireTombstone(definition.Id)));
        Assert.False(await IsScheduledAsync(definition.Id));
    }

    [Fact]
    public async Task Cancel_during_the_run_keeps_the_cancelled_tombstone()
    {
        var (definition, run) = await StartHeldRunAsync("ci-poll");
        var cancelled = await CancelAsync(definition.Id, TeamCaller);
        Assert.True(cancelled.Found);
        RequireTombstone(definition.Id);
        var cancelledTombstone = await File.ReadAllTextAsync(
            TombstonePath(definition.Id), TestContext.Current.CancellationToken);

        await CompleteRunAsync(run, definition.Id, RunOutcome.Success);

        Assert.Equal(
            cancelledTombstone,
            await File.ReadAllTextAsync(TombstonePath(definition.Id), TestContext.Current.CancellationToken));
        Assert.Equal("Cancelled", OutcomeOf(RequireTombstone(definition.Id)));
        Assert.Null(Definitions.Get(definition.Id));
        Assert.Single(await History.ReadAsync(definition.Id, 10));
    }

    // ── D5 at the fire boundary: a fire runs only the occurrence of the current definition ──

    // A one-shot ends, and its ack and schedule cancel both fail. The id is set again for a later time.
    // The old occurrence stays in Akka.Reminders, and its lease expires. The test puts that occurrence
    // in the scheduler directly. The in-memory store keeps one row for each reminder key, so this test
    // cannot also prove that the schedule of the current definition survives the refusal.
    [Fact]
    public async Task A_stale_occurrence_of_an_earlier_definition_is_acknowledged_and_does_not_run_early()
    {
        await DrainStartupAsync();
        // A run that started would stay active here, so the status below shows it at once.
        Pipeline.Mode = RunMode.Hold;
        var current = NewOneShot("ci-poll", fireIn: TimeSpan.FromMinutes(10));
        await ScheduleAsync(current);
        var staleDue = Clock.GetUtcNow() + FirstFireDelay;
        await ScheduleDirectlyAsync(current.Id, staleDue);

        Advance(FirstFireDelay);
        await AwaitEnvelopesHandledAsync(1);

        Assert.False(
            (await StatusAsync(current.Id, TeamCaller)).Executing,
            "The occurrence of an earlier definition must not run the new definition early.");
        Assert.Equal(0, Pipeline.InvocationCount);
        Assert.Equal("Delivered", await OccurrenceStatusAsync(current.Id, staleDue));
        var stored = Definitions.Get(current.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled);
        Assert.Equal(current.Schedule.FireAtMs, stored.Schedule.FireAtMs);
        Assert.False(TombstoneExists(current.Id));
        Assert.NotEmpty(LogsOfLevel(Akka.Event.LogLevel.WarningLevel, current.Id.Value));
    }

    // ── D4: a tombstone that matches its definition wins ──

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    [InlineData(3_600_000, false)]
    public async Task Reconcile_finishes_a_definition_only_when_the_tombstone_matches_to_the_millisecond(
        long fireTimeDeltaMs, bool matches)
    {
        await DrainStartupAsync();
        var definition = NewOneShot("matching");
        await ScheduleAsync(definition);
        WriteTombstone(
            definition,
            "Completed",
            Clock.GetUtcNow(),
            fireAt: definition.Schedule.FireAt!.Value.AddMilliseconds(fireTimeDeltaMs));
        var tombstoneText = await File.ReadAllTextAsync(TombstonePath(definition.Id), TestContext.Current.CancellationToken);

        await ReconcileAsync();

        if (matches)
        {
            Assert.True(Definitions.Get(definition.Id) is null, "A definition with a matching tombstone must be deleted.");
            Assert.False(await IsScheduledAsync(definition.Id));
            Assert.Equal(
                tombstoneText,
                await File.ReadAllTextAsync(TombstonePath(definition.Id), TestContext.Current.CancellationToken));
            return;
        }

        var stored = Definitions.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled);
        Assert.True(await IsScheduledAsync(definition.Id));
        Assert.False(TombstoneExists(definition.Id), "A stale tombstone must be deleted.");
        Assert.Contains(
            LogsOfLevel(Akka.Event.LogLevel.WarningLevel, definition.Id.Value),
            e => e.Message.ToString()!.Contains("tombstone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Stale_tombstone_does_not_block_a_newer_definition()
    {
        await DrainStartupAsync();
        var definition = NewOneShot("newer", fireIn: TimeSpan.FromHours(1));
        await ScheduleAsync(definition);
        // The tombstone belongs to an earlier occurrence of the same id: one hour before the definition.
        WriteTombstone(
            definition,
            "Completed",
            Clock.GetUtcNow(),
            fireAt: definition.Schedule.FireAt!.Value.AddHours(-1));

        await SweepAsync();

        var stored = Definitions.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled);
        Assert.True(await IsScheduledAsync(definition.Id));
        Assert.False(TombstoneExists(definition.Id));
        Assert.NotEmpty(LogsOfLevel(Akka.Event.LogLevel.WarningLevel, definition.Id.Value));
    }

    [Fact]
    public async Task A_tombstone_never_matches_a_recurring_definition()
    {
        await DrainStartupAsync();
        var definition = NewInterval("recurring-twin", TimeSpan.FromMinutes(30));
        await ScheduleAsync(definition);
        WriteTombstone(
            definition,
            "Completed",
            Clock.GetUtcNow(),
            fireAt: Definitions.Get(definition.Id)!.Schedule.FireAt!.Value);

        await ReconcileAsync();

        var stored = Definitions.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled);
        Assert.True(await IsScheduledAsync(definition.Id));
        Assert.False(TombstoneExists(definition.Id), "A tombstone that cannot match a recurring definition is stale.");
    }
}
