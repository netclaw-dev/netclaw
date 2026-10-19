// -----------------------------------------------------------------------
// <copyright file="OneShotUpgradeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// The first start of the new daemon (and the 12-hour sweep after it) ends every one-shot that the old
/// version left behind as a definition (design D8). The fixtures are the on-disk shapes that the
/// previous version wrote.
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public sealed class OneShotUpgradeTests : OneShotReminderTestBase
{
    public OneShotUpgradeTests(ITestOutputHelper output) : base(output) { }

    private ReminderDefinition Leftover(
        string id,
        ReminderTerminalOutcome? outcome,
        TimeSpan updatedAgo,
        TimeSpan fireOffset,
        bool enabled = false)
    {
        var now = Clock.GetUtcNow();
        return NewOneShot(id) with
        {
            Enabled = enabled,
            TerminalOutcome = outcome,
            ConsecutiveFailures = outcome == ReminderTerminalOutcome.Failed ? ReminderManagerActor.FailurePauseThreshold : 0,
            UpdatedAtMs = (now - updatedAgo).ToUnixTimeMilliseconds(),
            Schedule = new ReminderSchedule { Type = ReminderScheduleType.OneShot, FireAt = now + fireOffset }
        };
    }

    private async Task SaveLeftoverAsync(ReminderDefinition definition)
    {
        Definitions.Save(definition);
        await History.AppendAsync(
            definition.Id,
            new HistoryRecord(Clock.GetUtcNow(), definition.TerminalOutcome != ReminderTerminalOutcome.Failed, 5, "s", null));
    }

    [Fact]
    public async Task Upgrade_reconcile_ends_leftover_oneshots_and_keeps_live_reminders()
    {
        await DrainStartupAsync();
        var future = NewOneShot("enabled-future", fireIn: TimeSpan.FromDays(1));
        await ScheduleAsync(future);
        var oldCompleted = Leftover("old-completed", ReminderTerminalOutcome.Completed, TimeSpan.FromDays(20), TimeSpan.FromDays(-20));
        var recentFailed = Leftover("recent-failed", ReminderTerminalOutcome.Failed, TimeSpan.FromDays(2), TimeSpan.FromDays(-2));
        var recentCompleted = Leftover("recent-completed", ReminderTerminalOutcome.Completed, TimeSpan.FromDays(2), TimeSpan.FromDays(-2));
        var oldDisabled = Leftover("old-disabled", null, TimeSpan.FromDays(40), TimeSpan.FromDays(-30));
        var recentDisabled = Leftover("recent-disabled", null, TimeSpan.FromDays(5), TimeSpan.FromDays(-3));
        var pausedFuture = Leftover("paused-future", null, TimeSpan.FromDays(1), TimeSpan.FromDays(1));
        var disabledInterval = NewInterval("disabled-interval", TimeSpan.FromHours(1)) with { Enabled = false };
        var failedCron = NewOneShot("failed-cron") with
        {
            Enabled = false,
            ConsecutiveFailures = ReminderManagerActor.FailurePauseThreshold,
            TerminalOutcome = ReminderTerminalOutcome.Failed,
            Schedule = new ReminderSchedule { Type = ReminderScheduleType.Cron, CronExpression = "0 0 1 1 *" }
        };
        var livedBefore = new[] { pausedFuture, disabledInterval, failedCron };
        var leftovers = new[] { oldCompleted, recentFailed, recentCompleted, oldDisabled, recentDisabled };

        // The daemon stops, the files stay as the old version left them, and the new daemon starts.
        await StopManagerAsync();
        foreach (var definition in leftovers.Concat(livedBefore))
            await SaveLeftoverAsync(definition);
        await StartManagerAsync();

        foreach (var definition in leftovers)
            Assert.True(Definitions.Get(definition.Id) is null, $"'{definition.Id.Value}' must end at the upgrade.");

        // Older than 12 days: purged in the same pass, with the history.
        foreach (var old in new[] { oldCompleted, oldDisabled })
        {
            Assert.False(TombstoneExists(old.Id), $"'{old.Id.Value}' ended more than 12 days ago.");
            Assert.Empty(await History.ReadAsync(old.Id, 10));
        }

        var failed = RequireTombstone(recentFailed.Id);
        Assert.Equal("Failed", OutcomeOf(failed));
        Assert.Equal(recentFailed.UpdatedAtMs, Number(failed, "endedAtMs"));
        Assert.Equal(DefaultPrompt, Text(failed, "instructions"));
        Assert.False(string.IsNullOrWhiteSpace(Text(failed, "lastError")));
        Assert.Single(await History.ReadAsync(recentFailed.Id, 10));

        var completed = RequireTombstone(recentCompleted.Id);
        Assert.Equal("Completed", OutcomeOf(completed));
        Assert.Equal(recentCompleted.UpdatedAtMs, Number(completed, "endedAtMs"));
        Assert.False(TryGet(completed, "instructions", out _));

        var cancelled = RequireTombstone(recentDisabled.Id);
        Assert.Equal("Cancelled", OutcomeOf(cancelled));
        Assert.Contains("not recorded", Text(cancelled, "lastError"), StringComparison.OrdinalIgnoreCase);
        // The end time is the later of the last update and the fire time.
        Assert.Equal(recentDisabled.Schedule.FireAtMs, Number(cancelled, "endedAtMs"));
        Assert.False(TryGet(cancelled, "instructions", out _));

        // Live reminders do not change, and nothing writes a tombstone for them.
        foreach (var live in livedBefore)
        {
            Assert.Equal(live, Definitions.Get(live.Id));
            Assert.False(TombstoneExists(live.Id));
        }

        Assert.True(Definitions.Get(future.Id)!.Enabled);
        Assert.True(await IsScheduledAsync(future.Id));
        Assert.False(TombstoneExists(future.Id));

        // The log line counts the ends and the purges. The 20-day-old leftovers count in both.
        Assert.Contains(
            await LogsAboutAsync("Reminder reconcile complete"),
            e => e.Message.ToString()!.Contains("ended_oneshots=5", StringComparison.Ordinal)
                 && e.Message.ToString()!.Contains("purged_tombstones=2", StringComparison.Ordinal));

        // A paused one-shot with a future fire time can still start again.
        var enabled = await Manager.Ask<ReminderStateResponse>(
            new EnableReminderCommand(pausedFuture.Id), AskTimeout, TestContext.Current.CancellationToken);
        Assert.True(enabled.Enabled, enabled.ErrorMessage);
        Assert.True(await IsScheduledAsync(pausedFuture.Id));
    }

    [Fact]
    public async Task The_sweep_ends_leftovers_that_appear_after_startup()
    {
        await DrainStartupAsync();
        var completed = Leftover("late-completed", ReminderTerminalOutcome.Completed, TimeSpan.FromDays(1), TimeSpan.FromDays(-1));
        var disabledPast = Leftover("late-disabled", null, TimeSpan.FromDays(4), TimeSpan.FromDays(-2));
        var pausedFuture = Leftover("late-paused", null, TimeSpan.FromDays(1), TimeSpan.FromDays(1));
        foreach (var definition in new[] { completed, disabledPast, pausedFuture })
            await SaveLeftoverAsync(definition);

        await SweepAsync();

        Assert.True(Definitions.Get(completed.Id) is null);
        Assert.Equal("Completed", OutcomeOf(RequireTombstone(completed.Id)));
        Assert.True(Definitions.Get(disabledPast.Id) is null);
        Assert.Equal("Cancelled", OutcomeOf(RequireTombstone(disabledPast.Id)));
        Assert.Equal(pausedFuture, Definitions.Get(pausedFuture.Id));
        Assert.False(TombstoneExists(pausedFuture.Id));
    }

    [Fact]
    public async Task A_paused_oneshot_ends_as_cancelled_when_its_fire_time_passes()
    {
        await DrainStartupAsync();
        var paused = NewOneShot("paused-oneshot", fireIn: TimeSpan.FromHours(6));
        await ScheduleAsync(paused);
        var disabled = await Manager.Ask<ReminderStateResponse>(
            new DisableReminderCommand(paused.Id), AskTimeout, TestContext.Current.CancellationToken);
        Assert.True(disabled.Found);

        await SweepAsync();
        Assert.NotNull(Definitions.Get(paused.Id));
        Assert.False(TombstoneExists(paused.Id));

        Clock.Advance(TimeSpan.FromHours(7));
        await SweepAsync();

        Assert.True(Definitions.Get(paused.Id) is null);
        Assert.Equal("Cancelled", OutcomeOf(RequireTombstone(paused.Id)));
    }
}
