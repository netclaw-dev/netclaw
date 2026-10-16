// -----------------------------------------------------------------------
// <copyright file="OneShotReadSurfaceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// What each read surface says about an ended one-shot (design D7), and who may read it. The audience of
/// the tombstone gates every read, including the prompt of a <c>Failed</c> tombstone (design D1a).
/// A test seeds the ended state on disk, so a read test does not depend on the end path.
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public sealed class OneShotReadSurfaceTests : OneShotReminderTestBase
{
    public OneShotReadSurfaceTests(ITestOutputHelper output) : base(output) { }

    private async Task<ReminderDefinition> SeedAsync(
        string id,
        string outcome = "Completed",
        TrustAudience audience = TrustAudience.Team,
        DateTimeOffset? endedAt = null)
    {
        await DrainStartupAsync();
        var definition = NewOneShot(id, audience: audience);
        await SeedEndedAsync(definition, outcome, endedAt);
        return definition;
    }

    private static Netclaw.Tools.ToolExecutionContext ToolContext(TrustAudience audience, bool interactive = false)
        => TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = audience,
            ChannelType = "manual",
            InteractiveApproval = TestToolExecutionContext.InteractiveApproval(interactive)
        });

    // ── History ──

    [Theory]
    [InlineData(TrustAudience.Team, TrustAudience.Team, true)]
    [InlineData(TrustAudience.Personal, TrustAudience.Team, true)]
    [InlineData(TrustAudience.Team, TrustAudience.Personal, false)]
    [InlineData(TrustAudience.Public, TrustAudience.Team, false)]
    public async Task History_of_an_ended_oneshot_follows_the_tombstone_audience(
        TrustAudience caller, TrustAudience tombstone, bool visible)
    {
        var definition = await SeedAsync("ended-history", audience: tombstone);

        var history = await HistoryAsync(definition.Id, CallerFor(caller));

        Assert.Equal(visible, history.Found);
        Assert.Equal(visible ? 1 : 0, history.Records.Count);
    }

    [Fact]
    public async Task Get_reminder_history_tool_reads_an_ended_oneshot_but_never_shows_its_prompt()
    {
        var definition = await SeedAsync("ended-tool", "Failed");
        var tool = new GetReminderHistoryTool(new SchedulingConfig(), Manager);

        var inside = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = definition.Id.Value },
            ToolContext(TrustAudience.Team), TestContext.Current.CancellationToken);
        var outside = await new GetReminderHistoryTool(new SchedulingConfig(), Manager).ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = definition.Id.Value },
            ToolContext(TrustAudience.Public), TestContext.Current.CancellationToken);

        Assert.Contains("fired_at:", inside, StringComparison.Ordinal);
        Assert.DoesNotContain(DefaultPrompt, inside, StringComparison.Ordinal);
        Assert.Equal($"No execution history found for reminder '{definition.Id.Value}'.", outside);
    }

    // ── Status ──

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Expired")]
    public async Task Status_of_an_ended_oneshot_reports_the_end_and_shows_the_prompt_only_for_failed(string outcome)
    {
        var endedAt = Clock.GetUtcNow().AddHours(-1);
        var definition = await SeedAsync("ended-status", outcome, endedAt: endedAt);

        var status = await StatusAsync(definition.Id, TeamCaller);

        Assert.True(status.Found);
        Assert.False(status.Enabled);
        Assert.False(status.Executing);
        var ended = ReminderEndedProtocol.ReadEnded(status, "Ended");
        Assert.NotNull(ended);
        Assert.Equal(outcome, ended.Outcome);
        Assert.Equal(endedAt.ToUnixTimeMilliseconds(), ended.EndedAt.ToUnixTimeMilliseconds());
        Assert.Equal(
            endedAt.Add(ReminderManagerActor.TerminalRetention).ToUnixTimeMilliseconds(),
            ended.PurgeAt.ToUnixTimeMilliseconds());
        Assert.Equal(outcome == "Failed" ? FailureText : null, ended.LastError);
        Assert.Equal(outcome == "Failed" ? DefaultPrompt : null, ended.Instructions);
        Assert.Single(status.RecentHistory);
    }

    [Fact]
    public async Task Status_outside_the_audience_replies_not_found_and_shows_nothing_of_the_tombstone()
    {
        var definition = await SeedAsync("ended-private", "Failed", TrustAudience.Personal);

        var status = await StatusAsync(definition.Id, TeamCaller);

        Assert.False(status.Found);
        Assert.Empty(status.RecentHistory);
        Assert.DoesNotContain(DefaultPrompt, status.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(definition.Title, status.ToString(), StringComparison.Ordinal);
    }

    // ── Get, list, enable, disable ──

    [Fact]
    public async Task Get_of_an_ended_oneshot_returns_no_definition_and_the_ended_info()
    {
        var definition = await SeedAsync("ended-get", "Failed");

        var response = await GetAsync(definition.Id, TeamCaller);

        Assert.Null(response.Reminder);
        var ended = ReminderEndedProtocol.ReadEnded(response, "Ended");
        Assert.NotNull(ended);
        Assert.Equal("Failed", ended.Outcome);
        // The prompt has one read surface: status. A get reply does not carry it.
        Assert.Null(ended.Instructions);
    }

    [Fact]
    public async Task Get_outside_the_audience_replies_as_for_an_id_that_never_existed()
    {
        var definition = await SeedAsync("ended-get-private", "Failed", TrustAudience.Personal);

        var response = await GetAsync(definition.Id, TeamCaller);
        var unknown = await GetAsync(new ReminderId("never-existed"), TeamCaller);

        Assert.Null(response.Reminder);
        Assert.Equal(unknown.ToString(), response.ToString());
    }

    [Fact]
    public async Task List_does_not_show_an_ended_oneshot()
    {
        var ended = await SeedAsync("ended-list");
        var live = NewInterval("live-interval", TimeSpan.FromMinutes(30));
        await ScheduleAsync(live);

        var list = await ListAsync(OperatorCaller);

        var info = Assert.Single(list.Reminders);
        Assert.Equal(live.Id, info.Id);
        Assert.DoesNotContain(list.Reminders, r => r.Id == ended.Id);
    }

    [Fact]
    public async Task List_reminders_tool_does_not_show_an_ended_oneshot()
    {
        var ended = await SeedAsync("ended-list-tool");
        var tool = new ListRemindersTool(Manager, new SchedulingConfig { Enabled = true });

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Filter"] = "all" },
            ToolContext(TrustAudience.Team), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(ended.Id.Value, result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enable_and_disable_of_an_ended_oneshot_reply_not_found_with_the_ended_message()
    {
        var definition = await SeedAsync("ended-toggle");

        var enable = await Manager.Ask<ReminderStateResponse>(
            new EnableReminderCommand(definition.Id), AskTimeout, TestContext.Current.CancellationToken);
        var disable = await Manager.Ask<ReminderStateResponse>(
            new DisableReminderCommand(definition.Id), AskTimeout, TestContext.Current.CancellationToken);

        foreach (var response in new[] { enable, disable })
        {
            Assert.False(response.Found);
            Assert.Contains("ended", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Null(Definitions.Get(definition.Id));
        Assert.True(TombstoneExists(definition.Id));
    }

    // ── Cancel ──

    [Fact]
    public async Task Cancel_of_an_ended_oneshot_reports_already_ended_and_changes_nothing()
    {
        var endedAt = Clock.GetUtcNow().AddHours(-2);
        var definition = await SeedAsync("ended-cancel", endedAt: endedAt);
        var tombstoneText = await File.ReadAllTextAsync(TombstonePath(definition.Id), TestContext.Current.CancellationToken);

        var cancelled = await CancelAsync(definition.Id, TeamCaller);

        Assert.True(cancelled.Found);
        var ended = ReminderEndedProtocol.ReadEnded(cancelled, "AlreadyEnded");
        Assert.NotNull(ended);
        Assert.Equal("Completed", ended.Outcome);
        Assert.Equal(endedAt.ToUnixTimeMilliseconds(), ended.EndedAt.ToUnixTimeMilliseconds());
        Assert.Equal(
            tombstoneText,
            await File.ReadAllTextAsync(TombstonePath(definition.Id), TestContext.Current.CancellationToken));
        Assert.Single(await History.ReadAsync(definition.Id, 10));
    }

    [Fact]
    public async Task Cancel_of_an_ended_oneshot_outside_the_audience_is_not_found()
    {
        var definition = await SeedAsync("ended-cancel-private", audience: TrustAudience.Personal);

        var cancelled = await CancelAsync(definition.Id, TeamCaller);
        var unknown = await CancelAsync(new ReminderId("never-existed"), TeamCaller);

        Assert.False(cancelled.Found);
        Assert.Equal(unknown with { Id = definition.Id }, cancelled);
        Assert.True(TombstoneExists(definition.Id));
    }

    // ── Permanent delete ──

    [Fact]
    public async Task Permanent_delete_removes_the_history_and_then_the_tombstone()
    {
        var definition = await SeedAsync("ended-delete", "Failed");

        var deleted = await Manager.Ask<ReminderDeletedResponse>(
            new DeleteReminderCommand(definition.Id), AskTimeout, TestContext.Current.CancellationToken);

        Assert.True(deleted.Found, "A permanent delete must find an ended one-shot by its tombstone.");
        Assert.False(TombstoneExists(definition.Id));
        Assert.Empty(await History.ReadAsync(definition.Id, 10));
        var history = await HistoryAsync(definition.Id, OperatorCaller);
        Assert.False(history.Found);
    }

    // ── Tools ──

    [Fact]
    public async Task Run_reminder_tool_says_that_an_ended_oneshot_ended_and_does_not_show_its_prompt()
    {
        var definition = await SeedAsync("spent-run", "Failed");
        var tool = new RunReminderTool(Manager, new SchedulingConfig());

        var inside = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Id"] = definition.Id.Value },
            ToolContext(TrustAudience.Team, interactive: true), TestContext.Current.CancellationToken);
        var outside = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Id"] = definition.Id.Value },
            ToolContext(TrustAudience.Public, interactive: true), TestContext.Current.CancellationToken);

        Assert.StartsWith("Error:", inside, StringComparison.Ordinal);
        Assert.Contains("ended", inside, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DefaultPrompt, inside, StringComparison.Ordinal);
        Assert.Equal($"Error: Reminder '{definition.Id.Value}' not found.", outside);
    }

    [Fact]
    public async Task Cancel_reminder_tool_names_what_the_cancel_did()
    {
        await DrainStartupAsync();
        var oneShot = NewOneShot("tool-oneshot");
        var recurring = NewInterval("tool-interval", TimeSpan.FromMinutes(30));
        await ScheduleAsync(oneShot);
        await ScheduleAsync(recurring);
        var ended = await SeedAsync("tool-ended", "Failed");
        var tool = new CancelReminderTool(Manager, new SchedulingConfig());

        async Task<string> CancelToolAsync(ReminderId id) => await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = id.Value },
            ToolContext(TrustAudience.Team), TestContext.Current.CancellationToken);

        Assert.Equal("Reminder 'tool-oneshot' cancelled and deleted.", await CancelToolAsync(oneShot.Id));
        Assert.Equal("Reminder 'tool-interval' cancelled (disabled).", await CancelToolAsync(recurring.Id));
        var again = await CancelToolAsync(ended.Id);
        Assert.Contains("already ended", again, StringComparison.Ordinal);
        Assert.DoesNotContain(DefaultPrompt, again, StringComparison.Ordinal);
        Assert.Equal("Reminder 'never-existed' not found.", await CancelToolAsync(new ReminderId("never-existed")));
    }

    // ── A tombstone that Netclaw cannot read ──

    [Fact]
    public async Task A_corrupt_tombstone_counts_as_absent_for_reads_and_raises_an_alert_and_stays_on_disk()
    {
        await DrainStartupAsync();
        var definition = NewOneShot("corrupt-tombstone");
        Directory.CreateDirectory(EndedDirectory);
        await File.WriteAllTextAsync(TombstonePath(definition.Id), "{ not json", TestContext.Current.CancellationToken);
        await History.AppendAsync(definition.Id, new HistoryRecord(Clock.GetUtcNow(), true, 1, "s", null));

        var history = await HistoryAsync(definition.Id, OperatorCaller);

        Assert.False(history.Found, "History without a readable audience must not be exposed.");
        Assert.Contains(Alerts.Alerts, a => a.Type == "reminder.tombstone.invalid" && a.Source == definition.Id.Value);
        Assert.NotEmpty(LogsOfLevel(Akka.Event.LogLevel.ErrorLevel, definition.Id.Value));
        Assert.True(TombstoneExists(definition.Id), "The file stays for the operator.");
    }
}
