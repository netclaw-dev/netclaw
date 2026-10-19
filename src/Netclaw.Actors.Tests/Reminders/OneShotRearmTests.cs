// -----------------------------------------------------------------------
// <copyright file="OneShotRearmTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// A create or an update with the id of an ended one-shot starts a new reminder (design D6). This
/// includes the restart-resume reminders, which a restart recovery saves again with <c>CreateOnly</c>,
/// and a channel watcher that sets itself again.
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public sealed class OneShotRearmTests : OneShotReminderTestBase
{
    public OneShotRearmTests(ITestOutputHelper output) : base(output) { }

    private async Task<ReminderDefinition> SeedAsync(
        string id, string outcome = "Completed", TrustAudience audience = TrustAudience.Team)
    {
        await DrainStartupAsync();
        var definition = NewOneShot(id, audience: audience);
        await SeedEndedAsync(definition, outcome);
        return definition;
    }

    [Theory]
    [InlineData(ReminderWriteMode.CreateOnly)]
    [InlineData(ReminderWriteMode.Replace)]
    [InlineData(ReminderWriteMode.Upsert)]
    public async Task Rearm_after_the_end_discards_the_tombstone_and_history(ReminderWriteMode mode)
    {
        var ended = await SeedAsync("check-pr-42");
        var again = NewOneShot("check-pr-42", fireIn: TimeSpan.FromHours(2), instructions: "Check PR 42 again.");

        var saved = await SaveAsync(again, mode);

        Assert.True(saved.Success, saved.ErrorMessage);
        var stored = Definitions.Get(ended.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled);
        Assert.Equal(again.Schedule.FireAtMs, stored.Schedule.FireAtMs);
        Assert.False(TombstoneExists(ended.Id), "The tombstone of the old reminder must go when its id is used again.");
        Assert.Empty(await History.ReadAsync(ended.Id, 10));
        Assert.True(await IsScheduledAsync(ended.Id));
    }

    [Fact]
    public async Task Rearm_of_a_failed_oneshot_discards_its_prompt()
    {
        var ended = await SeedAsync("failed-rearm", "Failed");
        Assert.Equal(DefaultPrompt, Text(RequireTombstone(ended.Id), "instructions"));

        var saved = await SaveAsync(NewOneShot("failed-rearm", instructions: "A new prompt."));

        Assert.True(saved.Success, saved.ErrorMessage);
        var filesWithOldPrompt = Directory.EnumerateFiles(Paths.BasePath, "*", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains(DefaultPrompt, StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(filesWithOldPrompt);
    }

    [Fact]
    public async Task Rearm_over_a_corrupt_tombstone_replaces_it()
    {
        await DrainStartupAsync();
        var definition = NewOneShot("corrupt-rearm");
        Directory.CreateDirectory(EndedDirectory);
        await File.WriteAllTextAsync(TombstonePath(definition.Id), "{ not json", TestContext.Current.CancellationToken);

        var saved = await SaveAsync(definition);

        Assert.True(saved.Success, saved.ErrorMessage);
        Assert.False(TombstoneExists(definition.Id));
    }

    [Theory]
    [InlineData(ReminderWriteMode.CreateOnly)]
    [InlineData(ReminderWriteMode.Replace)]
    [InlineData(ReminderWriteMode.Upsert)]
    public async Task A_tombstone_outside_the_audience_blocks_the_id(ReminderWriteMode mode)
    {
        var ended = await SeedAsync("nightly", audience: TrustAudience.Personal);
        var tombstoneText = await File.ReadAllTextAsync(TombstonePath(ended.Id), TestContext.Current.CancellationToken);

        var saved = await SaveAsync(NewOneShot("nightly"), mode, TeamCaller);

        Assert.False(saved.Success, "A Team caller must not use the id of a Personal tombstone.");
        Assert.Equal(ReminderSaveError.NotFound, saved.Error);
        Assert.Equal("Reminder 'nightly' was not found.", saved.ErrorMessage);
        Assert.Equal(
            tombstoneText,
            await File.ReadAllTextAsync(TombstonePath(ended.Id), TestContext.Current.CancellationToken));
        Assert.Single(await History.ReadAsync(ended.Id, 10));
        Assert.Null(Definitions.Get(ended.Id));
        Assert.False(await IsScheduledAsync(ended.Id));
    }

    [Fact]
    public async Task A_failed_schedule_keeps_the_old_tombstone_and_history()
    {
        var ended = await SeedAsync("rearm-late");

        var saved = await SaveAsync(NewOneShot("rearm-late", fireIn: TimeSpan.FromMinutes(-5)));

        Assert.False(saved.Success);
        Assert.True(TombstoneExists(ended.Id));
        Assert.Single(await History.ReadAsync(ended.Id, 10));
        Assert.Null(Definitions.Get(ended.Id));
    }

    [Fact]
    public async Task Set_reminder_tool_starts_the_same_id_again_after_the_run_ended_it()
    {
        await DrainStartupAsync();
        Pipeline.Mode = RunMode.Succeed;
        var first = NewOneShot("watcher");
        await ScheduleAsync(first);
        Advance(FirstFireDelay);
        await AwaitSettledRunAsync(first.Id, 1);
        Assert.True(Definitions.Get(first.Id) is null, "The first run must end the one-shot.");
        var tool = new SetReminderTool(Manager, Clock, new SchedulingConfig());

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = "watcher",
                ["Name"] = "watcher",
                ["Prompt"] = "Check the build again.",
                ["ScheduleType"] = "once",
                ["Schedule"] = "30m",
                ["DeliveryKind"] = "none"
            },
            TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions { Audience = TrustAudience.Team, ChannelType = "manual" }),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Error", result, StringComparison.Ordinal);
        Assert.Contains("deletes itself", result, StringComparison.Ordinal);
        Assert.Empty(await History.ReadAsync(first.Id, 10));
        Advance(TimeSpan.FromMinutes(30));
        await AwaitSettledRunAsync(first.Id, 1);
        Assert.True(Definitions.Get(first.Id) is null);
        var tombstone = RequireTombstone(first.Id);
        Assert.Equal("Completed", OutcomeOf(tombstone));
        Assert.NotEqual(first.Schedule.FireAtMs, Number(tombstone, "fireAtMs"));
    }
}
