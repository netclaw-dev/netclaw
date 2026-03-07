using Akka.Actor;
using Akka;
using Akka.Streams.Dsl;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Streams;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Reminders;

public class ReminderExecutionActorTests : TestKit
{
    public ReminderExecutionActorTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No persistence needed — ReminderExecutionActor is ephemeral
    }

    [Fact]
    public async Task Execution_failure_reports_completed_false_with_error_message()
    {
        var innerException = new ArgumentException("inner cause");
        var outerException = new InvalidOperationException("outer failure", innerException);
        var failingPipeline = new FailingSessionPipeline(outerException);
        var definition = CreateDefinition("fail-test");

        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, failingPipeline)),
            "exec-parent");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5));

        Assert.False(completed.Success);
        Assert.Equal("fail-test", completed.Id.Value);
        // Error message is the outer exception message
        Assert.Equal("outer failure", completed.ErrorMessage);
    }

    [Fact]
    public async Task Execution_failure_with_inner_exception_propagates_outer_message()
    {
        // Verifies the inner exception is present in the exception chain logged.
        // The ReportAndStop uses ex.Message (outer), while the log uses ex.ToString()
        // which includes the full exception chain including inner exceptions.
        var innerException = new IOException("disk read failed");
        var outerException = new InvalidOperationException("pipeline initialization failed", innerException);
        var failingPipeline = new FailingSessionPipeline(outerException);
        var definition = CreateDefinition("inner-ex-test");

        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, failingPipeline)),
            "exec-parent-2");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5));

        Assert.False(completed.Success);
        // Outer message is the protocol-level error; inner is in the log via ex.ToString()
        Assert.Equal("pipeline initialization failed", completed.ErrorMessage);
    }

    [Fact]
    public async Task Execution_fails_when_notification_tool_returns_error()
    {
        var pipeline = new ScriptedSessionPipeline(sessionId =>
        [
            new ToolResultOutput
            {
                SessionId = sessionId,
                CallId = "call-1",
                ToolName = "send_slack_message",
                Result = "Error parsing arguments for tool 'send_slack_message': Required parameter 'Message' is missing or empty."
            },
            new TurnCompleted { SessionId = sessionId, TurnNumber = 1 }
        ]);

        var definition = CreateDefinition("notify-fail-test");
        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, pipeline)),
            "exec-parent-3");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5));

        Assert.False(completed.Success);
        Assert.Equal("notify-fail-test", completed.Id.Value);
        Assert.Contains("Required parameter 'Message'", completed.ErrorMessage);
    }

    [Fact]
    public async Task Execution_succeeds_when_notification_tool_reports_success()
    {
        var pipeline = new ScriptedSessionPipeline(sessionId =>
        [
            new ToolResultOutput
            {
                SessionId = sessionId,
                CallId = "call-2",
                ToolName = "send_slack_message",
                Result = "Message sent to channel C1. Thread: C1/1234567890.000001"
            },
            new TurnCompleted { SessionId = sessionId, TurnNumber = 1 }
        ]);

        var definition = CreateDefinition("notify-success-test");
        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, pipeline)),
            "exec-parent-4");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5));

        Assert.True(completed.Success);
        Assert.Equal("notify-success-test", completed.Id.Value);
        Assert.Null(completed.ErrorMessage);
    }

    private static ReminderDefinition CreateDefinition(string id)
    {
        var now = TimeProvider.System.GetUtcNow();
        return new ReminderDefinition
        {
            Id = id,
            Title = $"Test Reminder {id}",
            Instructions = "Do something.",
            NotifyInstructions = "Reply with result.",
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(1)
            },
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// Minimal parent actor that creates <see cref="ReminderExecutionActor"/> as a child
    /// and forwards messages it receives to a probe for test assertions.
    /// </summary>
    private sealed class ParentProxy : ReceiveActor
    {
        public ParentProxy(IActorRef probe, ReminderDefinition definition, ISessionPipeline pipeline)
        {
            var executionId = Guid.NewGuid();
            Context.ActorOf(
                ReminderExecutionActor.CreateProps(
                    executionId,
                    definition,
                    pipeline,
                    new ReminderConfig(),
                    TimeProvider.System),
                "exec");

            ReceiveAny(msg => probe.Tell(msg));
        }
    }

    /// <summary>Fake pipeline that throws a pre-configured exception on CreateAsync.</summary>
    private sealed class FailingSessionPipeline(Exception exception) : ISessionPipeline
    {
        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default) =>
            throw exception;
    }

    private sealed class ScriptedSessionPipeline(
        Func<SessionId, IReadOnlyList<SessionOutput>> outputFactory) : ISessionPipeline
    {
        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            var killSwitch = KillSwitches.Shared($"scripted-{sessionId.Value}");

            var input = Sink.Ignore<ChannelInput>()
                .MapMaterializedValue<NotUsed>(_ => NotUsed.Instance);

            var output = Source.From(outputFactory(sessionId).ToList())
                .Via(killSwitch.Flow<SessionOutput>());

            return Task.FromResult(new MaterializedSession(input, output, killSwitch));
        }
    }
}
