// -----------------------------------------------------------------------
// <copyright file="OneShotRetentionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// An ended one-shot keeps its tombstone and history for 12 days after its end time (design D8), then the
/// manager deletes the history and then the tombstone. Time is virtual: no test waits for a clock.
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public sealed class OneShotRetentionTests : OneShotReminderTestBase
{
    public OneShotRetentionTests(ITestOutputHelper output) : base(output) { }

    private async Task<ReminderDefinition> SeedAsync(string id)
    {
        await DrainStartupAsync();
        var definition = NewOneShot(id);
        await SeedEndedAsync(definition, "Failed");
        return definition;
    }

    [Theory]
    [InlineData(-60_000, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(60_000, true)]
    public async Task Tombstone_and_history_are_purged_only_after_the_retention_period(long pastRetentionMs, bool purged)
    {
        var definition = await SeedAsync("purge-me");

        Clock.Advance(ReminderManagerActor.TerminalRetention + TimeSpan.FromMilliseconds(pastRetentionMs));
        await SweepAsync();

        Assert.Equal(!purged, TombstoneExists(definition.Id));
        Assert.Equal(purged ? 0 : 1, (await History.ReadAsync(definition.Id, 10)).Count);
        var history = await HistoryAsync(definition.Id, TeamCaller);
        Assert.Equal(!purged, history.Found);
    }

    [Fact]
    public async Task The_sweep_timer_purges_an_expired_tombstone_with_its_history()
    {
        var definition = await SeedAsync("timer-purge");

        // The scheduler runs the 12-hour sweep on its own while the clock moves. One step of 12 hours fires
        // one sweep. The sweep at exactly 12 days must keep the tombstone; a later sweep purges it.
        for (var step = 0; step < 25; step++)
            Advance(ReminderManagerActor.TerminalPruneInterval);
        await BarrierAsync();

        Assert.False(TombstoneExists(definition.Id), "The sweep timer must purge the tombstone.");
        Assert.Empty(await History.ReadAsync(definition.Id, 10));
        Assert.False((await HistoryAsync(definition.Id, OperatorCaller)).Found);
    }

    [Fact]
    public async Task The_purge_keeps_live_reminders_and_younger_tombstones()
    {
        await DrainStartupAsync();
        var old = NewOneShot("old-ended");
        await SeedEndedAsync(old, "Completed", endedAt: Clock.GetUtcNow().AddDays(-13));
        var recent = NewOneShot("recent-ended");
        await SeedEndedAsync(recent, "Completed", endedAt: Clock.GetUtcNow().AddDays(-11));
        var live = NewInterval("live", TimeSpan.FromMinutes(30));
        await ScheduleAsync(live);

        await SweepAsync();

        Assert.False(TombstoneExists(old.Id));
        Assert.Empty(await History.ReadAsync(old.Id, 10));
        Assert.True(TombstoneExists(recent.Id));
        Assert.Single(await History.ReadAsync(recent.Id, 10));
        Assert.NotNull(Definitions.Get(live.Id));
    }
}
