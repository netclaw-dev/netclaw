// -----------------------------------------------------------------------
// <copyright file="RoutedSkillReminderDeliveryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Persistence;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
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
        var (subscriber, completed, childGate) = await RunRoutedTurnWithHeldChildAsync(sessionId, ReminderSource(FireKey));
        Assert.Equal(new Netclaw.Actors.Reminders.ReminderId(FireKey), completed.SourceReminderId);

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        Assert.Equal(ReminderSlug, alert.Source);
        childGate.TrySetResult();
    }

    [Fact]
    public async Task A_retryable_failure_on_a_routed_skill_reminder_turn_alerts_and_does_not_retry_on_the_main_model()
    {
        // Arrange
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-retryable");
        var (subscriber, completed, childGate) = await RunRoutedTurnWithHeldChildAsync(sessionId, ReminderSource(FireKey));
        var callsBefore = ChatClient.CallCount;

        // Act: ContentRejected is retryable for an ordinary turn. A routed turn has no retry budget to spend.
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);

        // Assert: the session gave up at the first report, so the operator hears about it once.
        var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        Assert.Equal(ReminderSlug, alert.Source);
        AssertNextModelCallIsTheBarrier(callsBefore);
        childGate.TrySetResult();
    }

    [Fact]
    public async Task Two_retryable_reports_for_a_routed_skill_reminder_turn_raise_one_alert_and_no_retry()
    {
        // Arrange
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-retryable-twice");
        var (subscriber, completed, childGate) = await RunRoutedTurnWithHeldChildAsync(sessionId, ReminderSource(FireKey));
        var callsBefore = ChatClient.CallCount;

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);

        // Assert
        await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        AssertNextModelCallIsTheBarrier(callsBefore);
        childGate.TrySetResult();
    }

    [Fact]
    public async Task A_recovered_session_treats_a_routed_skill_reminder_turn_like_the_live_session()
    {
        // Arrange: a session that replays the journal reads the routed mark of the TurnRecorded event.
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-recovered");
        await SeedRoutedTurnJournalAsync(sessionId, new Netclaw.Actors.Reminders.ReminderId(FireKey));

        var recovered = CreateTestProbe("routed-recovered-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);

        // Act
        SendDeliveryFailed(sessionId, new TurnNumber(1), DeliveryFailureKind.TransportFailure);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
        Assert.Equal(ReminderSlug, alert.Source);
    }

    [Fact]
    public async Task A_recovered_session_alerts_and_does_not_retry_a_retryable_failure_on_a_routed_skill_reminder_turn()
    {
        // Arrange
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-recovered-retryable");
        await SeedRoutedTurnJournalAsync(sessionId, new Netclaw.Actors.Reminders.ReminderId(FireKey));

        var recovered = CreateTestProbe("routed-recovered-retryable-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);
        var callsBefore = ChatClient.CallCount;

        // Act
        SendDeliveryFailed(sessionId, new TurnNumber(1), DeliveryFailureKind.ContentRejected);

        // Assert: the replayed session behaves like the live session in the test above.
        var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
        Assert.Equal(ReminderSlug, alert.Source);
        AssertNextModelCallIsTheBarrier(callsBefore);
    }

    [Fact]
    public async Task A_session_that_passivated_alerts_and_does_not_retry_a_retryable_failure_on_a_routed_skill_reminder_turn()
    {
        // Arrange: the restored session does not replay TurnRecorded, so the snapshot carries the routed mark.
        // A live routed turn leaves an open child run, and the session does not passivate while a child run is
        // open. The test therefore stores the snapshot that a passivation after the routed turn would save.
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-passivated-retryable");
        await SeedSnapshotAsync(sessionId, SnapshotOfOneTurn() with
        {
            EligibleDeliveryReminderId = new Netclaw.Actors.Reminders.ReminderId(FireKey),
            EligibleDeliveryTurnRoutedSkill = true
        });

        var recovered = CreateTestProbe("routed-passivated-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);
        var callsBefore = ChatClient.CallCount;

        // Act
        SendDeliveryFailed(sessionId, new TurnNumber(1), DeliveryFailureKind.ContentRejected);

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
        var (subscriber, completed, childGate) = await RunRoutedTurnWithHeldChildAsync(sessionId, personSource);
        Assert.Null(completed.SourceReminderId);
        var callsBefore = ChatClient.CallCount;

        // Act: the live session reads the routed mark from its own state.
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);
        await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);

        // Assert
        Assert.Equal(0, AlertCount);
        AssertNextModelCallIsTheBarrier(callsBefore);
        childGate.TrySetResult();
    }

    [Fact]
    public async Task A_routed_skill_turn_that_a_person_started_is_not_retried_and_raises_no_alert_after_recovery()
    {
        // Arrange: no reminder started this turn. A person in the thread sees the missing reply.
        var sessionId = new SessionId("mattermost/chan1/routed-person-recovered");
        await SeedRoutedTurnJournalAsync(sessionId, reminderId: null);

        var recovered = CreateTestProbe("routed-person-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);
        var callsBefore = ChatClient.CallCount;

        // Act
        SendDeliveryFailed(sessionId, new TurnNumber(1), DeliveryFailureKind.ContentRejected);
        await AwaitSessionHandledEarlierMessagesAsync(sessionId, recovered);

        // Assert
        Assert.Equal(0, AlertCount);
        AssertNextModelCallIsTheBarrier(callsBefore);
    }

    // --- The child result of a routed skill (spec netclaw-session, "A child-delivery turn keeps the reminder link") ---
    // The routed turn posts only the acceptance. A later child-delivery turn posts the real reply. That turn has no
    // input of its own, so it must keep the reminder link of the routed turn, and it follows the routed rule.

    [Fact]
    public async Task A_failed_post_of_the_child_result_of_a_routed_skill_reminder_alerts_once_and_does_not_retry()
    {
        // Arrange
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-child-result");
        var (subscriber, delivery) = await RunRoutedChildDeliveryTurnAsync(sessionId, ReminderSource(FireKey));
        var callsBefore = ChatClient.CallCount;

        // Act: ContentRejected is retryable for an ordinary turn. The child result of a routed skill is not.
        SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.ContentRejected);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        Assert.Equal(ReminderSlug, alert.Source);
        AssertNextModelCallIsTheBarrier(callsBefore);
    }

    [Fact]
    public async Task The_child_result_of_a_routed_skill_that_a_person_started_raises_no_alert_and_no_retry()
    {
        // Arrange: no reminder started the routed turn, so the child result has no reminder link.
        var sessionId = new SessionId("mattermost/chan1/routed-person-child-result");
        var personSource = ReminderSource(FireKey) with { ReminderId = null };
        var (subscriber, delivery) = await RunRoutedChildDeliveryTurnAsync(sessionId, personSource);
        var callsBefore = ChatClient.CallCount;

        // Act
        SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.ContentRejected);
        await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);

        // Assert
        Assert.Equal(0, AlertCount);
        AssertNextModelCallIsTheBarrier(callsBefore);
    }

    [Fact]
    public async Task A_recovered_session_alerts_once_and_does_not_retry_the_child_result_of_a_routed_skill_reminder()
    {
        // Arrange: the journal holds the child-delivery turn. Replay must read its reminder link and routed mark.
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-child-result-recovered");
        var (_, delivery) = await RunRoutedChildDeliveryTurnAsync(sessionId, ReminderSource(FireKey));
        await StopSessionAsync(sessionId);

        var recovered = CreateTestProbe("routed-child-result-recovered-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);
        var callsBefore = ChatClient.CallCount;

        // Act
        SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.ContentRejected);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
        Assert.Equal(ReminderSlug, alert.Source);
        AssertNextModelCallIsTheBarrier(callsBefore);
    }

    [Fact]
    public async Task A_session_that_passivated_alerts_once_and_does_not_retry_the_child_result_of_a_routed_skill_reminder()
    {
        // Arrange: the child run is closed after its delivery, so the session can passivate. The next incarnation
        // restores from the passivation snapshot and does not replay the child-delivery turn.
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-child-result-passivated");
        var (subscriber, delivery) = await RunRoutedChildDeliveryTurnAsync(sessionId, ReminderSource(FireKey));
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        session.Tell(new LeaveSession(subscriber) { SessionId = sessionId });
        session.Tell(ReceiveTimeout.Instance);
        await ExpectTerminatedAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateTestProbe("routed-child-result-passivated-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);
        var callsBefore = ChatClient.CallCount;

        // Act
        SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.ContentRejected);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
        Assert.Equal(ReminderSlug, alert.Source);
        AssertNextModelCallIsTheBarrier(callsBefore);
    }

    // Runs the routed turn, releases the child, and waits for the child-delivery turn, which posts the child result.
    private async Task<(Akka.TestKit.TestProbe Subscriber, TurnCompleted Delivery)> RunRoutedChildDeliveryTurnAsync(
        SessionId sessionId, MessageSource source)
    {
        var (subscriber, routed, childGate) = await RunRoutedTurnWithHeldChildAsync(sessionId, source);
        childGate.TrySetResult();
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        var delivery = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(routed.TurnNumber.Value + 1, delivery.TurnNumber.Value);
        Assert.Empty(delivery.OpenChildRunIds);
        return (subscriber, delivery);
    }

    // The coordination stack runs a routed skill as a child run. The routed turn records the acceptance, and a later
    // child-delivery turn posts the child result. The test holds the child's model call, so the routed turn stays the
    // latest recorded turn while the test sends its report. The method returns after the child's call started, so the
    // next model call belongs to the session.
    private async Task<(Akka.TestKit.TestProbe Subscriber, TurnCompleted Completed, TaskCompletionSource ChildGate)>
        RunRoutedTurnWithHeldChildAsync(SessionId sessionId, MessageSource source)
    {
        var childGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callsBefore = ChatClient.CallCount;
        ChatClient.NextResponseGate = childGate;
        var (subscriber, completed) = await RunTurnAsync(sessionId, source, RoutedPrompt);
        await AwaitAssertAsync(() =>
        {
            Assert.True(ChatClient.CallCount > callsBefore, "The child model call did not start.");
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        return (subscriber, completed, childGate);
    }

    // A live routed turn leaves an open child run. A recovered session records that run as lost and starts a
    // child-delivery turn at once, so the routed turn stops being the latest recorded turn. The recovery tests
    // therefore write the routed turn straight to the journal, as a session without an open child run leaves it.
    private async Task SeedRoutedTurnJournalAsync(SessionId sessionId, Netclaw.Actors.Reminders.ReminderId? reminderId)
    {
        var turn = new TurnRecorded
        {
            SessionId = sessionId,
            UserMessage = new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.User, Content = RoutedPrompt },
            AssistantReply = new SerializableChatMessage
            {
                Role = Netclaw.Actors.Protocol.ChatRole.Assistant,
                Content = "The routed skill accepted the task."
            },
            RecordedAtMs = 1_712_000_000_000,
            RoutedSkill = true,
            SourceReminderId = reminderId
        };
        var probe = CreateTestProbe("journal-seed-" + Guid.NewGuid().ToString("N"));
        var write = new AtomicWrite(new Persistent(turn, 1, $"session-{sessionId.Value}", writerGuid: Guid.NewGuid().ToString("N")));
        Akka.Persistence.Persistence.Instance.Apply(Sys).JournalFor(null).Tell(
            new WriteMessages([write], probe.Ref, actorInstanceId: 1), probe.Ref);
        await probe.ExpectMsgAsync<WriteMessagesSuccessful>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
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
