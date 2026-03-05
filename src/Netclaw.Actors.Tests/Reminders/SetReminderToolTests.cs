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

        _ = Task.Run(async () =>
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

        var cmd = probe.ExpectMsg<ScheduleReminderCommand>(TimeSpan.FromSeconds(5));
        Assert.Equal("Check the server", cmd.Payload.Prompt);
        Assert.Equal(ReminderScheduleType.OneShot, cmd.Payload.Schedule.Type);

        var expectedFire = _timeProvider.GetUtcNow().AddMinutes(30);
        Assert.NotNull(cmd.Payload.Schedule.FireAt);
        Assert.Equal(expectedFire, cmd.Payload.Schedule.FireAt.Value, TimeSpan.FromSeconds(1));

        // Reply
        probe.Reply(new ReminderScheduledResponse(cmd.Payload.Id, cmd.Payload.Name, expectedFire));
    }

    [Fact]
    public async Task Schedule_interval_2h()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());

        _ = Task.Run(async () =>
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

        var cmd = probe.ExpectMsg<ScheduleReminderCommand>(TimeSpan.FromSeconds(5));
        Assert.Equal(ReminderScheduleType.Interval, cmd.Payload.Schedule.Type);
        Assert.Equal(TimeSpan.FromHours(2), cmd.Payload.Schedule.Interval);

        probe.Reply(new ReminderScheduledResponse(cmd.Payload.Id, cmd.Payload.Name,
            _timeProvider.GetUtcNow().AddHours(2)));
    }

    [Fact]
    public async Task Schedule_cron_every_6_hours()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());

        _ = Task.Run(async () =>
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

        var cmd = probe.ExpectMsg<ScheduleReminderCommand>(TimeSpan.FromSeconds(5));
        Assert.Equal(ReminderScheduleType.Cron, cmd.Payload.Schedule.Type);
        Assert.Equal("0 */6 * * *", cmd.Payload.Schedule.CronExpression);

        probe.Reply(new ReminderScheduledResponse(cmd.Payload.Id, cmd.Payload.Name,
            new DateTimeOffset(2026, 3, 5, 18, 0, 0, TimeSpan.Zero)));
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

        _ = Task.Run(async () =>
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

        var cmd = probe.ExpectMsg<ScheduleReminderCommand>(TimeSpan.FromSeconds(5));
        Assert.Equal("C0123ABC", cmd.Payload.ReportToChannel);
        Assert.Equal("1234567890.123456", cmd.Payload.ReportToThreadTs);
        Assert.NotNull(cmd.Payload.OriginatingSessionId);
        Assert.Equal("C0123ABC/1234567890.123456", cmd.Payload.OriginatingSessionId.Value.Value);

        probe.Reply(new ReminderScheduledResponse(cmd.Payload.Id, cmd.Payload.Name,
            _timeProvider.GetUtcNow().AddMinutes(5)));
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
