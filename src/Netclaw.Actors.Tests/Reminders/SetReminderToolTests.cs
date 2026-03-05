using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Reminders;

public class SetReminderToolTests : TestKit
{
    private readonly FakeTimeProvider _timeProvider = new(
        new DateTimeOffset(2026, 3, 5, 12, 0, 0, TimeSpan.Zero));

    public SetReminderToolTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore();
    }

    [Fact]
    public async Task Schedule_oneshot_relative_time_30m()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Name"] = "test-reminder",
                ["Prompt"] = "Check the server",
                ["ScheduleType"] = "once",
                ["Schedule"] = "30m"
            });
            return result;
        });

        var cmd = probe.ExpectMsg<SaveReminderCommand>(TimeSpan.FromSeconds(5));
        Assert.Equal("Check the server", cmd.Definition.Instructions);
        Assert.Equal(ReminderScheduleType.OneShot, cmd.Definition.Schedule.Type);

        var expectedFire = _timeProvider.GetUtcNow().AddMinutes(30);
        Assert.NotNull(cmd.Definition.Schedule.FireAt);
        Assert.Equal(expectedFire, cmd.Definition.Schedule.FireAt.Value, TimeSpan.FromSeconds(1));

        // Reply
        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: expectedFire));

        await execution;
    }

    [Fact]
    public async Task Schedule_interval_2h()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Name"] = "interval-check",
                ["Prompt"] = "Run diagnostics",
                ["ScheduleType"] = "interval",
                ["Schedule"] = "2h"
            });
            return result;
        });

        var cmd = probe.ExpectMsg<SaveReminderCommand>(TimeSpan.FromSeconds(5));
        Assert.Equal(ReminderScheduleType.Interval, cmd.Definition.Schedule.Type);
        Assert.Equal(TimeSpan.FromHours(2), cmd.Definition.Schedule.Interval);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddHours(2)));

        await execution;
    }

    [Fact]
    public async Task Schedule_cron_every_6_hours()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Name"] = "cron-check",
                ["Prompt"] = "Periodic scan",
                ["ScheduleType"] = "cron",
                ["Schedule"] = "0 */6 * * *"
            });
            return result;
        });

        var cmd = probe.ExpectMsg<SaveReminderCommand>(TimeSpan.FromSeconds(5));
        Assert.Equal(ReminderScheduleType.Cron, cmd.Definition.Schedule.Type);
        Assert.Equal("0 */6 * * *", cmd.Definition.Schedule.CronExpression);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: new DateTimeOffset(2026, 3, 5, 18, 0, 0, TimeSpan.Zero)));

        await execution;
    }

    [Fact]
    public async Task Rejects_invalid_cron_expression()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Name"] = "bad-cron",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "cron",
            ["Schedule"] = "not valid cron"
        });

        Assert.Contains("Error:", result);
        Assert.Contains("Invalid cron expression", result);
        probe.ExpectNoMsg(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task Rejects_unknown_schedule_type()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Name"] = "bad-type",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "weekly",
            ["Schedule"] = "1h"
        });

        Assert.Contains("Error:", result);
        Assert.Contains("Unknown schedule type", result);
    }

    [Fact]
    public async Task Rejects_interval_under_60_seconds()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Name"] = "too-fast",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "interval",
            ["Schedule"] = "10s"
        });

        Assert.Contains("Error:", result);
        Assert.Contains("Minimum interval", result);
    }

    [Fact]
    public async Task Self_targeting_captures_session_id()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());
        var context = new ToolExecutionContext("C0123ABC/1234567890.123456", null);

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Name"] = "self-target",
                ["Prompt"] = "Check weather",
                ["ScheduleType"] = "once",
                ["Schedule"] = "5m"
            }, context);
            return result;
        });

        var cmd = probe.ExpectMsg<SaveReminderCommand>(TimeSpan.FromSeconds(5));
        Assert.Equal("C0123ABC", cmd.Definition.ReportToChannel);
        Assert.Equal("1234567890.123456", cmd.Definition.ReportToThreadTs);
        Assert.Equal("C0123ABC/1234567890.123456", cmd.Definition.SessionId);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddMinutes(5)));

        await execution;
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
