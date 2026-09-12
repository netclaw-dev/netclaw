// -----------------------------------------------------------------------
// <copyright file="ReminderTargetResolutionPathTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Daemon.Tests.Reminder;

public sealed class ReminderTargetResolutionPathTests : IDisposable
{
    private readonly ActorSystem _system;
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    public ReminderTargetResolutionPathTests()
    {
        _system = ActorSystem.Create($"reminder-target-resolution-tests-{Guid.NewGuid():N}");
    }

    [Fact]
    public async Task Channel_delivery_returns_resolution_error_when_resolver_fails()
    {
        var capture = new CaptureSink();
        var probe = _system.ActorOf(Props.Create(() => new CapturingReminderActor(capture, success: true)));
        var resolver = new StubReminderTargetResolver(
            _ => new ReminderTargetResolution(false, null, ReminderTargetKind.Unknown, "Could not resolve Slack target '#nope'."));

        var tool = CreateTool(probe, resolver);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "channel-delivery-error",
            ["Name"] = "channel-delivery-error",
            ["Prompt"] = "check status",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["DeliveryKind"] = "channel",
            ["DeliveryTransport"] = "slack",
            ["DeliveryAddress"] = "#nope"
        }, BuildManualToolContext(), TestContext.Current.CancellationToken);

        Assert.StartsWith("Error: Could not resolve delivery_address '#nope'", result);
    }

    [Fact]
    public async Task Channel_delivery_resolves_address_to_canonical_id()
    {
        var capture = new CaptureSink();
        var probe = _system.ActorOf(Props.Create(() => new CapturingReminderActor(capture, success: true)));
        var resolver = new StubReminderTargetResolver(
            input => input == "#ops"
                ? new ReminderTargetResolution(true, "C0999OPS", ReminderTargetKind.Channel, null)
                : new ReminderTargetResolution(false, null, ReminderTargetKind.Unknown, $"unexpected target {input}"));

        var tool = CreateTool(probe, resolver);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "channel-delivery-resolve",
            ["Name"] = "channel-delivery-resolve",
            ["Prompt"] = "check status",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["DeliveryKind"] = "channel",
            ["DeliveryTransport"] = "slack",
            ["DeliveryAddress"] = "#ops"
        }, BuildManualToolContext(), TestContext.Current.CancellationToken);

        Assert.StartsWith("Reminder 'channel-delivery-resolve' scheduled.", result);
        Assert.NotNull(capture.LastSavedDefinition);
        Assert.Equal(DeliveryKind.Channel, capture.LastSavedDefinition!.Delivery.Kind);
        Assert.Equal("slack", capture.LastSavedDefinition.Delivery.Transport);
        Assert.Equal("C0999OPS", capture.LastSavedDefinition.Delivery.Address);
    }

    [Fact]
    public async Task Channel_delivery_to_user_resolves_address()
    {
        var capture = new CaptureSink();
        var probe = _system.ActorOf(Props.Create(() => new CapturingReminderActor(capture, success: true)));
        var resolver = new StubReminderTargetResolver(
            input => input == "@aaron"
                ? new ReminderTargetResolution(true, "U0456XYZ", ReminderTargetKind.User, null)
                : new ReminderTargetResolution(false, null, ReminderTargetKind.Unknown, $"unexpected target {input}"));

        var tool = CreateTool(probe, resolver);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "channel-delivery-user",
            ["Name"] = "channel-delivery-user",
            ["Prompt"] = "check status",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["DeliveryKind"] = "channel",
            ["DeliveryTransport"] = "slack",
            ["DeliveryAddress"] = "@aaron"
        }, BuildManualToolContext(), TestContext.Current.CancellationToken);

        Assert.StartsWith("Reminder 'channel-delivery-user' scheduled.", result);
        Assert.NotNull(capture.LastSavedDefinition);
        Assert.Equal(DeliveryKind.Channel, capture.LastSavedDefinition!.Delivery.Kind);
        Assert.Equal("U0456XYZ", capture.LastSavedDefinition.Delivery.Address);
    }

    public void Dispose()
    {
        _system.Terminate().GetAwaiter().GetResult();
    }

    private SetReminderTool CreateTool(IActorRef reminderManager, IReminderTargetResolver resolver)
        => new(reminderManager, _timeProvider, new SchedulingConfig(), [resolver]);

    private static ToolExecutionContext BuildManualToolContext()
        => TestToolExecutionContext.CreateUnboundWithoutApproval(TrustAudience.Personal, "manual");

    private sealed class StubReminderTargetResolver(Func<string, ReminderTargetResolution> resolve) : IReminderTargetResolver
    {
        public string Transport { get; init; } = "slack";
        public Task<ReminderTargetResolution> ResolveAsync(string target, CancellationToken ct = default)
            => Task.FromResult(resolve(target));
    }

    private sealed class CaptureSink
    {
        public ReminderDefinition? LastSavedDefinition { get; set; }
    }

    private sealed class CapturingReminderActor : ReceiveActor
    {
        private readonly CaptureSink _capture;
        private readonly bool _success;

        public CapturingReminderActor(CaptureSink capture, bool success)
        {
            _capture = capture;
            _success = success;

            Receive<SaveReminderCommand>(cmd =>
            {
                _capture.LastSavedDefinition = cmd.Definition;
                Sender.Tell(new ReminderSavedResponse(
                    cmd.Definition.Id,
                    cmd.Definition.Title,
                    _success,
                    _success ? DateTimeOffset.UtcNow.AddMinutes(30) : null,
                    _success ? ReminderSaveError.None : ReminderSaveError.Internal,
                    _success ? null : "forced failure"));
            });
        }

    }
}
