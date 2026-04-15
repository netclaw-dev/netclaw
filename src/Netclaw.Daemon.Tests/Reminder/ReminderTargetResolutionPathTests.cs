using Akka.Actor;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

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
    public async Task ReportTarget_alias_flows_through_tool_path_and_returns_resolution_error()
    {
        var capture = new CaptureSink();
        var probe = _system.ActorOf(Props.Create(() => new CapturingReminderActor(capture, success: true)));
        var resolver = new StubReminderTargetResolver(
            _ => new ReminderTargetResolution(false, null, ReminderTargetKind.Unknown, "Could not resolve Slack target '#nope'."));

        var tool = CreateTool(probe, resolver);

        string? reportToChannel = null;
        const string reportTarget = "#nope";
        if (!string.IsNullOrWhiteSpace(reportTarget))
            reportToChannel = reportTarget;

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "report-target-error",
            ["Name"] = "report-target-error",
            ["Prompt"] = "check status",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["ReportToChannel"] = reportToChannel
        }, BuildManualToolContext(), TestContext.Current.CancellationToken);

        Assert.StartsWith("Error: Could not resolve reportToChannel '#nope'", result);
    }

    [Fact]
    public async Task ReportTarget_alias_overrides_report_to_channel()
    {
        var capture = new CaptureSink();
        var probe = _system.ActorOf(Props.Create(() => new CapturingReminderActor(capture, success: true)));
        var resolver = new StubReminderTargetResolver(
            input => input == "#ops"
                ? new ReminderTargetResolution(true, "C0999OPS", ReminderTargetKind.Channel, null)
                : new ReminderTargetResolution(false, null, ReminderTargetKind.Unknown, $"unexpected target {input}"));

        var tool = CreateTool(probe, resolver);

        string? reportToChannel = "C0123ABC";
        const string reportTarget = "#ops";
        if (!string.IsNullOrWhiteSpace(reportTarget))
            reportToChannel = reportTarget;

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "report-target-overrides",
            ["Name"] = "report-target-overrides",
            ["Prompt"] = "check status",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["ReportToChannel"] = reportToChannel
        }, BuildManualToolContext(), TestContext.Current.CancellationToken);

        Assert.StartsWith("Reminder 'report-target-overrides' scheduled.", result);
        Assert.NotNull(capture.LastSavedDefinition);
        Assert.Equal("C0999OPS", capture.LastSavedDefinition!.ReportToChannel);
    }

    [Fact]
    public async Task ReportTarget_user_alias_generates_dm_notify_instructions()
    {
        var capture = new CaptureSink();
        var probe = _system.ActorOf(Props.Create(() => new CapturingReminderActor(capture, success: true)));
        var resolver = new StubReminderTargetResolver(
            input => input == "@aaron"
                ? new ReminderTargetResolution(true, "U0456XYZ", ReminderTargetKind.User, null)
                : new ReminderTargetResolution(false, null, ReminderTargetKind.Unknown, $"unexpected target {input}"));

        var tool = CreateTool(probe, resolver);

        string? reportToChannel = null;
        const string reportTarget = "@aaron";
        if (!string.IsNullOrWhiteSpace(reportTarget))
            reportToChannel = reportTarget;

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "report-target-user",
            ["Name"] = "report-target-user",
            ["Prompt"] = "check status",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["ReportToChannel"] = reportToChannel
        }, BuildManualToolContext(), TestContext.Current.CancellationToken);

        Assert.StartsWith("Reminder 'report-target-user' scheduled.", result);
        Assert.NotNull(capture.LastSavedDefinition);
        Assert.Equal("U0456XYZ", capture.LastSavedDefinition!.ReportToChannel);
        Assert.Equal(
            "Send a direct message to user U0456XYZ with your findings, or lack thereof.",
            capture.LastSavedDefinition.NotifyInstructions);
    }

    public void Dispose()
    {
        _system.Terminate().GetAwaiter().GetResult();
    }

    private SetReminderTool CreateTool(IActorRef reminderManager, IReminderTargetResolver resolver)
        => new(reminderManager, _timeProvider, resolver);

    private static ToolExecutionContext BuildManualToolContext() => new(sessionId: null, sessionDirectory: null)
    {
        ChannelType = "manual"
    };

    private sealed class StubReminderTargetResolver(Func<string, ReminderTargetResolution> resolve) : IReminderTargetResolver
    {
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
                    new ReminderId(cmd.Definition.Id),
                    cmd.Definition.Title,
                    _success,
                    _success ? DateTimeOffset.UtcNow.AddMinutes(30) : null,
                    _success ? ReminderSaveError.None : ReminderSaveError.Internal,
                    _success ? null : "forced failure"));
            });
        }

    }
}
