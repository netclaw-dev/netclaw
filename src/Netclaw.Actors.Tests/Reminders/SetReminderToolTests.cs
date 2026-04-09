using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

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
                ["Id"] = "test-reminder",
                ["Name"] = "test-reminder",
                ["Prompt"] = "Check the server",
                ["ScheduleType"] = "once",
                ["Schedule"] = "30m"
            });
            return result;
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("test-reminder", cmd.Definition.Id);
        Assert.Equal("Check the server", cmd.Definition.Instructions);
        Assert.Equal(ReminderScheduleType.OneShot, cmd.Definition.Schedule.Type);
        Assert.Equal(ReminderWriteMode.Upsert, cmd.WriteMode);

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
                ["Id"] = "interval-check",
                ["Name"] = "interval-check",
                ["Prompt"] = "Run diagnostics",
                ["ScheduleType"] = "interval",
                ["Schedule"] = "2h"
            });
            return result;
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("interval-check", cmd.Definition.Id);
        Assert.Equal(ReminderScheduleType.Interval, cmd.Definition.Schedule.Type);
        Assert.Equal(TimeSpan.FromHours(2), cmd.Definition.Schedule.Interval);
        Assert.Equal(ReminderWriteMode.Upsert, cmd.WriteMode);

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
                ["Id"] = "cron-check",
                ["Name"] = "cron-check",
                ["Prompt"] = "Periodic scan",
                ["ScheduleType"] = "cron",
                ["Schedule"] = "0 */6 * * *"
            });
            return result;
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("cron-check", cmd.Definition.Id);
        Assert.Equal(ReminderScheduleType.Cron, cmd.Definition.Schedule.Type);
        Assert.Equal("0 */6 * * *", cmd.Definition.Schedule.CronExpression);
        Assert.Equal(ReminderWriteMode.Upsert, cmd.WriteMode);

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
            ["Id"] = "bad-cron",
            ["Name"] = "bad-cron",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "cron",
            ["Schedule"] = "not valid cron"
        }, TestContext.Current.CancellationToken);

        Assert.Contains("Error:", result);
        Assert.Contains("Invalid cron expression", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Rejects_unknown_schedule_type()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "bad-type",
            ["Name"] = "bad-type",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "weekly",
            ["Schedule"] = "1h"
        }, TestContext.Current.CancellationToken);

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
            ["Id"] = "too-fast",
            ["Name"] = "too-fast",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "interval",
            ["Schedule"] = "10s"
        }, TestContext.Current.CancellationToken);

        Assert.Contains("Error:", result);
        Assert.Contains("Minimum interval", result);
    }

    [Fact]
    public async Task Self_targeting_captures_session_id()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());
        var context = new ToolExecutionContext("C0123ABC/1234567890.123456", null)
        {
            Audience = "team"
        };

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "self-target",
                ["Name"] = "self-target",
                ["Prompt"] = "Check weather",
                ["ScheduleType"] = "once",
                ["Schedule"] = "5m"
            }, context);
            return result;
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("self-target", cmd.Definition.Id);
        Assert.Equal("C0123ABC", cmd.Definition.ReportToChannel);
        Assert.Equal("1234567890.123456", cmd.Definition.ReportToThreadTs);
        Assert.Equal("C0123ABC/1234567890.123456", cmd.Definition.SessionId);
        Assert.Equal(TrustAudience.Team, cmd.Authorization?.SourceAudience);
        Assert.Equal(ReminderWriteMode.Upsert, cmd.WriteMode);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddMinutes(5)));

        await execution;
    }

    [Fact]
    public async Task Normalizes_id_to_kebab_case()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "RAM Price Tracking",
                ["Name"] = "RAM Price Tracking",
                ["Prompt"] = "Check prices",
                ["ScheduleType"] = "interval",
                ["Schedule"] = "24h"
            });
            return result;
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ram-price-tracking", cmd.Definition.Id);
        Assert.Equal("RAM Price Tracking", cmd.Definition.Title);
        Assert.Equal(ReminderWriteMode.Upsert, cmd.WriteMode);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddHours(24)));

        await execution;
    }

    [Fact]
    public async Task Sets_audience_when_provided()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());
        var context = new ToolExecutionContext("slack/thread-1", null)
        {
            Audience = "personal"
        };

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "audience-test",
                ["Name"] = "audience-test",
                ["Prompt"] = "Search the web",
                ["ScheduleType"] = "once",
                ["Schedule"] = "30m",
                ["Audience"] = "personal"
            }, context);
            return result;
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TrustAudience.Personal, cmd.Definition.Audience);
        Assert.Equal(TrustAudience.Personal, cmd.Authorization?.SourceAudience);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddMinutes(30)));

        await execution;
    }

    [Fact]
    public async Task Rejects_invalid_audience()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "bad-audience",
            ["Name"] = "bad-audience",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["Audience"] = "superadmin"
        }, TestContext.Current.CancellationToken);

        Assert.Contains("Error:", result);
        Assert.Contains("Invalid audience", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Omitted_audience_inherits_source_audience()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());
        var context = new ToolExecutionContext("slack/thread-1", null)
        {
            Audience = "team"
        };

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "no-audience",
                ["Name"] = "no-audience",
                ["Prompt"] = "Check something",
                ["ScheduleType"] = "once",
                ["Schedule"] = "1h"
            }, context);
            return result;
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(cmd.Definition.Audience);
        Assert.Equal(TrustAudience.Team, cmd.Authorization?.SourceAudience);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddHours(1)));

        await execution;
    }

    [Fact]
    public async Task Rejects_invalid_source_audience_context()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());
        var context = new ToolExecutionContext("slack/thread-1", null)
        {
            Audience = "superadmin"
        };

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "bad-source-audience",
            ["Name"] = "bad-source-audience",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "once",
            ["Schedule"] = "1h"
        }, context, TestContext.Current.CancellationToken);

        Assert.Contains("Invalid source audience", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Manager_validation_failure_returns_error_prefix()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new ReminderConfig());
        var context = new ToolExecutionContext("slack/thread-1", null)
        {
            Audience = "team"
        };

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "manager-validation-failure",
                ["Name"] = "manager-validation-failure",
                ["Prompt"] = "Check something",
                ["ScheduleType"] = "once",
                ["Schedule"] = "1h",
                ["Audience"] = "personal"
            }, context);
            return result;
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TrustAudience.Team, cmd.Authorization?.SourceAudience);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: false,
            NextFire: null,
            Error: ReminderSaveError.Validation,
            ErrorMessage: "Requested audience 'personal' exceeds creator authority 'team' (team)."));

        var result = await execution;
        Assert.StartsWith("Error:", result);
        Assert.Contains("exceeds creator authority", result);
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
