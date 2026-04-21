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

namespace Netclaw.Actors.Tests.Reminders;

public class ReminderExecutionActorTests : TestKit, IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-exec-actor-tests-{Guid.NewGuid():N}");
    private readonly ReminderHistoryStore _historyStore;

    public ReminderExecutionActorTests(ITestOutputHelper output) : base(output: output)
    {
        Directory.CreateDirectory(_tempDir);
        var paths = new NetclawPaths(_tempDir);
        Directory.CreateDirectory(paths.RemindersDirectory);
        _historyStore = new ReminderHistoryStore(paths);
    }

    void IDisposable.Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

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
            Props.Create(() => new ParentProxy(probe.Ref, definition, failingPipeline, _historyStore)),
            "exec-parent");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

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
            Props.Create(() => new ParentProxy(probe.Ref, definition, failingPipeline, _historyStore)),
            "exec-parent-2");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

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
            Props.Create(() => new ParentProxy(probe.Ref, definition, pipeline, _historyStore)),
            "exec-parent-3");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

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
            Props.Create(() => new ParentProxy(probe.Ref, definition, pipeline, _historyStore)),
            "exec-parent-4");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(completed.Success);
        Assert.Equal("notify-success-test", completed.Id.Value);
        Assert.Null(completed.ErrorMessage);
    }

    [Fact]
    public async Task Execution_succeeds_when_conditional_policy_and_no_notification_sent()
    {
        var pipeline = new ScriptedSessionPipeline(sessionId =>
        [
            new TextOutput { SessionId = sessionId, Text = "No new opportunities found." },
            new TurnCompleted { SessionId = sessionId, TurnNumber = 1 }
        ]);

        var definition = CreateDefinition("conditional-no-notify") with
        {
            DeliveryRequired = false
        };
        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, pipeline, _historyStore)),
            "exec-conditional-no-notify");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(completed.Success);
        Assert.Null(completed.ErrorMessage);
    }

    [Fact]
    public async Task Execution_fails_when_required_policy_and_no_notification_sent()
    {
        var pipeline = new ScriptedSessionPipeline(sessionId =>
        [
            new TextOutput { SessionId = sessionId, Text = "Some output." },
            new TurnCompleted { SessionId = sessionId, TurnNumber = 1 }
        ]);

        // Default policy is Required — should fail when no notification tool is invoked
        var definition = CreateDefinition("required-no-notify");
        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, pipeline, _historyStore)),
            "exec-required-no-notify");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(completed.Success);
        Assert.Contains("no notification tool was invoked", completed.ErrorMessage);
    }

    [Fact]
    public async Task Execution_fails_when_conditional_policy_and_notification_tool_errors()
    {
        var pipeline = new ScriptedSessionPipeline(sessionId =>
        [
            new ToolResultOutput
            {
                SessionId = sessionId,
                CallId = "call-err",
                ToolName = "send_slack_message",
                Result = "Error: channel not found"
            },
            new TurnCompleted { SessionId = sessionId, TurnNumber = 1 }
        ]);

        var definition = CreateDefinition("conditional-notify-error") with
        {
            DeliveryRequired = false
        };
        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, pipeline, _historyStore)),
            "exec-conditional-notify-error");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Even with conditional policy, a failed notification attempt is still a failure
        Assert.False(completed.Success);
        Assert.Contains("channel not found", completed.ErrorMessage);
    }

    [Fact]
    public async Task Execution_pipeline_requests_streaming_and_tool_call_output()
    {
        var pipeline = new ScriptedSessionPipeline(sessionId =>
        [
            new TurnCompleted { SessionId = sessionId, TurnNumber = 1 }
        ]);

        var definition = CreateDefinition("filter-check") with { DeliveryInstructions = string.Empty };
        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, pipeline, _historyStore)),
            "exec-filter-check");

        await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(pipeline.CapturedOptions);
        Assert.True(pipeline.CapturedOptions!.Filter.HasFlag(OutputFilter.TextStreaming));
        Assert.True(pipeline.CapturedOptions!.Filter.HasFlag(OutputFilter.ToolCalls));
    }

    private static ReminderDefinition CreateDefinition(string id)
    {
        var now = TimeProvider.System.GetUtcNow();
        return new ReminderDefinition
        {
            Id = id,
            Title = $"Test Reminder {id}",
            Instructions = "Do something.",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.Channel, Transport = "slack", Address = "#general" },
            DeliveryInstructions = "Reply with result.",
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(1)
            },
            Audience = TrustAudience.Team,
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
        public ParentProxy(
            IActorRef probe,
            ReminderDefinition definition,
            ISessionPipeline pipeline,
            ReminderHistoryStore historyStore)
        {
            var executionId = Guid.NewGuid();
            Context.ActorOf(
                ReminderExecutionActor.CreateProps(
                    executionId,
                    definition,
                    pipeline,
                    TimeProvider.System,
                    historyStore),
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

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ScriptedSessionPipeline(
        Func<SessionId, IReadOnlyList<SessionOutput>> outputFactory) : ISessionPipeline
    {
        public SessionPipelineOptions? CapturedOptions { get; private set; }

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            CapturedOptions = options;

            var killSwitch = KillSwitches.Shared($"scripted-{sessionId.Value}");

            var input = Sink.Ignore<ChannelInput>()
                .MapMaterializedValue<NotUsed>(_ => NotUsed.Instance);

            var output = Source.From(outputFactory(sessionId).ToList())
                .Via(killSwitch.Flow<SessionOutput>());

            return Task.FromResult(new MaterializedSession(input, output, killSwitch));
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    // ── Audience resolution tests ─────────────────────────────────────────────

    [Fact]
    public async Task Execution_uses_definition_audience_when_set()
    {
        var pipeline = new ScriptedSessionPipeline(sessionId =>
        [
            new TurnCompleted { SessionId = sessionId, TurnNumber = 1 }
        ]);

        var definition = CreateDefinition("audience-override") with
        {
            Audience = TrustAudience.Personal,
            DeliveryInstructions = string.Empty
        };
        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, pipeline, _historyStore)),
            "exec-audience-override");

        await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(pipeline.CapturedOptions);
        Assert.Equal(TrustAudience.Personal, pipeline.CapturedOptions!.DefaultAudience);
    }

    [Fact]
    public async Task Execution_fails_when_definition_audience_missing()
    {
        var pipeline = new ScriptedSessionPipeline(sessionId =>
        [
            new TurnCompleted { SessionId = sessionId, TurnNumber = 1 }
        ]);

        var definition = CreateDefinition("audience-fallback") with
        {
            DeliveryInstructions = string.Empty,
            Audience = null
        };
        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, pipeline, _historyStore)),
            "exec-audience-fallback");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(completed.Success);
        Assert.Contains("missing a persisted execution audience", completed.ErrorMessage);
        Assert.Null(pipeline.CapturedOptions);
    }

    [Fact]
    public async Task Execution_uses_stored_audience_directly()
    {
        var pipeline = new ScriptedSessionPipeline(sessionId =>
        [
            new TurnCompleted { SessionId = sessionId, TurnNumber = 1 }
        ]);

        var definition = CreateDefinition("audience-team-default") with
        {
            DeliveryInstructions = string.Empty,
            Audience = TrustAudience.Team
        };
        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, pipeline, _historyStore)),
            "exec-audience-team-default");

        await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(pipeline.CapturedOptions);
        Assert.Equal(TrustAudience.Team, pipeline.CapturedOptions!.DefaultAudience);
    }

    // ── History integration tests ─────────────────────────────────────────────

    [Fact]
    public async Task Successful_execution_appends_success_true_history_record()
    {
        var pipeline = new ScriptedSessionPipeline(sessionId =>
        [
            new TurnCompleted { SessionId = sessionId, TurnNumber = 1 }
        ]);

        // Use Kind = None so success is not gated on send_slack_message
        var definition = CreateDefinition("history-success-test") with
        {
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None }
        };
        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, pipeline, _historyStore)),
            "exec-history-success");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(completed.Success);

        // PostStop writes the history record asynchronously after the actor stops.
        // Poll until the record appears rather than using a fixed delay.
        var rid = new ReminderId("history-success-test");
        await AwaitConditionAsync(
            async () => (await _historyStore.ReadAsync(rid, 10)).Count > 0,
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var records = await _historyStore.ReadAsync(rid, 10);
        Assert.Single(records);
        Assert.True(records[0].Success);
        Assert.Null(records[0].ErrorMessage);
        Assert.Contains("history-success-test", records[0].SessionId);
    }

    [Fact]
    public async Task Failed_execution_appends_success_false_with_error_message()
    {
        var exception = new InvalidOperationException("pipeline blew up");
        var failingPipeline = new FailingSessionPipeline(exception);
        var definition = CreateDefinition("history-failure-test");

        var probe = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new ParentProxy(probe.Ref, definition, failingPipeline, _historyStore)),
            "exec-history-failure");

        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(completed.Success);

        var rid = new ReminderId("history-failure-test");
        await AwaitConditionAsync(
            async () => (await _historyStore.ReadAsync(rid, 10)).Count > 0,
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var records = await _historyStore.ReadAsync(rid, 10);
        Assert.Single(records);
        Assert.False(records[0].Success);
        Assert.Equal("pipeline blew up", records[0].ErrorMessage);
    }
}
