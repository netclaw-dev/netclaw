// -----------------------------------------------------------------------
// <copyright file="TeamsSessionBindingContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Teams;
using Netclaw.Channels.Teams.Serialization;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Contract fixture for the shared lifecycle behind Teams' opaque Adaptive Card
/// transport. It deliberately exercises the public SDK-free Teams contracts;
/// card payload structure and nonce edge cases remain Teams transport tests.
/// </summary>
public sealed class TeamsSessionBindingContractTests(ITestOutputHelper output) : TestKit(output: output)
{
    private int _actorCounter;

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawSerialization()
            .WithTeamsPersistenceSerialization();
    }

    [Fact]
    public async Task Safe_inbound_reaches_the_session_pipeline()
    {
        var sessionId = CreateSessionId("safe-inbound");
        var pipeline = new RecordingSessionPipeline(_ => [], reactive: true);
        var actor = CreateBindingActor(sessionId, pipeline, new RecordingTeamsReplyClient(), SafeDetector());

        await RouteAsync(actor, sessionId, "safe message", "user-a");
        await pipeline.Created.WaitAsync(TestContext.Current.CancellationToken);

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(pipeline.CapturedInputs, input =>
                input.Contents.OfType<TextContent>().Any(content => content.Text == "safe message"));
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Prompt_injection_block_and_detector_unavailable_fail_closed_before_pipeline_creation()
    {
        var blockedSession = CreateSessionId("blocked-inbound");
        var blockedPipeline = new RecordingSessionPipeline(_ => []);
        var blocked = CreateBindingActor(
            blockedSession,
            blockedPipeline,
            new RecordingTeamsReplyClient(),
            new ConfigurablePromptInjectionDetector(
                PromptInjectionResult.Detected(PromptInjectionRisk.High, "injection")));

        var blockedResult = await RouteResultAsync(blocked, blockedSession, "ignore policy", "user-a");
        Assert.Equal(TeamsBindingRouteDisposition.Denied, blockedResult.Disposition);
        Assert.Equal(0, blockedPipeline.CreateCount);

        var unavailableSession = CreateSessionId("unavailable-inbound");
        var unavailablePipeline = new RecordingSessionPipeline(_ => []);
        var unavailable = CreateBindingActor(
            unavailableSession,
            unavailablePipeline,
            new RecordingTeamsReplyClient(),
            new ConfigurablePromptInjectionDetector(new InvalidOperationException("detector unavailable")));

        var unavailableResult = await RouteResultAsync(unavailable, unavailableSession, "hello", "user-a");
        Assert.Equal(TeamsBindingRouteDisposition.Unavailable, unavailableResult.Disposition);
        Assert.Equal(0, unavailablePipeline.CreateCount);
    }

    [Fact]
    public async Task Approval_card_action_forwards_the_exact_session_option()
    {
        var sessionId = CreateSessionId("approval-forward");
        var pipeline = new RecordingSessionPipeline(session =>
        [
            ApprovalRequest(session, "call-forward", "user-a", PrincipalClassification.TrustedInternal)
        ], reactive: true);
        var client = new RecordingTeamsReplyClient();
        var actor = CreateBindingActor(sessionId, pipeline, client, SafeDetector());

        await RouteAsync(actor, sessionId, "run tool", "user-a");
        var delivered = await WaitForApprovalAsync(client);
        var result = await SubmitApprovalAsync(actor, sessionId, delivered, ApprovalOptionKeys.ApproveOnce, "user-a");

        Assert.Equal(TeamsApprovalActionDisposition.Accepted, result.Disposition);
        var response = Assert.IsType<ToolInteractionResponse>(Assert.Single(pipeline.RecordedFeedback));
        Assert.Equal("call-forward", response.CallId.Value);
        Assert.Equal(ApprovalOptionKeys.ApproveOnce, response.SelectedKey.Value);
        Assert.Equal("user-a", response.SenderId.Value);
    }

    [Fact]
    public async Task Approval_requester_and_automation_semantics_match_the_shared_response_flow()
    {
        var requesterSession = CreateSessionId("approval-requester");
        var requesterPipeline = new RecordingSessionPipeline(session =>
        [
            ApprovalRequest(session, "call-requester", "user-a", PrincipalClassification.TrustedInternal)
        ], reactive: true);
        var requesterClient = new RecordingTeamsReplyClient();
        var requesterActor = CreateBindingActor(requesterSession, requesterPipeline, requesterClient, SafeDetector());

        await RouteAsync(requesterActor, requesterSession, "run tool", "user-a");
        var requesterCard = await WaitForApprovalAsync(requesterClient);
        var rejected = await SubmitApprovalAsync(
            requesterActor,
            requesterSession,
            requesterCard,
            ApprovalOptionKeys.ApproveOnce,
            "user-b");
        Assert.Equal(TeamsApprovalActionDisposition.Rejected, rejected.Disposition);
        Assert.Empty(requesterPipeline.RecordedFeedback);

        var automationSession = CreateSessionId("approval-automation");
        var automationPipeline = new RecordingSessionPipeline(session =>
        [
            ApprovalRequest(session, "call-automation", "reminder-system", PrincipalClassification.VerifiedAutomation)
        ], reactive: true);
        var automationClient = new RecordingTeamsReplyClient();
        var automationActor = CreateBindingActor(automationSession, automationPipeline, automationClient, SafeDetector());

        await RouteAsync(automationActor, automationSession, "run tool", "user-a");
        var automationCard = await WaitForApprovalAsync(automationClient);
        var accepted = await SubmitApprovalAsync(
            automationActor,
            automationSession,
            automationCard,
            ApprovalOptionKeys.ApproveOnce,
            "user-b");
        Assert.Equal(TeamsApprovalActionDisposition.Accepted, accepted.Disposition);
        var response = Assert.IsType<ToolInteractionResponse>(Assert.Single(automationPipeline.RecordedFeedback));
        Assert.Equal("call-automation", response.CallId.Value);
        Assert.Equal("user-b", response.SenderId.Value);
    }

    [Fact]
    public async Task Recovered_card_action_forwards_once_and_duplicate_action_cannot_execute_twice()
    {
        var sessionId = CreateSessionId("approval-recovered");
        var pipeline = new RecordingSessionPipeline(session =>
        [
            ApprovalRequest(session, "call-recovered", "user-a", PrincipalClassification.TrustedInternal)
        ], reactive: true);
        var client = new RecordingTeamsReplyClient();
        var first = CreateBindingActor(sessionId, pipeline, client, SafeDetector());

        await RouteAsync(first, sessionId, "run tool", "user-a");
        var delivered = await WaitForApprovalAsync(client);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateBindingActor(sessionId, pipeline, client, SafeDetector());
        var accepted = await SubmitApprovalAsync(recovered, sessionId, delivered, ApprovalOptionKeys.ApproveOnce, "user-a");
        Assert.Equal(TeamsApprovalActionDisposition.Accepted, accepted.Disposition);

        var duplicate = await SubmitApprovalAsync(recovered, sessionId, delivered, ApprovalOptionKeys.ApproveOnce, "user-a");
        Assert.Equal(TeamsApprovalActionDisposition.AlreadyProcessed, duplicate.Disposition);
        Assert.Single(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>());
    }

    [Fact]
    public async Task Output_completion_reinitializes_the_pipeline()
    {
        var sessionId = CreateSessionId("pipeline-reinitialize");
        var pipeline = new RecordingSessionPipeline(_ => [], reactive: true);
        var actor = CreateBindingActor(sessionId, pipeline, new RecordingTeamsReplyClient(), SafeDetector());

        await RouteAsync(actor, sessionId, "start", "user-a");
        await pipeline.Created.WaitAsync(TestContext.Current.CancellationToken);
        pipeline.TerminateOutputStream();

        await AwaitAssertAsync(
            () => Assert.True(pipeline.CreateCount >= 2, $"CreateCount={pipeline.CreateCount}"),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Transport_failure_notifies_the_session_with_DeliveryFailed()
    {
        var sessionId = CreateSessionId("delivery-failure");
        var pipeline = new RecordingSessionPipeline(session =>
        [
            new TextOutput("reply") { SessionId = session }
        ], reactive: true);
        var client = new RecordingTeamsReplyClient(new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable));
        var actor = CreateBindingActor(sessionId, pipeline, client, SafeDetector());

        await RouteAsync(actor, sessionId, "start", "user-a");

        await AwaitAssertAsync(() =>
        {
            var deliveryFailure = Assert.IsType<DeliveryFailed>(Assert.Single(pipeline.RecordedFeedback));
            Assert.Equal(ChannelType.Teams, deliveryFailure.ChannelType);
            Assert.Equal(DeliveryFailureKind.TransportFailure, deliveryFailure.FailureKind);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Feedback_pipe_failure_is_visible_to_actor_supervision()
    {
        var sessionId = CreateSessionId("feedback-failure");
        var pipeline = new RecordingSessionPipeline(session =>
        [
            new TextOutput("reply") { SessionId = session }
        ], reactive: true)
        {
            FeedbackException = new InvalidOperationException("feedback pipe failed")
        };
        var client = new RecordingTeamsReplyClient(new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable));
        var observer = CreateTestProbe();
        var props = TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, client, SafeDetector()));
        Sys.ActorOf(Props.Create(() => new StopOnFailureParent(props, observer.Ref)), NextActorName());
        var binding = await observer.ExpectMsgAsync<IActorRef>(cancellationToken: TestContext.Current.CancellationToken);

        Watch(binding);
        await RouteAsync(binding, sessionId, "start", "user-a");
        ExpectTerminated(binding, cancellationToken: TestContext.Current.CancellationToken);
    }

    private IActorRef CreateBindingActor(
        SessionId sessionId,
        ISessionPipeline pipeline,
        ITeamsReplyClient replyClient,
        IPromptInjectionDetector detector) => Sys.ActorOf(
        TeamsSessionBindingActor.CreateProps(sessionId, CreateDependencies(pipeline, replyClient, detector)),
        NextActorName());

    private TeamsConversationDependencies CreateDependencies(
        ISessionPipeline pipeline,
        ITeamsReplyClient replyClient,
        IPromptInjectionDetector detector) => new(
        new TeamsChannelOptions
        {
            Enabled = true,
            TenantId = "tenant-a",
            AllowDirectMessages = true,
            AllowedUserIds = ["user-a", "user-b"]
        },
        pipeline,
        replyClient,
        new TeamsOutputRenderer(),
        TimeProvider.System)
        {
            PromptInjectionDetector = detector
        };

    private static ConfigurablePromptInjectionDetector SafeDetector() =>
        new(PromptInjectionResult.Safe());

    private static SessionId CreateSessionId(string suffix)
    {
        Assert.True(TeamsSessionIdentifierCodec.TryCreatePersonal(
            "tenant-a",
            $"contract-{suffix}",
            out var sessionId,
            out _));
        return sessionId;
    }

    private static TeamsInboundActivity CreateInbound(SessionId sessionId, string text, string senderId) => new(
        new TeamsIngressTrustContext(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            senderId,
            "tenant-a",
            ReadConversationId(sessionId),
            TeamsConversationScope.Personal,
            $"activity-{Guid.NewGuid():N}",
            TimeProvider.System.GetUtcNow()),
        text,
        new TeamsReplyMetadata(null, null, "https://service.invalid/"));

    private static TeamsApprovalAction CreateApprovalAction(
        SessionId sessionId,
        DeliveredApproval delivered,
        string optionKey,
        string senderId)
    {
        var option = Assert.Single(delivered.Card.Actions, action => action.Action == optionKey);
        return new TeamsApprovalAction(
            new TeamsIngressTrustContext(
                TrustAudience.Public,
                PrincipalClassification.UntrustedExternal,
                TrustBoundary.Public,
                new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
                senderId,
                "tenant-a",
                ReadConversationId(sessionId),
                TeamsConversationScope.Personal,
                $"invoke-{Guid.NewGuid():N}",
                TimeProvider.System.GetUtcNow()),
            option.CorrelationId,
            option.Nonce,
            option.Action,
            null,
            null,
            null,
            delivered.ActivityId,
            "https://service.invalid/");
    }

    private static ToolInteractionRequest ApprovalRequest(
        SessionId sessionId,
        string callId,
        string requesterSenderId,
        PrincipalClassification requesterPrincipal) => new()
        {
            SessionId = sessionId,
            Kind = "approval",
            CallId = new ToolCallId(callId),
            ToolName = new ToolName("shell_execute"),
            DisplayText = "echo contract",
            RequesterSenderId = new SenderId(requesterSenderId),
            RequesterPrincipal = requesterPrincipal,
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

    private async Task<TeamsBindingRouteResult> RouteAsync(
        IActorRef actor,
        SessionId sessionId,
        string text,
        string senderId)
    {
        var result = await RouteResultAsync(actor, sessionId, text, senderId);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, result.Disposition);
        return result;
    }

    private Task<TeamsBindingRouteResult> RouteResultAsync(
        IActorRef actor,
        SessionId sessionId,
        string text,
        string senderId) => actor.Ask<TeamsBindingRouteResult>(
        new TeamsBindingIngress(CreateInbound(sessionId, text, senderId), TestContext.Current.CancellationToken),
        TestContext.Current.CancellationToken);

    private async Task<DeliveredApproval> WaitForApprovalAsync(RecordingTeamsReplyClient client)
    {
        DeliveredApproval? approval = null;
        await AwaitAssertAsync(() =>
        {
            approval = Assert.Single(client.DeliveredApprovals);
        }, cancellationToken: TestContext.Current.CancellationToken);
        return approval!;
    }

    private async Task<TeamsApprovalActionResult> SubmitApprovalAsync(
        IActorRef actor,
        SessionId sessionId,
        DeliveredApproval delivered,
        string optionKey,
        string senderId) => await actor.Ask<TeamsApprovalActionResult>(
        new TeamsBindingApprovalAction(
            CreateApprovalAction(sessionId, delivered, optionKey, senderId),
            TestContext.Current.CancellationToken),
        TestContext.Current.CancellationToken);

    private static string ReadConversationId(SessionId sessionId)
    {
        Assert.True(TeamsSessionIdentifierCodec.TryParse(sessionId, out var identifier, out _));
        return identifier.ConversationId;
    }

    private string NextActorName() => $"teams-contract-{Interlocked.Increment(ref _actorCounter)}";

    private sealed class RecordingTeamsReplyClient(params TeamsDeliveryResult[] results) : ITeamsReplyClient
    {
        private readonly ConcurrentQueue<TeamsDeliveryResult> _results = new(results);
        private int _deliveryCounter;

        public ConcurrentQueue<DeliveredApproval> DeliveredApprovals { get; } = new();

        public Task<TeamsDeliveryResult> DeliverAsync(
            TeamsOutboundMessage message,
            CancellationToken cancellationToken = default)
        {
            var result = _results.TryDequeue(out var configured)
                ? configured
                : new TeamsDeliveryResult(
                    TeamsDeliveryStatus.Delivered,
                    $"activity-{Interlocked.Increment(ref _deliveryCounter)}");
            if (message.ApprovalCard is { } card && result.IsSuccess && result.ActivityId is { } activityId)
                DeliveredApprovals.Enqueue(new DeliveredApproval(card, activityId));
            return Task.FromResult(result);
        }

        public Task<TeamsDeliveryResult> SendTypingAsync(
            TeamsOutboundDestination destination,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered));
    }

    private sealed record DeliveredApproval(TeamsApprovalCard Card, string ActivityId);

    private sealed class StopOnFailureParent(Props childProps, IActorRef observer) : ReceiveActor
    {
        protected override void PreStart()
        {
            observer.Tell(Context.ActorOf(childProps, "binding"));
            base.PreStart();
        }

        protected override SupervisorStrategy SupervisorStrategy() =>
            new OneForOneStrategy(_ => Directive.Stop, loggingEnabled: false);
    }
}
