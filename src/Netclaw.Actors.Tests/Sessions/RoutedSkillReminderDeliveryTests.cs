// -----------------------------------------------------------------------
// <copyright file="RoutedSkillReminderDeliveryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Skills;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// S2: a reminder whose prompt is a routed slash command (<c>/ops-route ...</c>) runs the skill in a sub-agent and
/// records the turn in <c>HandleRoutedSkillExecutionCompleted</c> with <c>TurnRecorded.RoutedSkill = true</c>.
/// The spec <c>netclaw-session</c> says the session reports a failed post on that turn once, and it never
/// re-runs the turn on the main model. A live session, a session that replays the journal, and a session that
/// restores from a snapshot must all do the same.
/// </summary>
public sealed class RoutedSkillReminderDeliveryTests : ReminderDeliveryTestBase
{
    private const string RoutedPrompt = "/ops-route check daemon health";

    public RoutedSkillReminderDeliveryTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);

        var skillDir = Path.Combine(TestPaths.BasePath, "routed-reminder-skill");
        Directory.CreateDirectory(skillDir);
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillFile, """
            ---
            name: ops-route
            description: Route to operations helper.
            metadata:
              subagent: summarizer
            ---

            # Ops Route

            You specialize in daemon health checks.
            """);
        var skillRegistry = new SkillRegistry();
        skillRegistry.Register(new SkillEntry("ops-route", "Ops Route", "Route to operations helper.", skillFile, skillDir, null)
        {
            HasSubagentRoutingMetadata = true,
            Subagent = "summarizer"
        });
        services.AddSingleton(skillRegistry);

        var promptProvider = new StaticSystemPromptProvider("You are a test assistant.");
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        var toolAccessPolicy = new ToolAccessPolicy(new NetclawPaths(),
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));
        var subAgentRegistry = new SubAgentDefinitionRegistry();
        subAgentRegistry.Register(new SubAgentProfile
        {
            Name = "summarizer",
            Description = "Summarize content",
            SystemPrompt = "You are a summarizer.",
            ToolNames = ["file_read"],
            ModelRole = ModelRole.Compaction,
            Visibility = SubAgentVisibility.UserFacing,
            EmitStructuredFindings = false
        });

        var toolRegistry = new ToolRegistry();
        toolRegistry.RegisterCore(new StubFileReadTool());
        var spawner = new SubAgentSpawner(
            new SingleClientProvider(ChatClient),
            toolRegistry,
            toolAccessPolicy,
            approvalService: null,
            promptProvider,
            new WorkingContextSnapshotProvider(
                new GitWorkingContextInspector(TimeProvider.System),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkingContextSnapshotProvider>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SubAgentSpawner>.Instance, new SessionConfig());

        services.AddSingleton(toolRegistry);
        services.AddSingleton(subAgentRegistry);
        services.AddSingleton(spawner);
        services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(
            toolRegistry,
            toolAccessPolicy,
            approvalService: null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DispatchingToolExecutor>.Instance));
    }

    [Fact]
    public async Task A_failed_post_on_a_routed_skill_reminder_turn_raises_one_alert()
    {
        // Arrange: the reminder prompt runs a routed skill. The turn is recorded by the routed-skill path.
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-live");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey), RoutedPrompt);
        Assert.Equal(new Netclaw.Actors.Reminders.ReminderId(FireKey), completed.SourceReminderId);

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        Assert.Equal(ReminderSlug, alert.Source);
    }

    [Fact]
    public async Task A_retryable_failure_on_a_routed_skill_reminder_turn_alerts_and_does_not_retry_on_the_main_model()
    {
        // Arrange
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-retryable");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey), RoutedPrompt);
        var callsBefore = ChatClient.CallCount;

        // Act: ContentRejected is retryable for an ordinary turn. A routed turn has no retry budget to spend.
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);

        // Assert: the session gave up at the first report, so the operator hears about it once.
        var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        Assert.Equal(ReminderSlug, alert.Source);
        AssertNextModelCallIsTheBarrier(callsBefore);
    }

    [Fact]
    public async Task Two_retryable_reports_for_a_routed_skill_reminder_turn_raise_one_alert_and_no_retry()
    {
        // Arrange
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-retryable-twice");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey), RoutedPrompt);
        var callsBefore = ChatClient.CallCount;

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);

        // Assert
        await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        AssertNextModelCallIsTheBarrier(callsBefore);
    }

    [Fact]
    public async Task A_recovered_session_treats_a_routed_skill_reminder_turn_like_the_live_session()
    {
        // Arrange: a session that replays the journal reads the routed mark of the TurnRecorded event.
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-recovered");
        var (_, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey), RoutedPrompt);
        await StopSessionAsync(sessionId);

        var recovered = CreateTestProbe("routed-recovered-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
        Assert.Equal(ReminderSlug, alert.Source);
    }

    [Fact]
    public async Task A_recovered_session_alerts_and_does_not_retry_a_retryable_failure_on_a_routed_skill_reminder_turn()
    {
        // Arrange
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-recovered-retryable");
        var (_, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey), RoutedPrompt);
        await StopSessionAsync(sessionId);

        var recovered = CreateTestProbe("routed-recovered-retryable-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);
        var callsBefore = ChatClient.CallCount;

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);

        // Assert: the replayed session behaves like the live session in the test above.
        var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
        Assert.Equal(ReminderSlug, alert.Source);
        AssertNextModelCallIsTheBarrier(callsBefore);
    }

    [Fact]
    public async Task A_session_that_passivated_alerts_and_does_not_retry_a_retryable_failure_on_a_routed_skill_reminder_turn()
    {
        // Arrange: the restored session does not replay TurnRecorded, so the snapshot carries the routed mark.
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-passivated-retryable");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey), RoutedPrompt);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        session.Tell(new LeaveSession(subscriber) { SessionId = sessionId });
        session.Tell(ReceiveTimeout.Instance);
        await ExpectTerminatedAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateTestProbe("routed-passivated-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);
        var callsBefore = ChatClient.CallCount;

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
        Assert.Equal(ReminderSlug, alert.Source);
        AssertNextModelCallIsTheBarrier(callsBefore);
    }

    [Fact]
    public async Task A_routed_skill_turn_that_a_person_started_is_not_retried_and_raises_no_alert_live()
    {
        // Arrange: no reminder started this turn. A person in the thread sees the missing reply.
        var sessionId = new SessionId("mattermost/chan1/routed-person-live");
        var personSource = ReminderSource(FireKey) with { ReminderId = null };
        var (subscriber, completed) = await RunTurnAsync(sessionId, personSource, RoutedPrompt);
        Assert.Null(completed.SourceReminderId);
        var callsBefore = ChatClient.CallCount;

        // Act: the live session reads the routed mark from its own state.
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);
        await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);

        // Assert
        Assert.Equal(0, AlertCount);
        AssertNextModelCallIsTheBarrier(callsBefore);
    }

    [Fact]
    public async Task A_routed_skill_turn_that_a_person_started_is_not_retried_and_raises_no_alert_after_recovery()
    {
        // Arrange: no reminder started this turn. A person in the thread sees the missing reply.
        var sessionId = new SessionId("mattermost/chan1/routed-person-recovered");
        var personSource = ReminderSource(FireKey) with { ReminderId = null };
        var (_, completed) = await RunTurnAsync(sessionId, personSource, RoutedPrompt);
        Assert.Null(completed.SourceReminderId);
        await StopSessionAsync(sessionId);

        var recovered = CreateTestProbe("routed-person-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);
        var callsBefore = ChatClient.CallCount;

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);
        await AwaitSessionHandledEarlierMessagesAsync(sessionId, recovered);

        // Assert
        Assert.Equal(0, AlertCount);
        AssertNextModelCallIsTheBarrier(callsBefore);
    }

    // The first model call after the report must belong to the barrier message. A retry would make its own
    // call first, with the original prompt and the delivery nudge, and the barrier call would follow it.
    private void AssertNextModelCallIsTheBarrier(int callsBefore)
    {
        var calls = ChatClient.ReceivedMessages;
        Assert.True(calls.Count > callsBefore, "The barrier message made no model call.");
        var lastUserMessage = calls[callsBefore]
            .Last(m => m.Role == Microsoft.Extensions.AI.ChatRole.User)
            .Text;
        Assert.Contains("Barrier message", lastUserMessage, StringComparison.Ordinal);
    }

    // The sub-agent profile needs one tool that the audience policy exposes. The model never calls it.
    private sealed class StubFileReadTool : INetclawTool
    {
        public string Name => "file_read";
        public LlmFacingToolName LlmFacingName { get; } = LlmFacingToolName.FromCanonical("file_read");
        public string Description => "Stub file read";
        public string GrantCategory => "file";
        public System.Text.Json.JsonElement ParameterSchema => default;

        public AITool ToAITool() => AIFunctionFactory.Create(() => "stub", name: Name, description: Description);

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => Task.FromResult("stub");

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
            => Task.FromResult("stub");
    }
}
