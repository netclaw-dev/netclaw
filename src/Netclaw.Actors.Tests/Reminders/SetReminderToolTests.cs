// -----------------------------------------------------------------------
// <copyright file="SetReminderToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Channels;
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
            .WithInMemorySnapshotStore()
            .WithNetclawSerialization();
    }

    [Fact]
    public async Task Schedule_oneshot_relative_time_30m()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "test-reminder",
                ["Name"] = "test-reminder",
                ["Prompt"] = "Check the server",
                ["ScheduleType"] = "once",
                ["Schedule"] = "30m",
                ["DeliveryKind"] = "none"
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
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "interval-check",
                ["Name"] = "interval-check",
                ["Prompt"] = "Run diagnostics",
                ["ScheduleType"] = "interval",
                ["Schedule"] = "2h",
                ["DeliveryKind"] = "none"
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
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "cron-check",
                ["Name"] = "cron-check",
                ["Prompt"] = "Periodic scan",
                ["ScheduleType"] = "cron",
                ["Schedule"] = "0 */6 * * *",
                ["DeliveryKind"] = "none"
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
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "bad-cron",
            ["Name"] = "bad-cron",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "cron",
            ["Schedule"] = "not valid cron",
            ["DeliveryKind"] = "none"
        }, TestContext.Current.CancellationToken);

        Assert.Contains("Error:", result);
        Assert.Contains("Invalid cron expression", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Rejects_unknown_schedule_type()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "bad-type",
            ["Name"] = "bad-type",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "weekly",
            ["Schedule"] = "1h",
            ["DeliveryKind"] = "none"
        }, TestContext.Current.CancellationToken);

        Assert.Contains("Error:", result);
        Assert.Contains("Unknown schedule type", result);
    }

    [Fact]
    public async Task Rejects_interval_under_60_seconds()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "too-fast",
            ["Name"] = "too-fast",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "interval",
            ["Schedule"] = "10s",
            ["DeliveryKind"] = "none"
        }, TestContext.Current.CancellationToken);

        Assert.Contains("Error:", result);
        Assert.Contains("Minimum interval", result);
    }

    [Fact]
    public async Task Mode_B_self_targeting_persists_session_and_origin_channel_type()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());
        var context = new ToolExecutionContext("C0123ABC/1234567890.123456", null)
        {
            Audience = "team",
            Boundary = SecurityPolicyDefaults.SlackWorkspaceBoundary,
            ChannelType = "slack"
        };

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "self-target",
                ["Name"] = "self-target",
                ["Prompt"] = "Check weather",
                ["ScheduleType"] = "once",
                ["Schedule"] = "5m",
                ["DeliveryKind"] = "current_session"
            }, context);
            return result;
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("self-target", cmd.Definition.Id);
        // CurrentSession delivery: SessionId + OriginChannelType populated in Delivery struct.
        Assert.Equal(DeliveryKind.CurrentSession, cmd.Definition.Delivery.Kind);
        Assert.Equal("C0123ABC/1234567890.123456", cmd.Definition.Delivery.SessionId);
        Assert.Equal(ChannelType.Slack, cmd.Definition.Delivery.OriginChannelType);
        Assert.Null(cmd.Definition.Delivery.Address);
        Assert.Equal(TrustAudience.Team, cmd.Authorization?.SourceAudience);
        Assert.Equal(SecurityPolicyDefaults.SlackWorkspaceBoundary, cmd.Definition.Boundary);
        Assert.Equal(ReminderWriteMode.Upsert, cmd.WriteMode);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddMinutes(5)));

        await execution;
    }

    [Fact]
    public async Task Mode_B_discord_self_targeting_persists_session_and_origin_channel_type()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());
        var context = new ToolExecutionContext("129847561203948576/130111223344556677", null)
        {
            Audience = "team",
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "discord"
        };

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "discord-self-target",
                ["Name"] = "discord-self-target",
                ["Prompt"] = "Check weather",
                ["ScheduleType"] = "once",
                ["Schedule"] = "5m",
                ["DeliveryKind"] = "current_session"
            }, context);
            return result;
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DeliveryKind.CurrentSession, cmd.Definition.Delivery.Kind);
        Assert.Equal("129847561203948576/130111223344556677", cmd.Definition.Delivery.SessionId);
        Assert.Equal(ChannelType.Discord, cmd.Definition.Delivery.OriginChannelType);
        Assert.Equal(TrustAudience.Team, cmd.Authorization?.SourceAudience);
        Assert.Equal(SecurityPolicyDefaults.TrustedInstanceBoundary, cmd.Definition.Boundary);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddMinutes(5)));

        await execution;
    }

    [Fact]
    public async Task Mode_B_rejected_for_unsupported_origin_channel_type()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());
        var context = new ToolExecutionContext("webhook/delivery-1", null)
        {
            Audience = "personal",
            ChannelType = "webhook"
        };

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "mode-b-bad-channel",
            ["Name"] = "mode-b-bad-channel",
            ["Prompt"] = "Check weather",
            ["ScheduleType"] = "once",
            ["Schedule"] = "5m",
            ["DeliveryKind"] = "current_session"
        }, context, TestContext.Current.CancellationToken);

        Assert.Contains("Error:", result);
        Assert.Contains("current_session delivery requires a channel with a gateway", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Mode_B_rejected_when_channel_type_missing_from_context()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());
        // Session id present but ChannelType is null — pre-v0.16 context
        // shape or an unusual caller. Fail loud, do not silently persist a
        // headless reminder that would drop on the floor at fire time.
        var context = new ToolExecutionContext("C0123ABC/1234567890.123456", null);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "mode-b-no-channel",
            ["Name"] = "mode-b-no-channel",
            ["Prompt"] = "check",
            ["ScheduleType"] = "once",
            ["Schedule"] = "5m",
            ["DeliveryKind"] = "current_session"
        }, context, TestContext.Current.CancellationToken);

        Assert.Contains("Error:", result);
        Assert.Contains("current_session delivery requires a channel with a gateway", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Headless_reminder_with_no_session_and_no_target_persists_with_both_null()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig(), targetResolvers: null);

        var execution = Task.Run(async () =>
        {
            return await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "headless-task",
                ["Name"] = "headless-task",
                ["Prompt"] = "Run the scan",
                ["ScheduleType"] = "once",
                ["Schedule"] = "10m",
                ["DeliveryKind"] = "none"
            }, ToolExecutionContext.Empty);
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(cmd.Definition.Delivery.SessionId);
        Assert.Null(cmd.Definition.Delivery.OriginChannelType);
        Assert.Null(cmd.Definition.Delivery.Address);
        Assert.Null(cmd.Definition.Boundary);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddMinutes(10)));

        await execution;
    }

    [Fact]
    public async Task Normalizes_id_to_kebab_case()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "RAM Price Tracking",
                ["Name"] = "RAM Price Tracking",
                ["Prompt"] = "Check prices",
                ["ScheduleType"] = "interval",
                ["Schedule"] = "24h",
                ["DeliveryKind"] = "none"
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
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());
        var context = new ToolExecutionContext("slack/thread-1", null)
        {
            Audience = "personal",
            ChannelType = "slack"
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
                ["Audience"] = "personal",
                ["DeliveryKind"] = "current_session"
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
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "bad-audience",
            ["Name"] = "bad-audience",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["Audience"] = "superadmin",
            ["DeliveryKind"] = "none"
        }, TestContext.Current.CancellationToken);

        Assert.Contains("Error:", result);
        Assert.Contains("Invalid audience", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Omitted_audience_inherits_source_audience()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());
        var context = new ToolExecutionContext("slack/thread-1", null)
        {
            Audience = "team",
            ChannelType = "slack"
        };

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "no-audience",
                ["Name"] = "no-audience",
                ["Prompt"] = "Check something",
                ["ScheduleType"] = "once",
                ["Schedule"] = "1h",
                ["DeliveryKind"] = "current_session"
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
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());
        var context = new ToolExecutionContext("slack/thread-1", null)
        {
            Audience = "superadmin",
            ChannelType = "slack"
        };

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "bad-source-audience",
            ["Name"] = "bad-source-audience",
            ["Prompt"] = "Test",
            ["ScheduleType"] = "once",
            ["Schedule"] = "1h",
            ["DeliveryKind"] = "current_session"
        }, context, TestContext.Current.CancellationToken);

        Assert.Contains("Invalid source audience", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Manager_validation_failure_returns_error_prefix()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());
        var context = new ToolExecutionContext("slack/thread-1", null)
        {
            Audience = "team",
            ChannelType = "slack"
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
                ["Audience"] = "personal",
                ["DeliveryKind"] = "current_session"
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

    [Fact]
    public async Task Manager_validation_failure_returns_error_prefix_for_discord_source()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());
        var context = new ToolExecutionContext("129847561203948576/130111223344556677", null)
        {
            Audience = "public",
            ChannelType = "discord"
        };

        var execution = Task.Run(async () =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "discord-manager-validation-failure",
                ["Name"] = "discord-manager-validation-failure",
                ["Prompt"] = "Check something",
                ["ScheduleType"] = "once",
                ["Schedule"] = "1h",
                ["Audience"] = "team",
                ["DeliveryKind"] = "current_session"
            }, context);
            return result;
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TrustAudience.Public, cmd.Authorization?.SourceAudience);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: false,
            NextFire: null,
            Error: ReminderSaveError.Validation,
            ErrorMessage: "Requested audience 'team' exceeds creator authority '129847561203948576/130111223344556677' (public)."));

        var result = await execution;
        Assert.StartsWith("Error:", result);
        Assert.Contains("exceeds creator authority", result);
    }

    [Fact]
    public async Task Resolves_hash_channel_name_to_canonical_id()
    {
        var probe = CreateTestProbe();
        var resolver = new TestResolver
        {
            ResultFor = (input) => input == "#general"
                ? new ReminderTargetResolution(true, "C0123ABC", ReminderTargetKind.Channel, null)
                : new ReminderTargetResolution(false, null, ReminderTargetKind.Unknown, $"unexpected target {input}")
        };
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig(), [resolver]);

        var execution = Task.Run(async () =>
        {
            return await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "channel-name-resolve",
                ["Name"] = "channel-name-resolve",
                ["Prompt"] = "Post status",
                ["ScheduleType"] = "once",
                ["Schedule"] = "30m",
                ["DeliveryKind"] = "channel",
                ["DeliveryTransport"] = "slack",
                ["DeliveryAddress"] = "#general"
            });
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DeliveryKind.Channel, cmd.Definition.Delivery.Kind);
        Assert.Equal("C0123ABC", cmd.Definition.Delivery.Address);
        Assert.Equal(1, resolver.CallCount);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddMinutes(30)));

        await execution;
    }

    [Fact]
    public async Task Rejects_invalid_report_to_channel_when_resolver_fails()
    {
        var probe = CreateTestProbe();
        var resolver = new TestResolver
        {
            ResultFor = (_) => new ReminderTargetResolution(
                false,
                null,
                ReminderTargetKind.Unknown,
                "Could not resolve Slack target '#nope'. Use #channel, @user, or a Slack ID (C..., G..., U...).")
        };
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig(), [resolver]);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "bad-channel",
            ["Name"] = "bad-channel",
            ["Prompt"] = "Post status",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["DeliveryKind"] = "channel",
            ["DeliveryTransport"] = "slack",
            ["DeliveryAddress"] = "#nope"
        }, TestContext.Current.CancellationToken);

        Assert.StartsWith("Error: Could not resolve delivery_address '#nope'", result);
        Assert.Contains("Could not resolve Slack target", result);
        Assert.Equal(1, resolver.CallCount);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Rejects_report_to_channel_when_no_resolver_registered()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig(), targetResolvers: null);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "no-resolver",
            ["Name"] = "no-resolver",
            ["Prompt"] = "Post status",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["DeliveryKind"] = "channel",
            ["DeliveryTransport"] = "slack",
            ["DeliveryAddress"] = "C0123ABC"
        }, TestContext.Current.CancellationToken);

        Assert.StartsWith("Error: Unknown transport 'slack'", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Rejects_channel_delivery_for_signalr_transport_with_actionable_error()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig(), targetResolvers: null);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "signalr-channel-delivery",
            ["Name"] = "signalr-channel-delivery",
            ["Prompt"] = "Post status",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["DeliveryKind"] = "channel",
            ["DeliveryTransport"] = "signalr",
            ["DeliveryAddress"] = "signalr/ops"
        }, TestContext.Current.CancellationToken);

        Assert.Equal("Error: Transport 'signalr' does not support channel delivery. Use current_session instead.", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Mode_B_session_reentry_skips_resolver()
    {
        var probe = CreateTestProbe();
        var resolver = new TestResolver
        {
            ResultFor = (_) => throw new InvalidOperationException("resolver must not be invoked for Mode B session re-entry")
        };
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig(), [resolver]);
        var context = new ToolExecutionContext("C0123ABC/1234567890.123456", null)
        {
            Audience = "team",
            ChannelType = "slack"
        };

        var execution = Task.Run(async () =>
        {
            return await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "mode-b-resolver-skip",
                ["Name"] = "mode-b-resolver-skip",
                ["Prompt"] = "Check something",
                ["ScheduleType"] = "once",
                ["Schedule"] = "5m",
                ["DeliveryKind"] = "current_session"
            }, context);
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DeliveryKind.CurrentSession, cmd.Definition.Delivery.Kind);
        Assert.Equal("C0123ABC/1234567890.123456", cmd.Definition.Delivery.SessionId);
        Assert.Equal(ChannelType.Slack, cmd.Definition.Delivery.OriginChannelType);
        Assert.Null(cmd.Definition.Delivery.Address);
        Assert.Equal(0, resolver.CallCount);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddMinutes(5)));

        await execution;
    }

    [Fact]
    public async Task Resolves_user_target_to_dm_notify_instructions()
    {
        var probe = CreateTestProbe();
        var resolver = new TestResolver
        {
            ResultFor = (input) => input == "@aaron"
                ? new ReminderTargetResolution(true, "U0456XYZ", ReminderTargetKind.User, null)
                : new ReminderTargetResolution(false, null, ReminderTargetKind.Unknown, $"unexpected target {input}")
        };
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig(), [resolver]);

        var execution = Task.Run(async () =>
        {
            return await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "user-target",
                ["Name"] = "user-target",
                ["Prompt"] = "Send results",
                ["ScheduleType"] = "once",
                ["Schedule"] = "15m",
                ["DeliveryKind"] = "channel",
                ["DeliveryTransport"] = "slack",
                ["DeliveryAddress"] = "@aaron"
            });
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DeliveryKind.Channel, cmd.Definition.Delivery.Kind);
        Assert.Equal("U0456XYZ", cmd.Definition.Delivery.Address);
        Assert.Equal(1, resolver.CallCount);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddMinutes(15)));

        await execution;
    }

    [Fact]
    public async Task Resolves_discord_user_target_with_discord_transport()
    {
        var probe = CreateTestProbe();
        var resolver = new TestResolver
        {
            Transport = "discord",
            ResultFor = (input) => input == "<@129847561203948576>"
                ? new ReminderTargetResolution(true, "129847561203948576", ReminderTargetKind.User, null)
                : new ReminderTargetResolution(false, null, ReminderTargetKind.Unknown, $"unexpected target {input}")
        };
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig(), [resolver]);

        var execution = Task.Run(async () =>
        {
            return await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "discord-user-target",
                ["Name"] = "discord-user-target",
                ["Prompt"] = "Send results",
                ["ScheduleType"] = "once",
                ["Schedule"] = "15m",
                ["DeliveryKind"] = "channel",
                ["DeliveryTransport"] = "discord",
                ["DeliveryAddress"] = "<@129847561203948576>"
            });
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DeliveryKind.Channel, cmd.Definition.Delivery.Kind);
        Assert.Equal("discord", cmd.Definition.Delivery.Transport);
        Assert.Equal("129847561203948576", cmd.Definition.Delivery.Address);
        Assert.Equal(1, resolver.CallCount);

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddMinutes(15)));

        await execution;
    }

    [Fact]
    public async Task Rejects_resolver_success_with_empty_resolved_id()
    {
        var probe = CreateTestProbe();
        var resolver = new TestResolver
        {
            ResultFor = (_) => new ReminderTargetResolution(true, null, ReminderTargetKind.Channel, null)
        };
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig(), [resolver]);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "empty-target-id",
            ["Name"] = "empty-target-id",
            ["Prompt"] = "Send status",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["DeliveryKind"] = "channel",
            ["DeliveryTransport"] = "slack",
            ["DeliveryAddress"] = "#general"
        }, TestContext.Current.CancellationToken);

        Assert.Contains("resolver returned an empty canonical target ID", result);
        Assert.Equal(1, resolver.CallCount);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    private sealed class TestResolver : IReminderTargetResolver
    {
        public required Func<string, ReminderTargetResolution> ResultFor { get; init; }
        public string Transport { get; init; } = "slack";
        public int CallCount { get; private set; }

        public Task<ReminderTargetResolution> ResolveAsync(string target, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(ResultFor(target));
        }
    }

    [Fact]
    public async Task ExpiresIn_sets_expiration_on_interval_reminder()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());

        var execution = Task.Run(async () =>
        {
            return await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Id"] = "expiring-check",
                ["Name"] = "expiring-check",
                ["Prompt"] = "Check status",
                ["ScheduleType"] = "interval",
                ["Schedule"] = "30m",
                ["DeliveryKind"] = "none",
                ["ExpiresIn"] = "24h"
            });
        });

        var cmd = await probe.ExpectMsgAsync<SaveReminderCommand>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(cmd.Definition.ExpiresAt);
        var expectedExpires = _timeProvider.GetUtcNow().AddHours(24);
        Assert.Equal(expectedExpires, cmd.Definition.ExpiresAt.Value, TimeSpan.FromSeconds(1));

        probe.Reply(new ReminderSavedResponse(
            new ReminderId(cmd.Definition.Id),
            cmd.Definition.Title,
            Success: true,
            NextFire: _timeProvider.GetUtcNow().AddMinutes(30)));

        var result = await execution;
        Assert.Contains("Expires:", result);
    }

    [Fact]
    public async Task ExpiresIn_rejects_on_oneshot_reminder()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "oneshot-no-expire",
            ["Name"] = "oneshot-no-expire",
            ["Prompt"] = "Check once",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["DeliveryKind"] = "none",
            ["ExpiresIn"] = "24h"
        }, TestContext.Current.CancellationToken);

        Assert.Contains("not applicable to one-shot", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExpiresIn_rejects_unparseable_duration()
    {
        var probe = CreateTestProbe();
        var tool = new SetReminderTool(probe, _timeProvider, new SchedulingConfig());

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "bad-expires",
            ["Name"] = "bad-expires",
            ["Prompt"] = "Check status",
            ["ScheduleType"] = "interval",
            ["Schedule"] = "1h",
            ["DeliveryKind"] = "none",
            ["ExpiresIn"] = "next tuesday"
        }, TestContext.Current.CancellationToken);

        Assert.Contains("Cannot parse expires_in", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

}
