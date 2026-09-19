// -----------------------------------------------------------------------
// <copyright file="TeamsPersonalRoutingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence;
using Akka.Persistence.Hosting;
using Akka.Serialization;
using Google.Protobuf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Apps.Schema.Entities;
using Microsoft.Teams.Core.Schema;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Channels.Teams.Serialization;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Daemon.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using SkiaSharp;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;
using LegacyProto = Netclaw.Actors.Serialization.Proto;
using TeamsAccount = Microsoft.Teams.Apps.Schema.TeamsChannelAccount;
using TeamsChannel = Microsoft.Teams.Apps.Schema.TeamsChannel;
using TeamsChannelData = Microsoft.Teams.Apps.Schema.TeamsChannelData;
using TeamsConversation = Microsoft.Teams.Apps.Schema.TeamsConversation;
using TeamsConversationType = Microsoft.Teams.Apps.Schema.ConversationType;
using TeamsTeam = Microsoft.Teams.Apps.Schema.Team;

namespace Netclaw.Daemon.Tests.Configuration;

[Collection("TeamsTelemetry")]
public sealed class TeamsPersonalRoutingTests(ITestOutputHelper output) : PersistenceTestKit(output: output)
{
    private static readonly byte[] PngBytes = TestImages.SmallPng();

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        base.ConfigureAkka(builder, provider);
        builder.WithNetclawSerialization().WithTeamsPersistenceSerialization();
    }

    [Fact]
    public void Teams_persistence_records_use_the_teams_owned_v2_serializer()
    {
        var value = new TeamsProactiveDeliveryRecorded
        {
            DeliveryKey = "reminder-v2:1",
            State = (int)TeamsProactiveDeliveryState.Sent,
            DestinationGeneration = 1
        };

        var serializer = Sys.Serialization.FindSerializerFor(value);
        var manifest = Assert.IsAssignableFrom<SerializerWithStringManifest>(serializer).Manifest(value);
        var restored = Assert.IsType<TeamsProactiveDeliveryRecorded>(
            Sys.Serialization.Deserialize(serializer.ToBinary(value), serializer.Identifier, manifest));

        Assert.Equal(151, serializer.Identifier);
        Assert.Equal("teams-delivery-recorded-v2", manifest);
        Assert.Equal(value, restored);
    }

    [Fact]
    public void Teams_approval_persistence_roundtrips_offered_keys_and_tool_scope()
    {
        var value = new TeamsApprovalPendingCreated
        {
            CallId = "call-approval",
            CorrelationId = "correlation_123",
            NonceHash = new string('a', 64),
            RequesterSenderId = "user-a",
            ExpiresAtUnixMilliseconds = 4_102_444_800_000,
            OfferedOptionKeys =
            [
                ApprovalOptionKeys.ApproveOnce,
                ApprovalOptionKeys.ApproveEverywhere,
                ApprovalOptionKeys.Deny
            ],
            IsMcpTool = true,
            ToolName = "filesystem/read_file",
            RequestDisplayText = "Read README.md"
        };

        var serializer = Sys.Serialization.FindSerializerFor(value);
        var manifest = Assert.IsAssignableFrom<SerializerWithStringManifest>(serializer).Manifest(value);
        var restored = Assert.IsType<TeamsApprovalPendingCreated>(
            Sys.Serialization.Deserialize(serializer.ToBinary(value), serializer.Identifier, manifest));

        Assert.Equal(value.OfferedOptionKeys, restored.OfferedOptionKeys);
        Assert.True(restored.IsMcpTool);
        Assert.Equal(value.ToolName, restored.ToolName);
        Assert.Equal(value.RequestDisplayText, restored.RequestDisplayText);
    }

    [Fact]
    public void Teams_approval_card_reissue_roundtrips_the_replacement_binding()
    {
        var value = new TeamsApprovalCardReissued
        {
            CorrelationId = "correlation_123",
            NonceHash = new string('b', 64),
            ExpiresAtUnixMilliseconds = 4_102_444_800_000
        };

        var serializer = Sys.Serialization.FindSerializerFor(value);
        var manifest = Assert.IsAssignableFrom<SerializerWithStringManifest>(serializer).Manifest(value);
        var restored = Assert.IsType<TeamsApprovalCardReissued>(
            Sys.Serialization.Deserialize(serializer.ToBinary(value), serializer.Identifier, manifest));

        Assert.Equal("teams-approval-reissued-v2", manifest);
        Assert.Equal(value, restored);
    }

    [Fact]
    public void Binding_snapshot_roundtrips_the_last_invalidated_destination_generation()
    {
        var value = new TeamsBindingSnapshot([])
        {
            LastDestinationGeneration = 7,
            ProactiveDeliveries = [new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "snapshot-roundtrip:1",
                State = (int)TeamsProactiveDeliveryState.FailedPermanent,
                DestinationGeneration = 7,
                InvalidatesDestination = true
            }]
        };

        var serializer = Sys.Serialization.FindSerializerFor(value);
        var manifest = Assert.IsAssignableFrom<SerializerWithStringManifest>(serializer).Manifest(value);
        var restored = Assert.IsType<TeamsBindingSnapshot>(
            Sys.Serialization.Deserialize(serializer.ToBinary(value), serializer.Identifier, manifest));

        Assert.Equal(7, restored.LastDestinationGeneration);
        Assert.Equal(value.ProactiveDeliveries, restored.ProactiveDeliveries);
    }

    [Fact]
    public void Channel_activity_mapping_roundtrips_the_established_sender_fingerprint()
    {
        var value = new TeamsChannelActivityMapped(
            ActivityFingerprint.Create("channel-root"),
            "channel-session",
            null,
            ActivityFingerprint.Create("approved-human"));

        var serializer = Sys.Serialization.FindSerializerFor(value);
        var manifest = Assert.IsAssignableFrom<SerializerWithStringManifest>(serializer).Manifest(value);
        var restored = Assert.IsType<TeamsChannelActivityMapped>(
            Sys.Serialization.Deserialize(serializer.ToBinary(value), serializer.Identifier, manifest));

        Assert.Equal(value, restored);
    }

    [Fact]
    public async Task Http_personal_capture_survives_request_scope_disposal_and_uses_the_app_level_reply_client()
    {
        var requestLifetime = new RequestLifetimeProbe();
        var replyClient = new RequestIndependentReplyClient(requestLifetime);
        await using var app = await BuildRequestIndependenceHostAsync(requestLifetime, replyClient);
        var sessionId = CreateSessionId("tenant-a", "request-personal");
        using var inboundCancellation = new CancellationTokenSource();
        using var request = CreateTeamsActivityRequest(CreateSdkPersonalMessage("request-personal", "request-personal-activity"));
        using var response = await app.GetTestClient().SendAsync(request, inboundCancellation.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ReceiveOutputSubscriber();
        response.Dispose();
        inboundCancellation.Cancel();
        await AwaitAssertAsync(() => Assert.True(requestLifetime.AllDisposed), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(ActorRegistry.For(Sys).TryGet<TeamsGatewayActorKey>(out var gateway));
        gateway.Tell(CreateReminder(sessionId, "request-personal:1"));
        var subscriber = ReceiveNextOutputSubscriber();
        subscriber.Tell(new TextOutput("reply after request disposal")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId("request-personal:1")
        });

        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var delivery = Assert.Single(replyClient.Messages);
        Assert.Equal(TeamsConversationScope.Personal, delivery.Destination.Scope);
        Assert.Equal("request-personal", delivery.Destination.ConversationId);
        Assert.Equal("user-a", delivery.Destination.UserId);
        Assert.Equal("https://request-service.invalid/", delivery.Destination.ServiceUrl);
        Assert.Equal("reply after request disposal", delivery.Text);
        Assert.True(requestLifetime.AllDisposed);
    }

    [Theory]
    [InlineData("approve_once", "Approval Granted")]
    [InlineData("deny", "Approval Denied")]
    public async Task Http_personal_approval_action_replaces_the_source_card_in_place(
        string selectedAction,
        string expectedTerminalTitle)
    {
        var requestLifetime = new RequestLifetimeProbe();
        var replyClient = new RequestIndependentReplyClient(requestLifetime);
        var sessionManager = CreateTestProbe();
        await using var app = await BuildRequestIndependenceHostAsync(requestLifetime, replyClient, sessionManager.Ref);
        var sessionId = CreateSessionId("tenant-a", "request-approval");

        using (var request = CreateTeamsActivityRequest(CreateSdkPersonalMessage("request-approval", "request-approval-input")))
        using (var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await sessionManager.ExpectMsgAsync<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        var subscription = await sessionManager.ExpectMsgAsync<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        await sessionManager.ExpectMsgAsync<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
        subscription.Subscriber.Tell(CreateApprovalRequest(sessionId, "request-approval-call", CreateStandardApprovalOptions()));
        await AwaitAssertAsync(
            () => Assert.Single(replyClient.Messages),
            cancellationToken: TestContext.Current.CancellationToken);
        var approvalCard = replyClient.Messages[0].ApprovalCard;
        Assert.NotNull(approvalCard);
        var selectedApprovalAction = Assert.Single(approvalCard.Actions, action => action.Action == selectedAction);

        using var approvalRequest = CreateTeamsActivityRequest(CreateSdkPersonalApprovalAction(
            "request-approval",
            "request-independent-activity",
            selectedApprovalAction.CorrelationId,
            selectedApprovalAction.Nonce,
            selectedApprovalAction.Action));
        var approvalResponseTask = app.GetTestClient().SendAsync(approvalRequest, TestContext.Current.CancellationToken);

        var feedback = await sessionManager.ExpectMsgAsync<ToolInteractionResponse>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, feedback.SessionId);
        Assert.Equal(selectedAction, feedback.SelectedKey.Value);
        sessionManager.LastSender.Tell(new CommandAck(sessionId));

        using (var response = await approvalResponseTask)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var terminalCard = document.RootElement.GetProperty("value");
            Assert.Equal("application/vnd.microsoft.card.adaptive", document.RootElement.GetProperty("type").GetString());
            Assert.Equal("AdaptiveCard", terminalCard.GetProperty("type").GetString());
            Assert.Equal(expectedTerminalTitle, terminalCard.GetProperty("body")[0].GetProperty("columns")[1].GetProperty("items")[0].GetProperty("text").GetString());
            Assert.Empty(terminalCard.GetProperty("actions").EnumerateArray());
        }

        Assert.Single(replyClient.Messages);
    }

    [Theory]
    [InlineData(ApprovalOptionKeys.ApproveOnce, "Approval Granted", false)]
    [InlineData(ApprovalOptionKeys.Deny, "Approval Denied", false)]
    [InlineData(ApprovalOptionKeys.ApproveOnce, "Approval Granted", true)]
    [InlineData(ApprovalOptionKeys.Deny, "Approval Denied", true)]
    public async Task Http_channel_approval_action_without_channel_data_replaces_the_source_card_in_place(
        string selectedAction,
        string expectedTerminalTitle,
        bool isThreadReply)
    {
        var requestLifetime = new RequestLifetimeProbe();
        var replyClient = new RequestIndependentReplyClient(requestLifetime);
        var sessionManager = CreateTestProbe();
        await using var app = await BuildRequestIndependenceHostAsync(
            requestLifetime,
            replyClient,
            sessionManager.Ref,
            includeChannel: true);
        const string conversationId = "request-channel-approval;messageid=request-channel-approval-root";
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel(
            "tenant-a",
            conversationId,
            "request-channel-approval-root",
            out var sessionId,
            out _));

        using (var request = CreateTeamsActivityRequest(CreateSdkChannelRootMessage(
                   conversationId,
                   "request-channel-approval-root")))
        using (var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await sessionManager.ExpectMsgAsync<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        var subscription = await sessionManager.ExpectMsgAsync<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        await sessionManager.ExpectMsgAsync<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
        if (isThreadReply)
        {
            using var threadRequest = CreateTeamsActivityRequest(CreateSdkChannelReplyMessage(conversationId));
            using var threadResponse = await app.GetTestClient().SendAsync(threadRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, threadResponse.StatusCode);
            await sessionManager.ExpectMsgAsync<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
            await sessionManager.ExpectMsgAsync<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
        }

        subscription.Subscriber.Tell(CreateApprovalRequest(sessionId, "request-channel-approval-call", CreateStandardApprovalOptions()));
        await AwaitAssertAsync(
            () => Assert.Single(replyClient.Messages),
            cancellationToken: TestContext.Current.CancellationToken);
        var approvalCard = Assert.IsType<TeamsApprovalCard>(Assert.Single(replyClient.Messages).ApprovalCard);
        var selectedApprovalAction = Assert.Single(approvalCard.Actions, action => action.Action == selectedAction);

        using var approvalRequest = CreateTeamsActivityRequest(CreateSdkChannelApprovalAction(
            conversationId,
            "request-independent-activity",
            selectedApprovalAction.CorrelationId,
            selectedApprovalAction.Nonce,
            selectedApprovalAction.Action));
        var approvalResponseTask = app.GetTestClient().SendAsync(approvalRequest, TestContext.Current.CancellationToken);

        var feedback = await sessionManager.ExpectMsgAsync<ToolInteractionResponse>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, feedback.SessionId);
        Assert.Equal(selectedAction, feedback.SelectedKey.Value);
        sessionManager.LastSender.Tell(new CommandAck(sessionId));

        using var approvalResponse = await approvalResponseTask;
        Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
        using var document = JsonDocument.Parse(await approvalResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var terminalCard = document.RootElement.GetProperty("value");
        Assert.Equal(expectedTerminalTitle, terminalCard.GetProperty("body")[0].GetProperty("columns")[1].GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Empty(terminalCard.GetProperty("actions").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_channel_approval_action_without_channel_data_accepts_a_sender_with_exact_channel_principal_access(
        bool useExactChannelGroupAccess)
    {
        var requestLifetime = new RequestLifetimeProbe();
        var replyClient = new RequestIndependentReplyClient(requestLifetime);
        var sessionManager = CreateTestProbe();
        await using var app = await BuildRequestIndependenceHostAsync(
            requestLifetime,
            replyClient,
            sessionManager.Ref,
            includeChannel: true,
            useExactChannelUserAccess: !useExactChannelGroupAccess,
            useExactChannelGroupAccess: useExactChannelGroupAccess);
        const string conversationId = "request-channel-approval;messageid=request-channel-approval-root";
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel(
            "tenant-a",
            conversationId,
            "request-channel-approval-root",
            out var sessionId,
            out _));

        using (var request = CreateTeamsActivityRequest(CreateSdkChannelRootMessage(
                   conversationId,
                   "request-channel-approval-root")))
        using (var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await sessionManager.ExpectMsgAsync<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        var subscription = await sessionManager.ExpectMsgAsync<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        await sessionManager.ExpectMsgAsync<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
        subscription.Subscriber.Tell(CreateApprovalRequest(sessionId, "request-channel-exact-call", CreateStandardApprovalOptions()));
        await AwaitAssertAsync(
            () => Assert.Single(replyClient.Messages),
            cancellationToken: TestContext.Current.CancellationToken);
        var approvalCard = Assert.IsType<TeamsApprovalCard>(Assert.Single(replyClient.Messages).ApprovalCard);
        var approve = Assert.Single(approvalCard.Actions, action => action.Action == ApprovalOptionKeys.ApproveOnce);

        using var approvalRequest = CreateTeamsActivityRequest(CreateSdkChannelApprovalAction(
            conversationId,
            "request-independent-activity",
            approve.CorrelationId,
            approve.Nonce,
            approve.Action));
        var approvalResponseTask = app.GetTestClient().SendAsync(approvalRequest, TestContext.Current.CancellationToken);

        var feedback = await sessionManager.ExpectMsgAsync<ToolInteractionResponse>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, feedback.SessionId);
        Assert.Equal(ApprovalOptionKeys.ApproveOnce, feedback.SelectedKey.Value);
        sessionManager.LastSender.Tell(new CommandAck(sessionId));

        using var approvalResponse = await approvalResponseTask;
        Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
    }

    [Fact]
    public async Task Http_channel_root_capture_survives_request_scope_disposal_without_a_top_level_fallback()
    {
        var requestLifetime = new RequestLifetimeProbe();
        var replyClient = new RequestIndependentReplyClient(requestLifetime);
        await using var app = await BuildRequestIndependenceHostAsync(requestLifetime, replyClient, includeChannel: true);
        const string conversationId = "request-channel;messageid=request-root";
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel(
            "tenant-a", conversationId, "request-root", out var sessionId, out _));
        using var request = CreateTeamsActivityRequest(CreateSdkChannelRootMessage(conversationId, "request-root"));
        using var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ReceiveOutputSubscriber();
        response.Dispose();
        await AwaitAssertAsync(() => Assert.True(requestLifetime.AllDisposed), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(ActorRegistry.For(Sys).TryGet<TeamsGatewayActorKey>(out var gateway));
        gateway.Tell(CreateReminder(sessionId, "request-channel:1"));
        var subscriber = ReceiveNextOutputSubscriber();
        subscriber.Tell(new TextOutput("channel reply after request disposal")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId("request-channel:1")
        });

        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var delivery = Assert.Single(replyClient.Messages);
        Assert.Equal(TeamsConversationScope.Channel, delivery.Destination.Scope);
        Assert.Equal(conversationId, delivery.Destination.ConversationId);
        Assert.Equal("request-root", delivery.Destination.RootActivityId);
        Assert.Equal("team-a", delivery.Destination.TeamId);
        Assert.Equal("channel-a", delivery.Destination.ChannelId);
        Assert.Equal("request-root", delivery.ReplyToActivityId);
        Assert.True(requestLifetime.AllDisposed);
    }

    [Fact]
    public async Task Cancelled_http_request_captures_no_destination_and_later_generic_delivery_fails_closed()
    {
        var requestLifetime = new RequestLifetimeProbe();
        var replyClient = new RequestIndependentReplyClient(requestLifetime);
        await using var app = await BuildRequestIndependenceHostAsync(requestLifetime, replyClient);
        var sessionId = CreateSessionId("tenant-a", "request-cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var request = CreateTeamsActivityRequest(CreateSdkPersonalMessage("request-cancelled", "request-cancelled-activity"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            app.GetTestClient().SendAsync(request, cancellation.Token));

        Assert.True(ActorRegistry.For(Sys).TryGet<TeamsGatewayActorKey>(out var gateway));
        var dispatcher = CreateTestProbe();
        gateway.Tell(CreateReminder(sessionId, "request-cancelled:1"), dispatcher.Ref);
        var nack = await dispatcher.ExpectMsgAsync<CommandNack>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("destination", nack.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(replyClient.Messages);
    }

    [Fact]
    public void Proactive_destination_resolution_is_explicit_and_fail_closed()
    {
        var session = CreateSessionId("tenant-a", "conversation-a");
        var other = CreateSessionId("tenant-a", "conversation-b");
        var current = new TeamsProactiveDestinationCandidate(session, TeamsConversationScope.Personal, 1, true);
        var invalid = new TeamsProactiveDestinationCandidate(other, TeamsConversationScope.Personal, 1, false);

        Assert.Equal(TeamsDestinationResolutionDisposition.Resolved,
            TeamsProactiveDestinationResolver.Resolve(session, TeamsConversationScope.Personal, null, [current]).Disposition);
        Assert.Equal(TeamsDestinationResolutionDisposition.Unavailable,
            TeamsProactiveDestinationResolver.Resolve(session, TeamsConversationScope.Personal, null, [invalid]).Disposition);
        Assert.Equal(TeamsDestinationResolutionDisposition.Rejected,
            TeamsProactiveDestinationResolver.Resolve(session, TeamsConversationScope.Personal, other.Value, [current, new(other, TeamsConversationScope.Personal, 1, true)]).Disposition);
        Assert.Equal(TeamsDestinationResolutionDisposition.Unavailable,
            TeamsProactiveDestinationResolver.Resolve(session, TeamsConversationScope.Channel, null, [current]).Disposition);
        Assert.Equal(TeamsDestinationResolutionDisposition.Ambiguous,
            TeamsProactiveDestinationResolver.Resolve(session, TeamsConversationScope.Personal, null, [current, current]).Disposition);
    }

    [Fact]
    public async Task Allowed_personal_activity_dispatches_once_with_final_personal_trust_context()
    {
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var dependencies = CreateDependencies(pipeline);
        var sessionId = CreateSessionId("tenant-a", "conversation-a");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies));

        var result = await RouteAsync(actor, CreateActivity("activity-a", "tenant-a", "conversation-a"));

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, result.Disposition);
        var dispatched = ReceiveDispatchedMessage();
        Assert.Equal(sessionId, dispatched.SessionId);
        Assert.NotNull(dispatched.Source);
        Assert.Equal(TrustAudience.Personal, dispatched.Source!.Audience);
        Assert.Equal(TrustBoundary.Personal, dispatched.Source.Boundary);
        Assert.Equal(PrincipalClassification.TrustedInternal, dispatched.Source.Principal);
        Assert.Equal("activity-a", dispatched.Source.MessageId);

        var duplicate = await RouteAsync(actor, CreateActivity("activity-a", "tenant-a", "conversation-a"));

        Assert.Equal(TeamsBindingRouteDisposition.Duplicate, duplicate.Disposition);
    }

    [Fact]
    public async Task High_risk_personal_activity_is_blocked_before_pipeline_dispatch()
    {
        var pipeline = CreatePipeline(TestActor);
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-a", "conversation-injection-blocked"),
            CreateDependencies(
                pipeline,
                detector: new FixedTeamsPromptInjectionDetector(
                    PromptInjectionResult.Detected(PromptInjectionRisk.High, "injection detected")))));

        var result = await RouteAsync(
            actor,
            CreateActivity("activity-injection-blocked", "tenant-a", "conversation-injection-blocked"));

        Assert.Equal(TeamsBindingRouteDisposition.Denied, result.Disposition);
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Unavailable_prompt_detector_fails_closed_before_pipeline_dispatch()
    {
        var pipeline = CreatePipeline(TestActor);
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-a", "conversation-injection-unavailable"),
            CreateDependencies(pipeline, detector: new ThrowingTeamsPromptInjectionDetector())));

        var result = await RouteAsync(
            actor,
            CreateActivity("activity-injection-unavailable", "tenant-a", "conversation-injection-unavailable"));

        Assert.Equal(TeamsBindingRouteDisposition.Unavailable, result.Disposition);
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Disabled_direct_messages_and_an_empty_user_allow_list_deny_without_a_pipeline_turn()
    {
        var disabledPipeline = CreatePipeline(TestActor);
        var disabled = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-a", "conversation-disabled"),
            CreateDependencies(disabledPipeline, allowDirectMessages: false)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Denied,
            (await RouteAsync(disabled, CreateActivity("activity-disabled", "tenant-a", "conversation-disabled"))).Disposition);

        var empty = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-a", "conversation-empty"),
            CreateDependencies(disabledPipeline, allowedUserIds: [])));

        Assert.Equal(
            TeamsBindingRouteDisposition.Denied,
            (await RouteAsync(empty, CreateActivity("activity-empty", "tenant-a", "conversation-empty"))).Disposition);

        var wrongUser = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-a", "conversation-wrong-user"),
            CreateDependencies(disabledPipeline, allowedUserIds: ["User-A"])));

        Assert.Equal(
            TeamsBindingRouteDisposition.Denied,
            (await RouteAsync(wrongUser, CreateActivity("activity-wrong-user", "tenant-a", "conversation-wrong-user"))).Disposition);

        var wrongTenant = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-b", "conversation-wrong-tenant"),
            CreateDependencies(disabledPipeline)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Denied,
            (await RouteAsync(wrongTenant, CreateActivity("activity-wrong-tenant", "tenant-b", "conversation-wrong-tenant"))).Disposition);

        var oversizedActivity = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-a", "conversation-oversized-activity"),
            CreateDependencies(disabledPipeline)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Denied,
            (await RouteAsync(
                oversizedActivity,
                CreateActivity(
                    new string('a', TeamsSessionIdentifierCodec.MaxRawIdentifierBytes + 1),
                    "tenant-a",
                    "conversation-oversized-activity"))).Disposition);
    }

    [Fact]
    public async Task A_durable_activity_reservation_suppresses_a_duplicate_after_binding_restart()
    {
        var pipeline = CreatePipeline(TestActor);
        var dependencies = CreateDependencies(pipeline);
        var sessionId = CreateSessionId("tenant-a", "conversation-restart");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-restart-first");
        var activity = CreateActivity("activity-restart-活動", "tenant-a", "conversation-restart");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        ReceiveDispatchedMessage();
        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-restart-second");

        Assert.Equal(TeamsBindingRouteDisposition.Duplicate, (await RouteAsync(recovered, activity)).Disposition);
    }

    [Fact]
    public async Task A_passivated_binding_rehydrates_its_durable_activity_reservation()
    {
        var pipeline = CreatePipeline(TestActor);
        var dependencies = CreateDependencies(pipeline);
        var sessionId = CreateSessionId("tenant-a", "conversation-passivation");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-passivation-first");
        var activity = CreateActivity("activity-passivation", "tenant-a", "conversation-passivation");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        ReceiveDispatchedMessage();
        Watch(actor);
        actor.Tell(ReceiveTimeout.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-passivation-second");

        Assert.Equal(TeamsBindingRouteDisposition.Duplicate, (await RouteAsync(recovered, activity)).Disposition);
    }

    [Fact]
    public async Task Processed_activity_retention_evicts_the_oldest_identifier_deterministically()
    {
        var sink = Sys.ActorOf(DiscardActor.Create(), "teams-discard-session-manager");
        var pipeline = CreatePipeline(sink);
        var dependencies = CreateDependencies(pipeline);
        var sessionId = CreateSessionId("tenant-a", "conversation-retention");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies));

        for (var index = 0; index <= TeamsSessionBindingActor.ProcessedActivityCapacity; index++)
        {
            var result = await RouteAsync(actor, CreateActivity($"activity-{index}", "tenant-a", "conversation-retention"));
            Assert.Equal(TeamsBindingRouteDisposition.Accepted, result.Disposition);
        }

        var retained = await RouteAsync(actor, CreateActivity("activity-1", "tenant-a", "conversation-retention"));
        var evicted = await RouteAsync(actor, CreateActivity("activity-0", "tenant-a", "conversation-retention"));

        Assert.Equal(TeamsBindingRouteDisposition.Duplicate, retained.Disposition);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, evicted.Disposition);

        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies));

        Assert.Equal(
            TeamsBindingRouteDisposition.Duplicate,
            (await RouteAsync(recovered, CreateActivity("activity-2", "tenant-a", "conversation-retention"))).Disposition);
        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(recovered, CreateActivity("activity-1", "tenant-a", "conversation-retention"))).Disposition);
    }

    [Fact]
    public async Task The_same_activity_identifier_in_another_tenant_uses_an_independent_binding()
    {
        var pipeline = CreatePipeline(TestActor);
        var tenantA = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-a", "conversation-a"),
            CreateDependencies(pipeline, tenantId: "tenant-a")));
        var tenantB = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-b", "conversation-a"),
            CreateDependencies(pipeline, tenantId: "tenant-b")));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(tenantA, CreateActivity("activity-shared", "tenant-a", "conversation-a"))).Disposition);
        ReceiveDispatchedMessage();
        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(tenantB, CreateActivity("activity-shared", "tenant-b", "conversation-a"))).Disposition);
        ReceiveDispatchedMessage();
    }

    [Fact]
    public async Task A_real_conversation_sink_routes_personal_input_once_and_keeps_channel_input_deferred()
    {
        var pipeline = CreatePipeline(TestActor);
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            AllowDirectMessages = true,
            AllowedUserIds = ["user-a"]
        };
        var services = new ServiceCollection();
        services.AddSingleton<ISessionPipeline>(pipeline);
        services.AddSingleton<ITeamsReplyClient, TestTeamsReplyClient>();
        services.AddSingleton<TeamsOutputRenderer>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IPromptInjectionDetector>(SafeTeamsPromptInjectionDetector.Instance);
        using var provider = services.BuildServiceProvider();
        var sink = new TeamsActorConversationIngressSink(Sys, options, provider);
        var personal = CreateActivity("activity-sink", "tenant-a", "conversation-sink");
        var ingress = Sys.ActorOf(Props.Create(() => new TeamsIngressActor(sink, TimeProvider.System)));

        Assert.Equal(
            TeamsIngressRouteDisposition.Routed,
            (await ingress.Ask<TeamsIngressRouteResult>(
                new TeamsIngressReceived(personal, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken)).Disposition);
        ReceiveDispatchedMessage();
        Assert.Equal(
            TeamsIngressRouteDisposition.Duplicate,
            (await ingress.Ask<TeamsIngressRouteResult>(
                new TeamsIngressReceived(personal, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken)).Disposition);

        var channel = new TeamsInboundActivity(
            new TeamsIngressTrustContext(
                TrustAudience.Public,
                PrincipalClassification.UntrustedExternal,
                TrustBoundary.Public,
                new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
                "user-a",
                "tenant-a",
                "conversation-channel",
                TeamsConversationScope.Channel,
                "activity-channel",
                TimeProvider.System.GetUtcNow()),
            "hello");

        Assert.Equal(TeamsIngressSinkResult.Denied, await sink.RouteAsync(channel, TestContext.Current.CancellationToken));
        var blockedSession = CreateSessionId("tenant-a", "conversation-channel");
        await Assert.ThrowsAsync<ActorNotFoundException>(() =>
            Sys.ActorSelection($"/user/{TeamsActorNames.Conversation(blockedSession)}")
                .ResolveOne(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_real_conversation_sink_routes_channel_roots_and_replies_and_indexes_mutations()
    {
        var pipeline = CreatePipeline(TestActor);
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            MentionOnly = true,
            AllowedTeamIds = ["team-a"],
            AllowedChannelIds = ["channel-a"]
        };
        var services = new ServiceCollection();
        services.AddSingleton<ISessionPipeline>(pipeline);
        services.AddSingleton<ITeamsReplyClient, TestTeamsReplyClient>();
        services.AddSingleton<TeamsOutputRenderer>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IPromptInjectionDetector>(SafeTeamsPromptInjectionDetector.Instance);
        using var provider = services.BuildServiceProvider();
        var sink = new TeamsActorConversationIngressSink(Sys, options, provider);
        var root = CreateChannelActivity("root-a", "conversation-a;messageid=root-a");

        Assert.Equal(TeamsIngressSinkResult.Accepted, await sink.RouteAsync(root, TestContext.Current.CancellationToken));
        var first = ReceiveDispatchedMessage();
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel("tenant-a", root.Trust.ConversationId, "root-a", out var expected, out _));
        Assert.Equal(expected, first.SessionId);

        var reply = CreateChannelActivity("reply-a", "conversation-a;messageid=root-a", isMentioned: false);
        Assert.Equal(TeamsIngressSinkResult.Accepted, await sink.RouteAsync(reply, TestContext.Current.CancellationToken));

        var update = CreateChannelActivity("root-a", "conversation-a;messageid=root-a", TeamsIngressActivityKind.MessageUpdate);
        var unknownDelete = CreateChannelActivity("unknown", "conversation-a;messageid=root-a", TeamsIngressActivityKind.MessageDelete);
        Assert.Equal(TeamsIngressSinkResult.Accepted, await sink.RouteAsync(update, TestContext.Current.CancellationToken));
        Assert.Equal(TeamsIngressSinkResult.Ignored, await sink.RouteAsync(unknownDelete, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Group_chat_routes_one_flat_session_with_each_authenticated_sender()
    {
        var pipeline = CreatePipeline(TestActor);
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            AllowGroupChats = true,
            MentionOnly = true,
            AllowedGroupChatIds = ["19:group-chat@thread.v2"],
            AllowedUserIds = ["user-a", "user-b"]
        };
        using var provider = CreateConversationServiceProvider(pipeline);
        var sink = new TeamsActorConversationIngressSink(Sys, options, provider);

        var firstActivity = CreateGroupChatActivity("group-first", senderId: "user-a");
        Assert.Equal(TeamsIngressSinkResult.Accepted, await sink.RouteAsync(firstActivity, TestContext.Current.CancellationToken));
        var first = ReceiveGroupChatMessage();

        var secondActivity = CreateGroupChatActivity("group-second", senderId: "user-b");
        Assert.Equal(TeamsIngressSinkResult.Accepted, await sink.RouteAsync(secondActivity, TestContext.Current.CancellationToken));
        var second = ReceiveGroupChatMessage();

        Assert.True(TeamsSessionIdentifierCodec.TryCreateGroupChat(
            "tenant-a",
            "19:group-chat@thread.v2",
            out var expectedSessionId,
            out _));
        Assert.Equal(expectedSessionId, first.SessionId);
        Assert.Equal(expectedSessionId, second.SessionId);
        Assert.Equal(TrustAudience.Team, first.Source!.Audience);
        Assert.Equal(new SenderId("user-a"), first.Source.SenderId);
        Assert.Equal(new SenderId("user-b"), second.Source!.SenderId);

        Assert.Equal(
            TeamsIngressSinkResult.Ignored,
            await sink.RouteAsync(
                CreateGroupChatActivity("group-unmentioned", isMentioned: false),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Group_chat_approval_recovers_after_sink_recreation_and_remains_single_use()
    {
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var replyClient = new RecordingTeamsReplyClient();
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            AllowGroupChats = true,
            MentionOnly = true,
            AllowedGroupChatIds = ["19:group-chat@thread.v2"],
            AllowedUserIds = ["user-a", "user-b"]
        };
        using var provider = CreateConversationServiceProvider(pipeline, replyClient: replyClient);
        var firstSink = new TeamsActorConversationIngressSink(Sys, options, provider);
        var activity = CreateGroupChatActivity("group-approval-input", senderId: "user-a");

        Assert.Equal(TeamsIngressSinkResult.Accepted, await firstSink.RouteAsync(activity, TestContext.Current.CancellationToken));
        var subscriber = ReceiveGroupChatOutputSubscriber();
        Assert.True(TeamsSessionIdentifierCodec.TryCreateGroupChat(
            "tenant-a",
            "19:group-chat@thread.v2",
            out var sessionId,
            out _));
        subscriber.Tell(CreateApprovalRequest(sessionId, "group-approval-call", CreateStandardApprovalOptions()));
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var approve = Assert.Single(
            Assert.IsType<TeamsApprovalCard>(replyClient.Messages[0].ApprovalCard).Actions,
            action => action.Action == ApprovalOptionKeys.ApproveOnce);

        var conversation = await Sys.ActorSelection($"/user/{TeamsActorNames.Conversation(sessionId)}")
            .ResolveOne(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Watch(conversation);
        conversation.Tell(PoisonPill.Instance);
        ExpectTerminated(conversation, cancellationToken: TestContext.Current.CancellationToken);

        var recoveredSink = new TeamsActorConversationIngressSink(Sys, options, provider);
        var callback = CreateGroupChatApprovalAction(
            "user-a",
            approve.CorrelationId,
            approve.Nonce,
            approve.Action);
        var accepted = await recoveredSink.RouteApprovalAsync(callback, TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Accepted, accepted.Disposition);
        await AwaitAssertAsync(() => Assert.Single(pipeline.Feedback), cancellationToken: TestContext.Current.CancellationToken);
        var feedback = Assert.IsType<ToolInteractionResponse>(pipeline.Feedback[0]);
        Assert.Equal(ApprovalOptionKeys.ApproveOnceKey, feedback.SelectedKey);
        Assert.Equal(new SenderId("user-a"), feedback.SenderId);

        var replay = await recoveredSink.RouteApprovalAsync(callback, TestContext.Current.CancellationToken);
        Assert.Equal(TeamsApprovalActionDisposition.AlreadyProcessed, replay.Disposition);
        Assert.Single(pipeline.Feedback);

        var wrongRequester = await recoveredSink.RouteApprovalAsync(
            CreateGroupChatApprovalAction("user-b", approve.CorrelationId, approve.Nonce, approve.Action),
            TestContext.Current.CancellationToken);
        Assert.Equal(TeamsApprovalActionDisposition.Rejected, wrongRequester.Disposition);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Group_chat_approval_rejects_a_sender_outside_the_global_principal_rules()
    {
        var pipeline = CreatePipeline(TestActor);
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            AllowGroupChats = true,
            AllowedGroupChatIds = ["19:group-chat@thread.v2"],
            AllowedUserIds = ["user-a"]
        };
        using var provider = CreateConversationServiceProvider(pipeline);
        var sink = new TeamsActorConversationIngressSink(Sys, options, provider);

        var result = await sink.RouteApprovalAsync(
            CreateGroupChatApprovalAction("user-b", "group-denied", "group-denied-nonce", ApprovalOptionKeys.Deny),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Rejected, result.Disposition);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Attachment_only_rejection_has_no_empty_model_turn_and_stays_deduplicated()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "attachment-conversation");
        var actor = CreateBindingActor(
            sessionId,
            CreateDependencies(CreatePipeline(TestActor), replyClient: replyClient),
            "teams-attachment-only-rejected");
        var activity = CreateAttachmentActivity(
            "attachment-only-rejected",
            string.Empty,
            [CreateInboundAttachment("image.png", TeamsInboundAttachmentKind.InlineImage, 0)]);

        var first = await RouteAsync(actor, activity);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, first.Disposition);
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Attachments are disabled for Microsoft Teams.", replyClient.Messages[0].Text);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        var duplicate = await RouteAsync(actor, activity);
        Assert.Equal(TeamsBindingRouteDisposition.Duplicate, duplicate.Disposition);
        Assert.Single(replyClient.Messages);
    }

    [Fact]
    public async Task Attachment_only_non_executable_unknown_has_no_model_turn()
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var replyClient = new RecordingTeamsReplyClient();
        var actor = CreateBindingActor(
            CreateSessionId("tenant-a", "attachment-conversation"),
            CreateAttachmentDependencies(CreatePipeline(TestActor), paths, replyClient),
            "teams-attachment-only-unknown");
        var activity = CreateAttachmentActivity(
            "attachment-only-unknown",
            string.Empty,
            [CreateInboundAttachment("unsupported.bin", TeamsInboundAttachmentKind.Unknown, 0)]);

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("not supported", replyClient.Messages[0].Text, StringComparison.Ordinal);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Text_with_a_rejected_attachment_still_creates_one_model_turn()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "attachment-conversation");
        var actor = CreateBindingActor(
            sessionId,
            CreateDependencies(CreatePipeline(TestActor), replyClient: replyClient),
            "teams-attachment-text-rejected");
        var activity = CreateAttachmentActivity(
            "attachment-text-rejected",
            "safe text",
            [CreateInboundAttachment("image.png", TeamsInboundAttachmentKind.InlineImage, 0)]);

        var result = await RouteAsync(actor, activity);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, result.Disposition);
        var dispatched = ReceiveDispatchedMessage();
        Assert.Equal("safe text", dispatched.Content);
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Attachments are disabled for Microsoft Teams.", replyClient.Messages[0].Text);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Accepted_attachment_only_creates_one_model_turn()
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var sessionId = CreateSessionId("tenant-a", "attachment-conversation");
        var actor = CreateBindingActor(
            sessionId,
            CreateAttachmentDependencies(CreatePipeline(TestActor), paths),
            "teams-attachment-only-accepted");
        var activity = CreateAttachmentActivity(
            "attachment-only-accepted",
            string.Empty,
            [CreateInboundAttachment("image.png", TeamsInboundAttachmentKind.InlineImage, 0)]);

        var result = await RouteAsync(actor, activity);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, result.Disposition);
        var dispatched = ReceiveDispatchedMessage();
        Assert.Contains("[attachment]", dispatched.Content, StringComparison.Ordinal);
        Assert.Contains("image.png", dispatched.Content, StringComparison.Ordinal);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    public async Task Provisional_wildcard_inline_image_reaches_the_model_with_a_concrete_verified_mime(string expectedMime)
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var actor = CreateBindingActor(
            CreateSessionId("tenant-a", "attachment-conversation"),
            CreateVerifiedAttachmentDependencies(CreatePipeline(TestActor), paths, BytesFor(expectedMime)),
            $"teams-wildcard-{expectedMime.Replace('/', '-')}");
        var activity = CreateAttachmentActivity(
            $"wildcard-{expectedMime}",
            "inspect this image",
            [CreateInboundAttachment("attachment-1", TeamsInboundAttachmentKind.InlineImage, 0, "image/*")]);

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);

        var dispatched = ReceiveDispatchedMessage();
        var media = Assert.Single(dispatched.MediaReferences);
        Assert.Equal(expectedMime, media.MimeType.Value);
        Assert.EndsWith(MimeTypeCatalog.ExtensionFor(new Netclaw.Media.MimeType(expectedMime)), media.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provisional_wildcard_inline_image_with_non_image_bytes_is_rejected()
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var replyClient = new RecordingTeamsReplyClient();
        var actor = CreateBindingActor(
            CreateSessionId("tenant-a", "attachment-conversation"),
            CreateVerifiedAttachmentDependencies(
                CreatePipeline(TestActor),
                paths,
                "%PDF-1.7"u8.ToArray(),
                replyClient),
            "teams-wildcard-non-image");
        var activity = CreateAttachmentActivity(
            "wildcard-non-image",
            string.Empty,
            [CreateInboundAttachment("attachment-1", TeamsInboundAttachmentKind.InlineImage, 0, "image/*")]);

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("verified image signature", replyClient.Messages[0].Text, StringComparison.Ordinal);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Wildcard_inline_image_with_text_is_accepted_in_a_public_channel_post()
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var dependencies = CreatePublicVerifiedAttachmentDependencies(CreatePipeline(TestActor), paths, PngBytes);
        const string conversationId = "public-wildcard-post;messageid=root-a";
        var actor = Sys.ActorOf(TeamsConversationActor.CreateProps(
            CreateSessionId("tenant-a", conversationId), dependencies));
        var activity = CreateChannelActivity(
            "root-a",
            conversationId,
            text: "inspect this image",
            attachments: [CreateInboundAttachment("attachment-1", TeamsInboundAttachmentKind.InlineImage, 0, "image/*")]);

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteConversationAsync(actor, activity)).Disposition);

        var dispatched = ReceiveDispatchedMessage();
        Assert.Equal(TrustAudience.Public, dispatched.Source!.Audience);
        Assert.Equal("image/png", Assert.Single(dispatched.MediaReferences).MimeType.Value);
    }

    [Fact]
    public async Task Image_only_mentioned_channel_post_with_a_wildcard_inline_image_is_accepted()
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var dependencies = CreatePublicVerifiedAttachmentDependencies(CreatePipeline(TestActor), paths, PngBytes);
        const string conversationId = "public-wildcard-image-only;messageid=root-a";
        var actor = Sys.ActorOf(TeamsConversationActor.CreateProps(
            CreateSessionId("tenant-a", conversationId), dependencies));
        var activity = CreateChannelActivity(
            "root-a",
            conversationId,
            text: string.Empty,
            attachments: [CreateInboundAttachment("attachment-1", TeamsInboundAttachmentKind.InlineImage, 0, "image/*")]);

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteConversationAsync(actor, activity)).Disposition);

        var dispatched = ReceiveDispatchedMessage();
        Assert.Equal(TrustAudience.Public, dispatched.Source!.Audience);
        Assert.Equal("image/png", Assert.Single(dispatched.MediaReferences).MimeType.Value);
    }

    [Fact]
    public async Task Public_channel_rejects_non_image_bytes_behind_a_wildcard_inline_image()
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var replyClient = new RecordingTeamsReplyClient();
        var dependencies = CreatePublicVerifiedAttachmentDependencies(
            CreatePipeline(TestActor),
            paths,
            "%PDF-1.7"u8.ToArray(),
            replyClient);
        const string conversationId = "public-wildcard-non-image;messageid=root-a";
        var actor = Sys.ActorOf(TeamsConversationActor.CreateProps(
            CreateSessionId("tenant-a", conversationId), dependencies));
        var activity = CreateChannelActivity(
            "root-a",
            conversationId,
            text: string.Empty,
            attachments: [CreateInboundAttachment("attachment-1", TeamsInboundAttachmentKind.InlineImage, 0, "image/*")]);

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteConversationAsync(actor, activity)).Disposition);
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("isn't allowed in Public channels", replyClient.Messages[0].Text, StringComparison.Ordinal);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Wildcard_inline_image_is_rejected_when_teams_attachments_are_disabled()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var actor = CreateBindingActor(
            CreateSessionId("tenant-a", "attachment-conversation"),
            CreateDependencies(CreatePipeline(TestActor), replyClient: replyClient),
            "teams-wildcard-attachments-disabled");
        var activity = CreateAttachmentActivity(
            "wildcard-attachments-disabled",
            string.Empty,
            [CreateInboundAttachment("attachment-1", TeamsInboundAttachmentKind.InlineImage, 0, "image/*")]);

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Attachments are disabled for Microsoft Teams.", replyClient.Messages[0].Text);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Attachment_pipeline_failure_after_async_ingress_releases_the_activity_reservation()
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var pipeline = new FailThenDelegatePipeline(CreatePipeline(TestActor));
        var actor = CreateBindingActor(
            CreateSessionId("tenant-a", "attachment-conversation"),
            CreateAttachmentDependencies(pipeline, paths),
            "teams-attachment-pipeline-failure");
        var activity = CreateAttachmentActivity(
            "attachment-pipeline-failure",
            string.Empty,
            [CreateInboundAttachment("image.png", TeamsInboundAttachmentKind.InlineImage, 0)]);

        Assert.Equal(TeamsBindingRouteDisposition.Failed, (await RouteAsync(actor, activity)).Disposition);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        ReceiveDispatchedMessage();
    }

    [Fact]
    public async Task Attachment_pipeline_cancellation_after_async_ingress_releases_the_activity_reservation()
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        using var cancellation = new CancellationTokenSource();
        var pipeline = new CancelThenDelegatePipeline(CreatePipeline(TestActor), cancellation);
        var actor = CreateBindingActor(
            CreateSessionId("tenant-a", "attachment-conversation"),
            CreateAttachmentDependencies(pipeline, paths),
            "teams-attachment-pipeline-cancelled");
        var activity = CreateAttachmentActivity(
            "attachment-pipeline-cancelled",
            string.Empty,
            [CreateInboundAttachment("image.png", TeamsInboundAttachmentKind.InlineImage, 0)]);

        var cancelled = await actor.Ask<TeamsBindingRouteResult>(
            new TeamsBindingIngress(activity, cancellation.Token),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsBindingRouteDisposition.Cancelled, cancelled.Disposition);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        ReceiveDispatchedMessage();
    }

    [Fact]
    public async Task Safe_text_and_rejected_attachment_create_one_safe_model_turn_and_one_rejection()
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "attachment-conversation");
        var actor = CreateBindingActor(
            sessionId,
            CreateAttachmentDependencies(CreatePipeline(TestActor), paths, replyClient),
            "teams-attachment-safe-and-rejected");
        var activity = CreateAttachmentActivity(
            "attachment-safe-and-rejected",
            "safe text",
            [
                CreateInboundAttachment("image.png", TeamsInboundAttachmentKind.InlineImage, 0),
                CreateInboundAttachment("unsupported.bin", TeamsInboundAttachmentKind.Unknown, 1)
            ]);

        var result = await RouteAsync(actor, activity);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, result.Disposition);
        var dispatched = ReceiveDispatchedMessage();
        Assert.Contains("safe text", dispatched.Content, StringComparison.Ordinal);
        Assert.Contains("[attachment]", dispatched.Content, StringComparison.Ordinal);
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("not supported", replyClient.Messages[0].Text, StringComparison.Ordinal);

        var duplicate = await RouteAsync(actor, activity);
        Assert.Equal(TeamsBindingRouteDisposition.Duplicate, duplicate.Disposition);
        Assert.Single(replyClient.Messages);
        ExpectNoMsg(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Approval_first_cold_spawn_preserves_the_detector_for_later_personal_ingress()
    {
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            AllowDirectMessages = true,
            AllowedUserIds = ["user-a"]
        };
        using var provider = CreateConversationServiceProvider(CreatePipeline(TestActor));
        var sink = new TeamsActorConversationIngressSink(Sys, options, provider);

        var approval = await sink.RouteApprovalAsync(
            CreateApprovalAction(
                "tenant-a",
                "conversation-cold-approval",
                "cold-approval-correlation",
                "cold-approval-nonce",
                "synthetic-activity",
                ApprovalOptionKeys.ApproveOnce),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Rejected, approval.Disposition);
        Assert.Equal(
            TeamsIngressSinkResult.Accepted,
            await sink.RouteAsync(
                CreateActivity("activity-after-cold-approval", "tenant-a", "conversation-cold-approval"),
                TestContext.Current.CancellationToken));
        ReceiveDispatchedMessage();
    }

    [Fact]
    public async Task Reminder_first_cold_spawn_preserves_the_detector_for_later_channel_ingress()
    {
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            MentionOnly = true,
            AllowedTeamIds = ["team-a"],
            AllowedChannelIds = ["channel-a"]
        };
        const string conversationId = "conversation-cold-reminder;messageid=root-a";
        var sessionId = CreateChannelSessionId(conversationId);
        using var provider = CreateConversationServiceProvider(CreatePipeline(TestActor));
        var sink = new TeamsActorConversationIngressSink(Sys, options, provider);

        Assert.True(sink.TryGetReminderConversation(sessionId, out var conversation));
        conversation.Tell(new TeamsConversationReminder(CreateReminder(sessionId, "cold-reminder:1")));
        ExpectMsg<CommandNack>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            TeamsIngressSinkResult.Accepted,
            await sink.RouteAsync(
                CreateChannelActivity("activity-after-cold-reminder", conversationId),
                TestContext.Current.CancellationToken));
        ReceiveDispatchedMessage();
    }

    [Fact]
    public async Task Cold_spawn_without_a_detector_still_fails_closed_for_later_ingress()
    {
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            AllowDirectMessages = true,
            AllowedUserIds = ["user-a"]
        };
        using var provider = CreateConversationServiceProvider(CreatePipeline(TestActor), includeDetector: false);
        var sink = new TeamsActorConversationIngressSink(Sys, options, provider);

        var approval = await sink.RouteApprovalAsync(
            CreateApprovalAction(
                "tenant-a",
                "conversation-cold-no-detector",
                "cold-no-detector-correlation",
                "cold-no-detector-nonce",
                "synthetic-activity",
                ApprovalOptionKeys.ApproveOnce),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Rejected, approval.Disposition);
        Assert.Equal(
            TeamsIngressSinkResult.Unavailable,
            await sink.RouteAsync(
                CreateActivity("activity-after-cold-no-detector", "tenant-a", "conversation-cold-no-detector"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Channel_activity_mapping_rehydrates_after_conversation_actor_restart()
    {
        var pipeline = CreatePipeline(TestActor);
        var dependencies = new TeamsConversationDependencies(
            new TeamsChannelOptions
            {
                TenantId = "tenant-a",
                MentionOnly = true,
                AllowedTeamIds = ["team-a"],
                AllowedChannelIds = ["channel-a"]
            },
            pipeline,
            new TestTeamsReplyClient(),
            new TeamsOutputRenderer(),
            TimeProvider.System)
        {
            PromptInjectionDetector = SafeTeamsPromptInjectionDetector.Instance
        };
        const string conversationId = "conversation-recovery;messageid=root-a";
        var parentId = CreateSessionId("tenant-a", conversationId);
        var root = CreateChannelActivity("root-a", conversationId);
        var first = Sys.ActorOf(TeamsConversationActor.CreateProps(parentId, dependencies), "teams-channel-recovery-first");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteConversationAsync(first, root)).Disposition);
        ReceiveDispatchedMessage();

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsConversationActor.CreateProps(parentId, dependencies), "teams-channel-recovery-second");
        var update = CreateChannelActivity("root-a", conversationId, TeamsIngressActivityKind.MessageUpdate);

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteConversationAsync(recovered, update)).Disposition);
    }

    [Fact]
    public async Task Channel_unmentioned_continuation_requires_the_established_root_owner()
    {
        var pipeline = CreatePipeline(TestActor);
        var dependencies = new TeamsConversationDependencies(
            new TeamsChannelOptions
            {
                TenantId = "tenant-a",
                MentionOnly = true,
                AllowedTeamIds = ["team-a"],
                AllowedChannelIds = ["channel-a"],
                AllowedUserIds = ["user-a", "user-b"]
            },
            pipeline,
            new TestTeamsReplyClient(),
            new TeamsOutputRenderer(),
            TimeProvider.System)
        {
            PromptInjectionDetector = SafeTeamsPromptInjectionDetector.Instance
        };
        const string conversationId = "conversation-continuation;messageid=root-a";
        var parent = Sys.ActorOf(TeamsConversationActor.CreateProps(
            CreateSessionId("tenant-a", conversationId), dependencies));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteConversationAsync(parent, CreateChannelActivity("root-a", conversationId))).Disposition);
        ReceiveDispatchedMessage();

        Assert.Equal(
            TeamsBindingRouteDisposition.Ignored,
            (await RouteConversationAsync(parent, CreateChannelActivity(
                "reply-other",
                conversationId,
                isMentioned: false,
                senderId: "user-b"))).Disposition);
        const string unknownConversationId = "conversation-unestablished;messageid=root-unmentioned";
        var unknownParent = Sys.ActorOf(TeamsConversationActor.CreateProps(
            CreateSessionId("tenant-a", unknownConversationId), dependencies));
        Assert.Equal(
            TeamsBindingRouteDisposition.Ignored,
            (await RouteConversationAsync(unknownParent, CreateChannelActivity(
                "root-unmentioned",
                unknownConversationId,
                rootActivityId: "root-unmentioned",
                isMentioned: false))).Disposition);
        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteConversationAsync(parent, CreateChannelActivity(
                "reply-owner",
                conversationId,
                isMentioned: false))).Disposition);
        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        var continuation = ExpectMsg<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel("tenant-a", conversationId, "root-a", out var expected, out _));
        Assert.Equal(expected, continuation.SessionId);
    }

    [Fact]
    public async Task Established_channel_thread_with_text_and_wildcard_inline_image_creates_one_model_turn()
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var pipeline = CreatePipeline(TestActor);
        var dependencies = CreatePublicVerifiedAttachmentDependencies(pipeline, paths, PngBytes);
        const string conversationId = "conversation-inline-image;messageid=root-a";
        var parent = Sys.ActorOf(TeamsConversationActor.CreateProps(
            CreateSessionId("tenant-a", conversationId), dependencies));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteConversationAsync(parent, CreateChannelActivity("root-a", conversationId))).Disposition);
        ReceiveDispatchedMessage();

        var continuation = CreateChannelActivity(
            "reply-with-image",
            conversationId,
            isMentioned: false,
            text: "inspect this image",
            attachments: [CreateInboundAttachment("attachment-1", TeamsInboundAttachmentKind.InlineImage, 0, "image/*")]);
        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteConversationAsync(parent, continuation)).Disposition);

        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        var dispatched = ExpectMsg<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("inspect this image", dispatched.Content, StringComparison.Ordinal);
        Assert.Contains("[attachment]", dispatched.Content, StringComparison.Ordinal);
        Assert.Equal("image/png", Assert.Single(dispatched.MediaReferences).MimeType.Value);
    }

    [Theory]
    [InlineData(0, 10, 15, 20)]
    [InlineData(1, 280, 285, 290)]
    [InlineData(3, 280, 285, 290)]
    [InlineData(10, 280, 285, 290)]
    public void Teams_route_deadlines_cover_each_download_and_scan(
        int attachmentCount,
        int bindingSeconds,
        int conversationSeconds,
        int ingressSeconds)
    {
        var attachments = Enumerable.Range(0, attachmentCount)
            .Select(index => CreateInboundAttachment("image.png", TeamsInboundAttachmentKind.InlineImage, index))
            .ToImmutableArray();
        var activity = CreateAttachmentActivity("deadline-ladder", "inspect", attachments);

        Assert.Equal(TimeSpan.FromSeconds(30), TeamsIngressTimeouts.AttachmentOperation);
        Assert.Equal(TimeSpan.FromSeconds(bindingSeconds), TeamsIngressTimeouts.BindingRoute(activity));
        Assert.Equal(TimeSpan.FromSeconds(conversationSeconds), TeamsIngressTimeouts.ConversationRoute(activity));
        Assert.Equal(TimeSpan.FromSeconds(ingressSeconds), TeamsIngressTimeouts.IngressRoute(activity));
        Assert.True(TeamsIngressTimeouts.ConversationRoute(activity) > TeamsIngressTimeouts.BindingRoute(activity));
        Assert.True(TeamsIngressTimeouts.IngressRoute(activity) > TeamsIngressTimeouts.ConversationRoute(activity));
    }

    [Theory]
    [InlineData("inspect this image", false)]
    [InlineData("", false)]
    [InlineData("inspect this image", true)]
    [InlineData("", true)]
    public async Task Slow_inline_image_completes_the_host_route_once_in_an_established_thread(string text, bool expireDeadline)
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var clock = new FakeTimeProvider();
        var downloader = new GatedTeamsAttachmentDownloader(PngBytes);
        var observationActor = Sys.ActorOf(Props.Create(() => new TeamsRouteObservationActor(TestActor)));
        Sys.EventStream.Subscribe(observationActor, typeof(DeadLetter));
        var pipeline = CreatePipeline(observationActor);
        var options = CreatePublicVerifiedAttachmentDependencies(pipeline, paths, PngBytes).Options;
        var services = new ServiceCollection();
        services.AddSingleton(Sys);
        services.AddSingleton(options);
        services.AddSingleton(pipeline);
        services.AddSingleton<TimeProvider>(clock);
        var replyClient = new RecordingTeamsReplyClient();
        services.AddSingleton<ITeamsReplyClient>(replyClient);
        services.AddSingleton<TeamsOutputRenderer>();
        services.AddSingleton<IPromptInjectionDetector>(SafeTeamsPromptInjectionDetector.Instance);
        services.AddSingleton<ITeamsAttachmentDownloader>(downloader);
        services.AddSingleton<IContentScanner>(new MagicByteContentScanner(new ContentPolicy()));
        services.AddSingleton(new ToolConfig { AudienceProfiles = ToolAudienceProfileDefaults.CreateProfiles() });
        services.AddSingleton(ImageModelCapabilities);
        services.AddSingleton(paths);
        services.AddSingleton<ISessionStorageResolver>(new TestSessionStorageResolver(paths));
        services.AddSingleton<ITeamsConversationIngressSink, TeamsActorConversationIngressSink>();
        using var provider = services.BuildServiceProvider();
        var host = new TeamsIngressActorHost(provider);
        var cancellationToken = TestContext.Current.CancellationToken;
        await host.StartAsync(cancellationToken);
        try
        {
            const string conversationId = "slow-inline-thread;messageid=root-a";
            Assert.Equal(TeamsIngressRouteDisposition.Routed,
                (await host.SubmitAsync(CreateChannelActivity("root-a", conversationId), cancellationToken)).Disposition);
            ReceiveDispatchedMessage();

            var activity = CreateChannelActivity(
                "slow-inline-reply",
                conversationId,
                isMentioned: false,
                text: text,
                attachments: [CreateInboundAttachment("attachment-1", TeamsInboundAttachmentKind.InlineImage, 0, "image/*")]);
            var route = host.SubmitAsync(activity, cancellationToken).AsTask();
            Assert.Same(downloader.Started.Task, await Task.WhenAny(downloader.Started.Task, route));
            await downloader.Started.Task.WaitAsync(cancellationToken);
            clock.Advance(TimeSpan.FromSeconds(31));
            Assert.False(route.IsCompleted);
            Assert.False(downloader.DownloadToken.IsCancellationRequested);
            if (expireDeadline)
                clock.Advance(TeamsIngressTimeouts.InlineImageDownload - TimeSpan.FromSeconds(31));
            else
                downloader.Release.SetResult();

            Assert.Equal(TeamsIngressRouteDisposition.Routed, (await route).Disposition);
            if (!expireDeadline || text.Length > 0)
            {
                ExpectMsg<JoinSession>(cancellationToken: cancellationToken);
                var dispatched = ExpectMsg<SendUserMessage>(cancellationToken: cancellationToken);
                Assert.Equal(CreateChannelSessionId(conversationId), dispatched.SessionId);
                if (expireDeadline)
                {
                    Assert.Empty(dispatched.MediaReferences);
                    Assert.Equal(text, dispatched.Content);
                }
                else
                {
                    Assert.Equal("image/png", Assert.Single(dispatched.MediaReferences).MimeType.Value);
                    Assert.Contains("[attachment]", dispatched.Content, StringComparison.Ordinal);
                    if (text.Length > 0)
                        Assert.Contains(text, dispatched.Content, StringComparison.Ordinal);
                }
            }
            if (expireDeadline)
                Assert.Contains("Timed out downloading", Assert.Single(replyClient.Messages).Text, StringComparison.Ordinal);
            else
                Assert.Empty(replyClient.Messages);

            Assert.Equal(TeamsIngressRouteDisposition.Duplicate,
                (await host.SubmitAsync(activity, cancellationToken)).Disposition);
            var observation = await observationActor.Ask<TeamsRouteObservation>(new ReadTeamsRouteObservation(), cancellationToken);
            Assert.Equal(expireDeadline && text.Length == 0 ? 1 : 2, observation.DispatchedTurns);
            Assert.Empty(observation.RouteDeadLetters);
        }
        finally
        {
            downloader.Release.TrySetResult();
            Sys.EventStream.Unsubscribe(observationActor);
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false, false, "image/*", false, false)]
    [InlineData(true, false, "image/png", false, false)]
    [InlineData(false, true, "image/png", false, false)]
    [InlineData(true, true, "image/*", false, false)]
    [InlineData(true, false, "image/png", true, false)]
    [InlineData(false, false, "image/png", false, true)]
    public async Task Image_batch_bounds_parallel_work_preserves_order_and_recovers_after_its_deadline(
        bool channel, bool expireBatch, string mime, bool sameName, bool personalFile)
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var clock = new FakeTimeProvider();
        var downloader = new IndexedGatedTeamsAttachmentDownloader();
        var replies = new RecordingTeamsReplyClient();
        var observer = Sys.ActorOf(Props.Create(() => new TeamsRouteObservationActor(TestActor)));
        var registry = ActorRegistry.For(Sys);
        registry.Register<SessionManagerActorKey>(observer);
        var pipeline = new SessionPipeline(
            Sys,
            new RequiredActor<SessionManagerActorKey>(registry),
            new TestSessionStorageResolver(paths));
        var dependencies = (channel
            ? CreatePublicVerifiedAttachmentDependencies(pipeline, paths, PngBytes, replies)
            : CreateVerifiedAttachmentDependencies(pipeline, paths, PngBytes, replies)) with
        {
            TimeProvider = clock,
            AttachmentDownloader = downloader
        };
        const string conversation = "image-batch;messageid=root-a";
        var actor = channel
            ? Sys.ActorOf(TeamsConversationActor.CreateProps(CreateSessionId("tenant-a", conversation), dependencies))
            : CreateBindingActor(CreateSessionId("tenant-a", "attachment-conversation"), dependencies, "teams-image-batch");
        var attachments = Enumerable.Range(0, 4)
            .Select(index => CreateInboundAttachment($"image-{(sameName ? 0 : index)}.png", personalFile ? TeamsInboundAttachmentKind.PersonalFile : TeamsInboundAttachmentKind.InlineImage, index, mime))
            .ToImmutableArray();
        var activity = MakeActivity("root-a", attachments);
        var route = Route(activity);
        var started = new HashSet<int>();
        for (var index = 0; index < 3; index++)
            started.Add(await ReadStarted());
        Assert.Equal([0, 1, 2], started.Order().ToArray());
        Assert.Equal(3, downloader.CallCount);

        if (expireBatch)
        {
            clock.Advance(TeamsIngressTimeouts.AttachmentBatch);
            downloader.ReleaseCancellation.SetResult();
        }
        else
        {
            downloader.Release(2);
            Assert.Equal(3, await ReadStarted());
            downloader.Release(3);
            downloader.Release(1);
            downloader.Release(0);
        }

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await route).Disposition);
        var dispatched = ReceiveDispatchedMessage();
        Assert.Equal(3, downloader.MaximumActive);
        Assert.Equal(expireBatch ? 3 : 4, downloader.CallCount);
        if (expireBatch)
        {
            Assert.Equal("inspect these images", dispatched.Content);
            Assert.Empty(dispatched.MediaReferences);
            Assert.Contains("Timed out processing", Assert.Single(replies.Messages).Text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(4, dispatched.MediaReferences.Count());
            var previous = -1;
            for (var index = 0; index < 4; index++)
            {
                if (!sameName)
                {
                    var position = dispatched.Content.IndexOf($"image-{index}.png", StringComparison.Ordinal);
                    Assert.True(position > previous, "The model input must retain attachment order.");
                    previous = position;
                }
                var mediaPath = Assert.Single(Directory.GetFiles(root.Path,
                    Path.GetFileName(dispatched.MediaReferences[index].RelativePath), SearchOption.AllDirectories));
                Assert.Equal(TestImages.Image(16 + index, 16),
                    await File.ReadAllBytesAsync(mediaPath, TestContext.Current.CancellationToken));
            }
            Assert.Empty(replies.Messages);
        }
        Assert.Empty(Directory.GetFiles(root.Path, ".teams-test-download-*.tmp", SearchOption.AllDirectories));
        Assert.Equal(TeamsBindingRouteDisposition.Duplicate, (await Route(activity)).Disposition);
        Assert.Equal(expireBatch ? 3 : 4, downloader.CallCount);

        downloader.Release(0);
        var later = MakeActivity("later-image", [attachments[0]]);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await Route(later)).Disposition);
        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        var next = ExpectMsg<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(next.MediaReferences);
        var observation = await observer.Ask<TeamsRouteObservation>(new ReadTeamsRouteObservation(), TestContext.Current.CancellationToken);
        Assert.Equal(2, observation.DispatchedTurns);
        Assert.Equal(expireBatch ? 4 : 5, downloader.CallCount);

        async Task<int> ReadStarted()
        {
            var read = downloader.Started.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
            Assert.Same(read, await Task.WhenAny(read, route));
            return await read;
        }

        TeamsInboundActivity MakeActivity(string id, ImmutableArray<TeamsAttachmentMetadata> files) => channel
            ? CreateChannelActivity(id, conversation, text: "inspect these images", attachments: files)
            : CreateAttachmentActivity(id, "inspect these images", files);
        Task<TeamsBindingRouteResult> Route(TeamsInboundActivity input) => channel
            ? RouteConversationAsync(actor, input)
            : RouteAsync(actor, input);
    }

    [Fact]
    public async Task Image_only_established_channel_thread_with_a_wildcard_inline_image_creates_one_model_turn()
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var dependencies = CreatePublicVerifiedAttachmentDependencies(CreatePipeline(TestActor), paths, PngBytes);
        const string conversationId = "public-wildcard-thread;messageid=root-a";
        var actor = Sys.ActorOf(TeamsConversationActor.CreateProps(
            CreateSessionId("tenant-a", conversationId), dependencies));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteConversationAsync(actor, CreateChannelActivity("root-a", conversationId))).Disposition);
        ReceiveDispatchedMessage();

        var continuation = CreateChannelActivity(
            "reply-image-only",
            conversationId,
            isMentioned: false,
            text: string.Empty,
            attachments: [CreateInboundAttachment("attachment-1", TeamsInboundAttachmentKind.InlineImage, 0, "image/*")]);
        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteConversationAsync(actor, continuation)).Disposition);

        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        var dispatched = ExpectMsg<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("image/png", Assert.Single(dispatched.MediaReferences).MimeType.Value);
    }

    [Fact]
    public async Task Mentioned_channel_text_and_live_wildcard_inline_image_with_rendering_companion_creates_one_model_turn()
    {
        using var root = new DisposableTempDir();
        var paths = new NetclawPaths(root.Path);
        paths.EnsureDirectoriesExist();
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            BotId = "bot",
            MentionOnly = true,
            AllowedTeamIds = ["team-a"],
            AllowedChannelIds = ["channel-a"],
            AllowedUserIds = ["user-a"],
            AllowAttachments = true
        };
        var translator = new TeamsSdkActivityTranslator(options, TimeProvider.System);
        var source = MessageActivity.FromActivity(CoreActivity.FromJsonString("""
            {
              "type": "message",
              "id": "root-a",
              "text": "<at>Netclaw</at> inspect this image",
              "from": { "id": "user-a" },
              "recipient": { "id": "28:bot" },
              "serviceUrl": "https://service.invalid/",
              "conversation": {
                "id": "conversation-inline-root;messageid=root-a",
                "tenantId": "tenant-a",
                "conversationType": "channel"
              },
              "channelData": {
                "team": { "id": "team-a" },
                "channel": { "id": "channel-a" }
              },
              "entities": [
                {
                  "type": "mention",
                  "mentioned": { "id": "28:bot" },
                  "text": "<at>Netclaw</at>"
                }
              ]
            }
            """));
        const string rawContentUrl = "https://smba.trafficmanager.net/amer/v3/attachments/live-inline-image";
        source.Attachments =
        [
            new Microsoft.Teams.Apps.Schema.TeamsAttachment
            {
                ContentType = new AttachmentContentType("image/*"),
                ContentUrl = new Uri(rawContentUrl)
            },
            new Microsoft.Teams.Apps.Schema.TeamsAttachment
            {
                ContentType = new AttachmentContentType("text/html"),
                Content = "<div><img src=\"https://rendering.invalid/inline\" /></div>"
            }
        ];
        var translated = translator.Translate(source, "tenant-a");
        Assert.Equal(TeamsTranslationDisposition.Accepted, translated.Disposition);
        var inlineImage = Assert.Single(translated.Activity!.Attachments);
        Assert.Equal("attachment-1", inlineImage.Name);
        Assert.Equal("image/*", inlineImage.ContentType);
        Assert.Equal(TeamsInboundAttachmentKind.InlineImage, inlineImage.Kind);
        Assert.DoesNotContain(rawContentUrl, JsonSerializer.Serialize(translated.Activity), StringComparison.Ordinal);
        Assert.DoesNotContain("Url", string.Join(',', translated.Activity!.Attachments
            .SelectMany(attachment => attachment.GetType().GetProperties())
            .Select(property => property.Name)), StringComparison.OrdinalIgnoreCase);
        var dependencies = CreatePublicVerifiedAttachmentDependencies(
            CreatePipeline(TestActor),
            paths,
            PngBytes,
            options: options);
        var parent = Sys.ActorOf(TeamsConversationActor.CreateProps(
            CreateSessionId("tenant-a", translated.Activity.Trust.ConversationId), dependencies));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteConversationAsync(parent, translated.Activity)).Disposition);
        var dispatched = ReceiveDispatchedMessage();
        Assert.Contains("inspect this image", dispatched.Content, StringComparison.Ordinal);
        Assert.Contains("[attachment]", dispatched.Content, StringComparison.Ordinal);
        Assert.Equal("image/png", Assert.Single(dispatched.MediaReferences).MimeType.Value);
    }

    [Fact]
    public async Task Concurrent_personal_ingress_resolves_one_conversation_and_binding()
    {
        var pipeline = CreatePipeline(TestActor);
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            AllowDirectMessages = true,
            AllowedUserIds = ["user-a"]
        };
        var services = new ServiceCollection();
        services.AddSingleton<ISessionPipeline>(pipeline);
        services.AddSingleton<ITeamsReplyClient, TestTeamsReplyClient>();
        services.AddSingleton<TeamsOutputRenderer>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IPromptInjectionDetector>(SafeTeamsPromptInjectionDetector.Instance);
        using var provider = services.BuildServiceProvider();
        var sink = new TeamsActorConversationIngressSink(Sys, options, provider);
        var activity = CreateActivity("activity-concurrent", "tenant-a", "conversation-concurrent");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => sink.RouteAsync(activity, TestContext.Current.CancellationToken).AsTask()));

        Assert.Equal(1, results.Count(result => result == TeamsIngressSinkResult.Accepted));
        Assert.Equal(7, results.Count(result => result == TeamsIngressSinkResult.Duplicate));
        ReceiveDispatchedMessage();

        var sessionId = CreateSessionId("tenant-a", "conversation-concurrent");
        var conversation = await Sys.ActorSelection($"/user/{TeamsActorNames.Conversation(sessionId)}")
            .ResolveOne(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var binding = await Sys.ActorSelection($"/user/{TeamsActorNames.Conversation(sessionId)}/{TeamsActorNames.Binding(sessionId)}")
            .ResolveOne(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.NotEqual(ActorRefs.Nobody, conversation);
        Assert.NotEqual(ActorRefs.Nobody, binding);
    }

    [Fact]
    public async Task A_pipeline_failure_releases_the_activity_reservation_for_a_retry()
    {
        var fallback = CreatePipeline(TestActor);
        var pipeline = new FailThenDelegatePipeline(fallback);
        var dependencies = CreateDependencies(pipeline);
        var sessionId = CreateSessionId("tenant-a", "conversation-failure");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies));
        var activity = CreateActivity("activity-failure", "tenant-a", "conversation-failure");

        Assert.Equal(TeamsBindingRouteDisposition.Failed, (await RouteAsync(actor, activity)).Disposition);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        ReceiveDispatchedMessage();
    }

    [Fact]
    public async Task A_pipeline_cancellation_releases_the_activity_reservation_for_a_retry()
    {
        var fallback = CreatePipeline(TestActor);
        using var cancellation = new CancellationTokenSource();
        var pipeline = new CancelThenDelegatePipeline(fallback, cancellation);
        var dependencies = CreateDependencies(pipeline);
        var sessionId = CreateSessionId("tenant-a", "conversation-cancelled");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies));
        var activity = CreateActivity("activity-cancelled", "tenant-a", "conversation-cancelled");

        var cancelled = await actor.Ask<TeamsBindingRouteResult>(
            new TeamsBindingIngress(activity, cancellation.Token),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsBindingRouteDisposition.Cancelled, cancelled.Disposition);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        ReceiveDispatchedMessage();
    }

    [Fact]
    public async Task Legacy_journal_only_recovers_to_a_teams_v2_snapshot_and_survives_restart()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-legacy-journal");
        var persistenceId = BindingPersistenceId(sessionId);
        var fingerprint = ActivityFingerprint.Create("legacy-journal-activity");
        await SeedJournalAsync(persistenceId,
            new DurableActivityDispatchReserved(fingerprint, null),
            DecodeLegacy("tapc-v1", CreateLegacyApproval("legacy-journal-correlation")),
            DecodeLegacy("tpdc-v1", CreateLegacyDestination("conversation-legacy-journal")),
            DecodeLegacy("tpdr-v1", new LegacyProto.TeamsProactiveDeliveryRecordedProto
            {
                DeliveryKey = "legacy-journal:1",
                State = (int)TeamsProactiveDeliveryState.Sent
            }));

        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        var actor = CreateBindingActor(sessionId, dependencies, "legacy-journal-first");
        var snapshot = await WaitForBindingSnapshotAsync(persistenceId);

        Assert.Equal(TeamsBindingSnapshot.CurrentMigrationVersion, snapshot.MigrationVersion);
        Assert.Contains(fingerprint, snapshot.ActivityFingerprints);
        Assert.Contains(snapshot.Approvals, approval => approval.CorrelationId == "legacy-journal-correlation");
        Assert.Equal("conversation-legacy-journal", snapshot.Destination?.ConversationId);
        Assert.Contains(snapshot.ProactiveDeliveries, delivery => delivery.DeliveryKey == "legacy-journal:1");
        Assert.Equal(TeamsMigrationHealthState.Completed, (await GetDiagnosticsAsync(actor)).Migration);

        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateBindingActor(sessionId, dependencies, "legacy-journal-recovered");
        Assert.Equal(
            TeamsBindingRouteDisposition.Duplicate,
            (await RouteAsync(recovered, CreateActivity(
                "legacy-journal-activity",
                "tenant-a",
                "conversation-legacy-journal"))).Disposition);
    }

    [Fact]
    public async Task Legacy_snapshot_only_recovers_all_binding_state_to_a_teams_v2_snapshot()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-legacy-snapshot");
        var persistenceId = BindingPersistenceId(sessionId);
        var fingerprint = ActivityFingerprint.Create("legacy-snapshot-activity");
        await SeedSnapshotAsync(persistenceId, 7, DecodeLegacy("dads-v1", CreateLegacyBindingSnapshot(
            fingerprint,
            "conversation-legacy-snapshot",
            "legacy-snapshot-correlation",
            "legacy-snapshot:1")));

        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        var first = CreateBindingActor(sessionId, dependencies, "legacy-snapshot-only");
        var snapshot = await WaitForBindingSnapshotAsync(persistenceId);

        Assert.Equal(TeamsBindingSnapshot.CurrentMigrationVersion, snapshot.MigrationVersion);
        Assert.Equal([fingerprint], snapshot.ActivityFingerprints);
        Assert.Single(snapshot.Approvals);
        Assert.Equal("legacy-snapshot-correlation", snapshot.Approvals[0].CorrelationId);
        Assert.Equal("conversation-legacy-snapshot", snapshot.Destination?.ConversationId);
        Assert.Single(snapshot.ProactiveDeliveries);
        Assert.Equal("legacy-snapshot:1", snapshot.ProactiveDeliveries[0].DeliveryKey);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "legacy-snapshot-only-recovered");
        Assert.Equal(TeamsMigrationHealthState.Completed, (await GetDiagnosticsAsync(recovered)).Migration);
        Assert.Equal([fingerprint], (await WaitForBindingSnapshotAsync(persistenceId)).ActivityFingerprints);
    }

    [Fact]
    public async Task Legacy_snapshot_and_events_recover_in_sequence_before_the_v2_migration_snapshot()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-legacy-sequence");
        var persistenceId = BindingPersistenceId(sessionId);
        var fingerprint = ActivityFingerprint.Create("legacy-sequence-activity");
        await SeedSnapshotAsync(persistenceId, 1, DecodeLegacy("dads-v1", CreateLegacyBindingSnapshot(
            fingerprint,
            "conversation-legacy-sequence",
            "legacy-sequence-correlation",
            "legacy-sequence:1")));
        await SeedJournalAsync(persistenceId, 2,
            DecodeLegacy("tpdc-v1", CreateLegacyDestination("conversation-legacy-sequence", "https://service-refreshed.invalid/")),
            DecodeLegacy("tacd-v1", new LegacyProto.TeamsApprovalCardDeliveredProto
            {
                CorrelationId = "legacy-sequence-correlation",
                PromptId = "legacy-sequence-prompt"
            }));

        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        var first = CreateBindingActor(sessionId, dependencies, "legacy-snapshot-events");
        var snapshot = await WaitForBindingSnapshotAsync(persistenceId);

        Assert.Equal("https://service-refreshed.invalid/", snapshot.Destination?.ServiceUrl);
        Assert.Equal(2, snapshot.Destination?.Generation);
        Assert.Equal("legacy-sequence-prompt", snapshot.Approvals.Single().PromptId);
        Assert.Contains(snapshot.ActivityFingerprints, value => value == fingerprint);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        CreateBindingActor(sessionId, dependencies, "legacy-snapshot-events-recovered");
        var recovered = await WaitForBindingSnapshotAsync(persistenceId);
        Assert.Equal(2, recovered.Destination?.Generation);
        Assert.Equal("legacy-sequence-prompt", recovered.Approvals.Single().PromptId);
    }

    [Fact]
    public async Task Legacy_and_v2_records_can_coexist_before_compaction_to_a_v2_snapshot()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-legacy-v2");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedJournalAsync(persistenceId,
            DecodeLegacy("tpdc-v1", CreateLegacyDestination("conversation-legacy-v2")),
            new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "legacy-v2:1",
                State = (int)TeamsProactiveDeliveryState.FailedRetryable,
                DestinationGeneration = 1
            });

        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        var first = CreateBindingActor(sessionId, dependencies, "legacy-v2-mixed");
        var snapshot = await WaitForBindingSnapshotAsync(persistenceId);

        Assert.IsType<TeamsBindingSnapshot>(snapshot);
        Assert.Contains(snapshot.ProactiveDeliveries, delivery =>
            delivery.DeliveryKey == "legacy-v2:1"
            && delivery.State == (int)TeamsProactiveDeliveryState.FailedRetryable
            && delivery.DestinationGeneration == 1);
        Assert.Equal(151, Sys.Serialization.FindSerializerFor(snapshot).Identifier);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        CreateBindingActor(sessionId, dependencies, "legacy-v2-mixed-recovered");
        Assert.Contains((await WaitForBindingSnapshotAsync(persistenceId)).ProactiveDeliveries, delivery =>
            delivery.DeliveryKey == "legacy-v2:1" && delivery.DestinationGeneration == 1);
    }

    [Fact]
    public async Task Legacy_channel_index_recovers_to_a_teams_v2_snapshot()
    {
        const string conversationId = "conversation-legacy-channel;messageid=root-a";
        var conversationActorId = CreateSessionId("tenant-a", conversationId);
        var sessionId = CreateChannelSessionId(conversationId);
        var persistenceId = "teams-channel-conversation-" + Uri.EscapeDataString(conversationActorId.Value);
        var fingerprint = ActivityFingerprint.Create("root-a");
        await SeedJournalAsync(persistenceId, DecodeLegacy("dtcam-v1", new LegacyProto.DurableTeamsChannelActivityMappedProto
        {
            ActivityFingerprint = fingerprint,
            SessionId = sessionId.Value
        }));

        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            MentionOnly = true,
            AllowedTeamIds = ["team-a"],
            AllowedChannelIds = ["channel-a"]
        };
        var dependencies = new TeamsConversationDependencies(
            options,
            CreatePipeline(TestActor),
            new TestTeamsReplyClient(),
            new TeamsOutputRenderer(),
            TimeProvider.System)
        {
            PromptInjectionDetector = SafeTeamsPromptInjectionDetector.Instance
        };
        var first = Sys.ActorOf(TeamsConversationActor.CreateProps(conversationActorId, dependencies), "legacy-channel-index");
        var snapshot = await WaitForSnapshotAsync<TeamsChannelActivityIndexSnapshot>(persistenceId);

        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(fingerprint, entry.ActivityFingerprint);
        Assert.Equal(sessionId.Value, entry.SessionId);
        Assert.Equal(151, Sys.Serialization.FindSerializerFor(snapshot).Identifier);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = Sys.ActorOf(TeamsConversationActor.CreateProps(conversationActorId, dependencies), "legacy-channel-index-recovered");
        Assert.Equal(fingerprint, Assert.Single((await WaitForSnapshotAsync<TeamsChannelActivityIndexSnapshot>(persistenceId)).Entries).ActivityFingerprint);
        Assert.Equal(
            TeamsBindingRouteDisposition.Ignored,
            (await RouteConversationAsync(recovered, CreateChannelActivity(
                "legacy-unmentioned-reply",
                conversationId,
                isMentioned: false))).Disposition);
    }

    [Fact]
    public async Task Failed_migration_snapshot_is_retained_and_a_restart_retries_successfully()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-migration-retry");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedJournalAsync(persistenceId, DecodeLegacy("tpdc-v1", CreateLegacyDestination("conversation-migration-retry")));
        await Snapshots.OnSave.Fail();

        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        var failed = CreateBindingActor(sessionId, dependencies, "migration-retry-failed");
        Assert.Equal(TeamsMigrationHealthState.Failed,
            (await WaitForMigrationStateAsync(failed, TeamsMigrationHealthState.Failed)).Migration);

        await Snapshots.OnSave.Pass();
        Watch(failed);
        failed.Tell(PoisonPill.Instance);
        ExpectTerminated(failed, cancellationToken: TestContext.Current.CancellationToken);

        var retried = CreateBindingActor(sessionId, dependencies, "migration-retry-success");
        var snapshot = await WaitForBindingSnapshotAsync(persistenceId);
        Assert.Equal("conversation-migration-retry", snapshot.Destination?.ConversationId);
        Assert.Equal(TeamsMigrationHealthState.Completed,
            (await WaitForMigrationStateAsync(retried, TeamsMigrationHealthState.Completed)).Migration);
    }

    [Fact]
    public async Task Repeated_restart_failures_do_not_clear_required_legacy_migration()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-migration-restarts");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedJournalAsync(persistenceId, DecodeLegacy("tpdc-v1", CreateLegacyDestination("conversation-migration-restarts")));
        await Snapshots.OnSave.Fail();

        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var actor = CreateBindingActor(sessionId, dependencies, $"migration-restart-{attempt}");
            Assert.Equal(TeamsMigrationHealthState.Failed,
                (await WaitForMigrationStateAsync(actor, TeamsMigrationHealthState.Failed)).Migration);
            Watch(actor);
            actor.Tell(PoisonPill.Instance);
            ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
        }

        await Snapshots.OnSave.Pass();
        CreateBindingActor(sessionId, dependencies, "migration-restart-success");
        Assert.IsType<TeamsBindingSnapshot>(await WaitForBindingSnapshotAsync(persistenceId));
    }

    [Fact]
    public void Malformed_or_insufficient_legacy_payloads_are_decode_only_and_never_writeable()
    {
        var serializer = Assert.IsType<NetclawProtobufSerializer>(
            Sys.Serialization.FindSerializerFor(new SessionId("serializer-proof")));
        var malformedDestination = serializer.FromBinary(
            new LegacyProto.TeamsProactiveDestinationCapturedProto().ToByteArray(),
            "tpdc-v1");
        var insufficientSnapshot = serializer.FromBinary(
            new LegacyProto.DurableActivityDispatchSnapshotProto
            {
                TeamsDestination = new LegacyProto.TeamsProactiveDestinationSnapshotEntryProto()
            }.ToByteArray(),
            "dads-v1");

        Assert.IsType<LegacyChannelPersistenceEnvelope>(malformedDestination);
        Assert.IsType<LegacyChannelPersistenceEnvelope>(insufficientSnapshot);
        Assert.Throws<ArgumentException>(() => serializer.Manifest(malformedDestination));
        Assert.Throws<ArgumentException>(() => serializer.Manifest(insufficientSnapshot));
    }

    [Fact]
    public async Task Insufficient_legacy_destination_fails_closed_during_binding_recovery()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-malformed-legacy");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedJournalAsync(persistenceId, DecodeLegacy("tpdc-v1", new LegacyProto.TeamsProactiveDestinationCapturedProto()));

        var observer = CreateTestProbe();
        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        Sys.ActorOf(
            Props.Create(() => new StopOnRecoveryFailureParent(
                TeamsSessionBindingActor.CreateProps(sessionId, dependencies),
                observer.Ref)),
            "malformed-legacy-parent");
        var binding = await observer.ExpectMsgAsync<IActorRef>(cancellationToken: TestContext.Current.CancellationToken);
        Watch(binding);
        ExpectTerminated(binding, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await LoadLatestSnapshotAsync(persistenceId));
    }

    [Fact]
    public void New_teams_persistence_writes_use_only_the_teams_owned_v2_serializer()
    {
        var values = new ITeamsPersistenceMessage[]
        {
            new TeamsApprovalPendingCreated
            {
                CallId = "call", CorrelationId = "correlation", NonceHash = new string('a', 64),
                ExpiresAtUnixMilliseconds = 4_102_444_800_000
            },
            new TeamsApprovalCardDelivered { CorrelationId = "correlation", PromptId = "prompt" },
            new TeamsApprovalCardReissued
            {
                CorrelationId = "correlation", NonceHash = new string('b', 64), ExpiresAtUnixMilliseconds = 4_102_444_800_000
            },
            new TeamsApprovalConsumed { CorrelationId = "correlation", Decision = "approve", ConsumedAtUnixMilliseconds = 1 },
            new TeamsProactiveDestinationCaptured
            {
                TenantId = "tenant-a", ConversationId = "conversation-v2", Scope = (int)TeamsConversationScope.Personal,
                ServiceUrl = "https://service.invalid/", UserId = "user-a", Generation = 1
            },
            new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "delivery:1", State = (int)TeamsProactiveDeliveryState.Sent, DestinationGeneration = 1
            },
            new TeamsBindingSnapshot([]),
            new TeamsChannelActivityMapped(ActivityFingerprint.Create("v2-channel"), "channel-session", null),
            new TeamsChannelActivityIndexSnapshot([])
        };

        foreach (var value in values)
        {
            var serializer = Assert.IsAssignableFrom<SerializerWithStringManifest>(Sys.Serialization.FindSerializerFor(value));
            Assert.Equal(151, serializer.Identifier);
            Assert.DoesNotContain("-v1", serializer.Manifest(value), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Destination_generations_start_at_one_ignore_identical_refreshes_and_bind_new_deliveries()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-generation");
        var persistenceId = BindingPersistenceId(sessionId);
        var dependencies = CreateDependencies(CreatePipeline(TestActor), enabled: true);
        var actor = CreateBindingActor(sessionId, dependencies, "generation-policy");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("generation-first", "tenant-a", "conversation-generation", "https://generation-first.invalid/"))).Disposition);
        ReceiveOutputSubscriber();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "generation:1")));
        ReceiveNextOutputSubscriber();

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("generation-identical", "tenant-a", "conversation-generation", "https://generation-first.invalid/"))).Disposition);
        ReceiveNextOutputSubscriber();
        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("generation-refresh", "tenant-a", "conversation-generation", "https://generation-second.invalid/"))).Disposition);
        ReceiveNextOutputSubscriber();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "generation:2")));
        ReceiveNextOutputSubscriber();

        var events = await ReadJournalAsync(persistenceId);
        var captures = events.OfType<TeamsProactiveDestinationCaptured>().ToArray();
        Assert.Equal(2, captures.Length);
        Assert.Equal([1, 2], captures.Select(capture => capture.Generation));
        var reservations = events.OfType<TeamsProactiveDeliveryRecorded>()
            .Where(recorded => recorded.State == (int)TeamsProactiveDeliveryState.Pending)
            .ToDictionary(recorded => recorded.DeliveryKey, StringComparer.Ordinal);
        Assert.Equal(1, reservations["generation:1"].DestinationGeneration);
        Assert.Equal(2, reservations["generation:2"].DestinationGeneration);

        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "generation-policy-recovered");
        Assert.Equal(TeamsProactiveHealthState.Available, (await GetDiagnosticsAsync(recovered)).Health);
    }

    [Fact]
    public async Task Generation_overflow_fails_closed_without_wraparound()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-generation-overflow");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedSnapshotAsync(persistenceId, 1, CreateBindingSnapshot(
            "conversation-generation-overflow",
            "https://generation-max.invalid/",
            long.MaxValue));

        var observer = CreateTestProbe();
        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        Sys.ActorOf(Props.Create(() => new StopOnRecoveryFailureParent(
            TeamsSessionBindingActor.CreateProps(sessionId, dependencies), observer.Ref)), "generation-overflow-parent");
        var binding = await observer.ExpectMsgAsync<IActorRef>(cancellationToken: TestContext.Current.CancellationToken);
        Watch(binding);
        binding.Tell(new TeamsBindingIngress(
            CreateActivity("generation-overflow-refresh", "tenant-a", "conversation-generation-overflow", "https://generation-overflow.invalid/"),
            TestContext.Current.CancellationToken));
        ExpectTerminated(binding, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stale_completion_updates_only_its_old_delivery_and_never_posts_or_invalidates_the_refreshed_generation()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-stale-matrix");
        var persistenceId = BindingPersistenceId(sessionId);
        var dependencies = CreateDependencies(CreatePipeline(TestActor), replyClient: replyClient, enabled: true);
        var actor = CreateBindingActor(sessionId, dependencies, "stale-matrix");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("stale-first", "tenant-a", "conversation-stale-matrix", "https://stale-first.invalid/"))).Disposition);
        ReceiveOutputSubscriber();
        var observer = CreateTestProbe();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "stale-matrix:1", observer.Ref)));
        ReceiveNextOutputSubscriber();
        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("stale-refresh", "tenant-a", "conversation-stale-matrix", "https://stale-second.invalid/"))).Disposition);
        ReceiveNextOutputSubscriber();

        actor.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("late success")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId("stale-matrix:1")
        }));
        Assert.False((await observer.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken)).Delivered);
        Assert.Empty(replyClient.Messages);

        var events = await ReadJournalAsync(persistenceId);
        var stale = events.OfType<TeamsProactiveDeliveryRecorded>().Last(recorded => recorded.DeliveryKey == "stale-matrix:1");
        Assert.Equal((int)TeamsProactiveDeliveryState.FailedRetryable, stale.State);
        Assert.Equal(1, stale.DestinationGeneration);
        Assert.False(stale.InvalidatesDestination);
        Assert.Equal(2, events.OfType<TeamsProactiveDestinationCaptured>().Last().Generation);

        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "stale-matrix-recovered");
        var diagnostics = await GetDiagnosticsAsync(recovered);
        Assert.Equal(TeamsProactiveHealthState.Available, diagnostics.Health);
        Assert.Equal(1, diagnostics.RetryableFailureCount);
        Assert.Equal(0, diagnostics.PermanentFailureCount);
    }

    [Fact]
    public async Task Atomic_permanent_failure_invalidates_only_the_matching_generation_and_survives_restart()
    {
        var replyClient = new RecordingTeamsReplyClient(new TeamsDeliveryResult(TeamsDeliveryStatus.InvalidDestination));
        var sessionId = CreateSessionId("tenant-a", "conversation-atomic-permanent");
        var persistenceId = BindingPersistenceId(sessionId);
        var dependencies = CreateDependencies(CreatePipeline(TestActor), replyClient: replyClient, enabled: true);
        var actor = CreateBindingActor(sessionId, dependencies, "atomic-permanent");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("atomic-first", "tenant-a", "conversation-atomic-permanent"))).Disposition);
        ReceiveOutputSubscriber();
        var observer = CreateTestProbe();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "atomic-permanent:1", observer.Ref)));
        ReceiveNextOutputSubscriber();
        actor.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("permanent failure")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId("atomic-permanent:1")
        }));
        Assert.False((await observer.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken)).Delivered);

        var events = await ReadJournalAsync(persistenceId);
        var terminal = events.OfType<TeamsProactiveDeliveryRecorded>().Last(recorded => recorded.DeliveryKey == "atomic-permanent:1");
        Assert.Equal((int)TeamsProactiveDeliveryState.FailedPermanent, terminal.State);
        Assert.Equal(1, terminal.DestinationGeneration);
        Assert.True(terminal.InvalidatesDestination);
        Assert.Single(events.OfType<TeamsProactiveDeliveryRecorded>(), recorded =>
            recorded.DeliveryKey == "atomic-permanent:1" && recorded.InvalidatesDestination);

        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "atomic-permanent-recovered");
        var diagnostics = await GetDiagnosticsAsync(recovered);
        Assert.Equal(TeamsProactiveHealthState.Unavailable, diagnostics.Health);
        Assert.Equal(1, diagnostics.PermanentFailureCount);
        Assert.Equal(1, diagnostics.TerminalDeliveryCount);
        Assert.Equal(1, diagnostics.InvalidatedDestinationCount);
        Assert.Equal(1, diagnostics.MissingTargetCount);

        recovered.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("duplicate completion")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId("atomic-permanent:1")
        }));
        var afterDuplicate = await ReadJournalAsync(persistenceId);
        Assert.Single(afterDuplicate.OfType<TeamsProactiveDeliveryRecorded>(), recorded =>
            recorded.DeliveryKey == "atomic-permanent:1" && recorded.InvalidatesDestination);
    }

    [Fact]
    public async Task Concurrent_reminder_outputs_complete_by_delivery_key_and_late_completions_are_ignored()
    {
        var replyClient = new RecordingTeamsReplyClient(
            new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered),
            new TeamsDeliveryResult(TeamsDeliveryStatus.Cancelled));
        var sessionId = CreateSessionId("tenant-a", "conversation-concurrent-reminders");
        var persistenceId = BindingPersistenceId(sessionId);
        var actor = CreateBindingActor(sessionId, CreateDependencies(CreatePipeline(TestActor), replyClient: replyClient), "concurrent-reminders");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("concurrent-first", "tenant-a", "conversation-concurrent-reminders"))).Disposition);
        ReceiveOutputSubscriber();
        var observerA = CreateTestProbe();
        var observerB = CreateTestProbe();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "concurrent:a", observerA.Ref)));
        ReceiveNextOutputSubscriber();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "concurrent:b", observerB.Ref)));
        ReceiveNextOutputSubscriber();

        actor.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("B")
        {
            SessionId = sessionId, SourceReminderId = new ReminderId("concurrent:b")
        }));
        actor.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("A")
        {
            SessionId = sessionId, SourceReminderId = new ReminderId("concurrent:a")
        }));
        Assert.True((await observerB.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken)).Delivered);
        Assert.False((await observerA.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken)).Delivered);
        Assert.Equal(2, replyClient.Messages.Count);

        actor.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("late A")
        {
            SessionId = sessionId, SourceReminderId = new ReminderId("concurrent:a")
        }));
        await AwaitAssertAsync(() => Assert.Equal(2, replyClient.Messages.Count), cancellationToken: TestContext.Current.CancellationToken);
        var terminal = (await ReadJournalAsync(persistenceId)).OfType<TeamsProactiveDeliveryRecorded>()
            .Where(recorded => recorded.State is (int)TeamsProactiveDeliveryState.Sent or (int)TeamsProactiveDeliveryState.FailedRetryable)
            .GroupBy(recorded => recorded.DeliveryKey)
            .ToDictionary(group => group.Key, group => group.Last().State, StringComparer.Ordinal);
        Assert.Equal((int)TeamsProactiveDeliveryState.Sent, terminal["concurrent:b"]);
        Assert.Equal((int)TeamsProactiveDeliveryState.FailedRetryable, terminal["concurrent:a"]);
    }

    [Fact]
    public async Task Delivery_capacity_preserves_terminal_idempotency_and_invalid_snapshots_fail_closed()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-delivery-capacity");
        var persistenceId = BindingPersistenceId(sessionId);
        var retained = Enumerable.Range(0, TeamsSessionBindingActor.ProactiveDeliveryCapacity)
            .Select(index => new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = $"retained:{index}",
                State = (int)TeamsProactiveDeliveryState.Sent,
                DestinationGeneration = 1
            }).ToArray();
        await SeedSnapshotAsync(persistenceId, 1, CreateBindingSnapshot(
            "conversation-delivery-capacity",
            "https://capacity.invalid/",
            1,
            retained));

        var dependencies = CreateDependencies(CreatePipeline(TestActor), enabled: true);
        var actor = CreateBindingActor(sessionId, dependencies, "delivery-capacity");
        Assert.True((await GetDiagnosticsAsync(actor)).HasCapacityPressure);
        var existingObserver = CreateTestProbe();
        var existingDispatcher = CreateTestProbe();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "retained:0", existingObserver.Ref)), existingDispatcher.Ref);
        await existingDispatcher.ExpectMsgAsync<CommandAck>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True((await existingObserver.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken)).Delivered);
        var fullDispatcher = CreateTestProbe();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "new-at-capacity")), fullDispatcher.Ref);
        var nack = await fullDispatcher.ExpectMsgAsync<CommandNack>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("capacity", nack.Reason, StringComparison.OrdinalIgnoreCase);

        var invalidPersistenceId = BindingPersistenceId(CreateSessionId("tenant-a", "conversation-delivery-overcapacity"));
        await SeedSnapshotAsync(invalidPersistenceId, 1, CreateBindingSnapshot(
            "conversation-delivery-overcapacity",
            "https://overcapacity.invalid/",
            1,
            retained.Append(new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "overflow", State = (int)TeamsProactiveDeliveryState.Sent, DestinationGeneration = 1
            }).ToArray()));
        var stopObserver = CreateTestProbe();
        var invalidSession = CreateSessionId("tenant-a", "conversation-delivery-overcapacity");
        Sys.ActorOf(Props.Create(() => new StopOnRecoveryFailureParent(
            TeamsSessionBindingActor.CreateProps(invalidSession, dependencies),
            stopObserver.Ref)), "delivery-overcapacity-parent");
        var invalidBinding = await stopObserver.ExpectMsgAsync<IActorRef>(cancellationToken: TestContext.Current.CancellationToken);
        Watch(invalidBinding);
        ExpectTerminated(invalidBinding, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Interrupted_dispatch_has_no_partial_permanent_invalidation_after_restart()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-before-atomic");
        var dependencies = CreateDependencies(CreatePipeline(TestActor), enabled: true);
        var first = CreateBindingActor(sessionId, dependencies, "before-atomic-first");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity("before-atomic-activity", "tenant-a", "conversation-before-atomic"))).Disposition);
        ReceiveOutputSubscriber();
        first.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "before-atomic:1")));
        ReceiveNextOutputSubscriber();

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateBindingActor(sessionId, dependencies, "before-atomic-recovered");
        TeamsBindingProactiveDiagnostics? diagnostics = null;
        await AwaitAssertAsync(() =>
        {
            diagnostics = GetDiagnosticsAsync(recovered).GetAwaiter().GetResult();
            Assert.Equal(1, diagnostics.UnknownDeliveryCount);
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(diagnostics);
        Assert.Equal(TeamsProactiveHealthState.Available, diagnostics.Health);
        Assert.Equal(0, diagnostics.PermanentFailureCount);
        Assert.Equal(1, diagnostics.UnknownDeliveryCount);
    }

    [Fact]
    public async Task Journal_only_stale_permanent_failure_is_terminal_without_invalidating_newer_destination()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-journal-stale-permanent");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedJournalAsync(persistenceId,
            CreateDestination("conversation-journal-stale-permanent", "https://journal-first.invalid/", 1),
            new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "journal-stale:1",
                State = (int)TeamsProactiveDeliveryState.Sending,
                DestinationGeneration = 1
            },
            CreateDestination("conversation-journal-stale-permanent", "https://journal-second.invalid/", 2),
            new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "journal-stale:1",
                State = (int)TeamsProactiveDeliveryState.FailedPermanent,
                DestinationGeneration = 1,
                InvalidatesDestination = true
            });

        var dependencies = CreateDependencies(CreatePipeline(TestActor), enabled: true);
        var first = CreateBindingActor(sessionId, dependencies, "journal-stale-permanent-first");
        var diagnostics = await GetDiagnosticsAsync(first);
        Assert.Equal(TeamsProactiveHealthState.Available, diagnostics.Health);
        Assert.Equal(1, diagnostics.PermanentFailureCount);
        Assert.Equal(1, diagnostics.TerminalDeliveryCount);
        Assert.Equal(0, diagnostics.InvalidatedDestinationCount);
        Assert.Equal(0, diagnostics.MissingTargetCount);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "journal-stale-permanent-recovered");
        diagnostics = await GetDiagnosticsAsync(recovered);
        Assert.Equal(TeamsProactiveHealthState.Available, diagnostics.Health);
        Assert.Equal(1, diagnostics.PermanentFailureCount);
    }

    [Fact]
    public async Task Snapshot_recovery_preserves_atomic_permanent_invalidation_without_reapplying_it()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-snapshot-permanent");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedSnapshotAsync(persistenceId, 4, new TeamsBindingSnapshot([])
        {
            LastDestinationGeneration = 1,
            ProactiveDeliveries = [new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "snapshot-permanent:1",
                State = (int)TeamsProactiveDeliveryState.FailedPermanent,
                DestinationGeneration = 1,
                InvalidatesDestination = true
            }]
        });

        var dependencies = CreateDependencies(CreatePipeline(TestActor), enabled: true);
        var first = CreateBindingActor(sessionId, dependencies, "snapshot-permanent-first");
        var diagnostics = await GetDiagnosticsAsync(first);
        Assert.Equal(TeamsProactiveHealthState.Unavailable, diagnostics.Health);
        Assert.Equal(1, diagnostics.PermanentFailureCount);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "snapshot-permanent-recovered");
        diagnostics = await GetDiagnosticsAsync(recovered);
        Assert.Equal(TeamsProactiveHealthState.Unavailable, diagnostics.Health);
        Assert.Equal(1, diagnostics.PermanentFailureCount);
        Assert.Empty(await ReadJournalAsync(persistenceId));
    }

    [Fact]
    public async Task Invalidated_snapshot_advances_before_a_new_destination_is_reserved()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-snapshot-recapture");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedSnapshotAsync(persistenceId, 4, new TeamsBindingSnapshot([])
        {
            LastDestinationGeneration = 1,
            ProactiveDeliveries = [new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "snapshot-recapture:1",
                State = (int)TeamsProactiveDeliveryState.FailedPermanent,
                DestinationGeneration = 1,
                InvalidatesDestination = true
            }]
        });

        var actor = CreateBindingActor(
            sessionId,
            CreateDependencies(CreatePipeline(TestActor), enabled: true),
            "snapshot-recapture");
        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "snapshot-recapture-activity",
                "tenant-a",
                "conversation-snapshot-recapture",
                "https://recaptured.invalid/"))).Disposition);
        ReceiveOutputSubscriber();

        var capture = Assert.Single((await ReadJournalAsync(persistenceId)).OfType<TeamsProactiveDestinationCaptured>());
        Assert.Equal(2, capture.Generation);
    }

    [Fact]
    public async Task Compaction_snapshot_retains_generation_and_terminal_idempotency_state()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-compaction-state");
        var persistenceId = BindingPersistenceId(sessionId);
        var replyClient = new RecordingTeamsReplyClient();
        var dependencies = CreateDependencies(
            CreatePipeline(Sys.ActorOf(DiscardActor.Create())),
            enabled: true,
            replyClient: replyClient);
        var first = CreateBindingActor(sessionId, dependencies, "compaction-state-first");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity(
                "compaction-0",
                "tenant-a",
                "conversation-compaction-state"))).Disposition);
        first.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "compaction-delivery:1")));
        await AwaitAssertAsync(() => Assert.Contains(
                ReadJournalAsync(persistenceId).GetAwaiter().GetResult().OfType<TeamsProactiveDeliveryRecorded>(),
                recorded => recorded.DeliveryKey == "compaction-delivery:1"
                    && recorded.State == (int)TeamsProactiveDeliveryState.Sending),
            cancellationToken: TestContext.Current.CancellationToken);
        first.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("compaction delivery")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId("compaction-delivery:1")
        }));
        await AwaitAssertAsync(() => Assert.Contains(
                ReadJournalAsync(persistenceId).GetAwaiter().GetResult().OfType<TeamsProactiveDeliveryRecorded>(),
                recorded => recorded.DeliveryKey == "compaction-delivery:1"
                    && recorded.State == (int)TeamsProactiveDeliveryState.Sent),
            cancellationToken: TestContext.Current.CancellationToken);

        for (var index = 1; index < 60; index++)
        {
            Assert.Equal(TeamsBindingRouteDisposition.Accepted,
                (await RouteAsync(first, CreateActivity(
                    $"compaction-{index}",
                    "tenant-a",
                    "conversation-compaction-state"))).Disposition);
        }

        var snapshot = await WaitForBindingSnapshotAsync(persistenceId);
        Assert.Equal(TeamsBindingSnapshot.CurrentMigrationVersion, snapshot.MigrationVersion);
        Assert.Equal(60, snapshot.ActivityFingerprints.Count);
        Assert.Equal(1, snapshot.Destination?.Generation);
        Assert.Equal("conversation-compaction-state", snapshot.Destination?.ConversationId);
        Assert.Contains(snapshot.ProactiveDeliveries, recorded =>
            recorded.DeliveryKey == "compaction-delivery:1"
                && recorded.State == (int)TeamsProactiveDeliveryState.Sent);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "compaction-state-recovered");
        Assert.Equal(TeamsBindingRouteDisposition.Duplicate,
            (await RouteAsync(recovered, CreateActivity(
                "compaction-59",
                "tenant-a",
                "conversation-compaction-state"))).Disposition);
        Assert.Equal(TeamsProactiveHealthState.Available, (await GetDiagnosticsAsync(recovered)).Health);
        var dispatcher = CreateTestProbe();
        var observer = CreateTestProbe();
        recovered.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "compaction-delivery:1", observer.Ref)), dispatcher.Ref);
        await dispatcher.ExpectMsgAsync<CommandAck>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True((await observer.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken)).Delivered);
        Assert.Single(replyClient.Messages);
    }

    [Fact]
    public async Task Snapshot_with_a_future_delivery_generation_fails_closed()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-future-delivery-generation");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedSnapshotAsync(persistenceId, 1, CreateBindingSnapshot(
            "conversation-future-delivery-generation",
            "https://future-generation.invalid/",
            1,
            [new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "future-generation:1",
                State = (int)TeamsProactiveDeliveryState.Sent,
                DestinationGeneration = 2
            }]));

        var observer = CreateTestProbe();
        Sys.ActorOf(Props.Create(() => new StopOnRecoveryFailureParent(
            TeamsSessionBindingActor.CreateProps(sessionId, CreateDependencies(CreatePipeline(TestActor))), observer.Ref)),
            "future-generation-parent");
        var binding = await observer.ExpectMsgAsync<IActorRef>(cancellationToken: TestContext.Current.CancellationToken);
        Watch(binding);
        ExpectTerminated(binding, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Personal_output_posts_one_reply_to_the_verified_personal_destination()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-output");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-output", "tenant-a", "conversation-output"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        var telemetryBefore = ChannelTelemetry.For(ChannelType.Teams).GetSnapshot();
        subscriber.Tell(new TextOutput("final reply") { SessionId = sessionId });

        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var message = Assert.Single(replyClient.Messages);
        Assert.Equal(TeamsConversationScope.Personal, message.Destination.Scope);
        Assert.Equal("tenant-a", message.Destination.TenantId);
        Assert.Equal("conversation-output", message.Destination.ConversationId);
        Assert.Equal("user-a", message.Destination.UserId);
        Assert.Equal("final reply", message.Text);
        Assert.Equal(telemetryBefore.RepliesPosted + 1, ChannelTelemetry.For(ChannelType.Teams).GetSnapshot().RepliesPosted);
    }

    [Fact]
    public async Task Allowed_personal_destination_survives_binding_restart_for_later_output()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-proactive-restart");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-proactive-first");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity(
                "activity-proactive",
                "tenant-a",
                "conversation-proactive-restart"))).Disposition);
        ReceiveDispatchedMessage();
        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-proactive-second");
        recovered.Tell(new TeamsSessionBindingActor.BindingOutput(
            new TextOutput("later reminder output") { SessionId = sessionId }));

        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("conversation-proactive-restart", Assert.Single(replyClient.Messages).Destination.ConversationId);
        Assert.Equal(TeamsConversationScope.Personal, Assert.Single(replyClient.Messages).Destination.Scope);
    }

    [Fact]
    public async Task Rejected_personal_activity_cannot_create_a_destination()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-poisoning");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient, allowedUserIds: ["different-user"])));

        Assert.Equal(
            TeamsBindingRouteDisposition.Denied,
            (await RouteAsync(actor, CreateActivity("activity-poisoning", "tenant-a", "conversation-poisoning"))).Disposition);

        var telemetryBefore = ChannelTelemetry.For(ChannelType.Teams).GetSnapshot();
        actor.Tell(new TeamsSessionBindingActor.BindingOutput(
            new TextOutput("must not deliver") { SessionId = sessionId }));
        await AwaitAssertAsync(
            () => Assert.Equal(telemetryBefore.RepliesFailed + 1, ChannelTelemetry.For(ChannelType.Teams).GetSnapshot().RepliesFailed),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(replyClient.Messages);
    }

    [Fact]
    public async Task Reminder_without_a_persisted_destination_is_rejected_without_a_delivery_attempt()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-reminder-missing");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(CreatePipeline(TestActor), replyClient: replyClient)));
        var dispatcher = CreateTestProbe();

        actor.Tell(
            new TeamsBindingReminder(CreateReminder(sessionId, "reminder-missing:1")),
            dispatcher.Ref);

        var nack = await dispatcher.ExpectMsgAsync<CommandNack>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("destination", nack.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(replyClient.Messages);
    }

    [Fact]
    public async Task Proactive_diagnostics_are_actor_owned_and_do_not_disclose_destination_values()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-proactive-diagnostics");
        var dependencies = CreateDependencies(CreatePipeline(TestActor), enabled: true);
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies));

        var missing = await actor.Ask<TeamsBindingProactiveDiagnostics>(
            GetTeamsBindingProactiveDiagnostics.Instance,
            TestContext.Current.CancellationToken);
        Assert.Equal(TeamsProactiveHealthState.Unavailable, missing.Health);
        Assert.Equal("proactive_destination_missing", missing.ReasonCode);
        Assert.Equal(0, missing.PersonalDestinationCount);
        Assert.Equal(0, missing.ChannelDestinationCount);
        Assert.Equal(1, missing.MissingTargetCount);
        Assert.Equal(0, missing.InvalidatedDestinationCount);
        Assert.Equal(0, missing.TerminalDeliveryCount);
        Assert.Equal(0, missing.AmbiguousTargetCount);
        Assert.False(missing.HasInvalidRecoveredState);

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "activity-proactive-diagnostics",
                "tenant-a",
                "conversation-proactive-diagnostics"))).Disposition);
        ReceiveOutputSubscriber();

        var available = await actor.Ask<TeamsBindingProactiveDiagnostics>(
            GetTeamsBindingProactiveDiagnostics.Instance,
            TestContext.Current.CancellationToken);
        Assert.Equal(TeamsProactiveHealthState.Available, available.Health);
        Assert.Equal(1, available.PersonalDestinationCount);
        Assert.Equal(0, available.ChannelDestinationCount);
        Assert.Equal(0, available.MissingTargetCount);
        Assert.Equal(0, available.InvalidatedDestinationCount);
        Assert.Equal(0, available.TerminalDeliveryCount);
        Assert.Equal(0, available.AmbiguousTargetCount);
        Assert.False(available.HasInvalidRecoveredState);
        Assert.Null(available.ReasonCode);
        Assert.DoesNotContain("tenant-a", available.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("conversation-proactive-diagnostics", available.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reminder_known_destination_key_must_match_the_binding_session()
    {
        var pipeline = CreatePipeline(TestActor);
        var sessionId = CreateSessionId("tenant-a", "conversation-reminder-known");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "activity-reminder-known",
                "tenant-a",
                "conversation-reminder-known"))).Disposition);
        ReceiveOutputSubscriber();

        var dispatcher = CreateTestProbe();
        var otherSession = CreateSessionId("tenant-a", "conversation-reminder-other");
        actor.Tell(
            new TeamsBindingReminder(
                CreateReminder(sessionId, "reminder-known-rejected:1"),
                otherSession.Value),
            dispatcher.Ref);

        var nack = await dispatcher.ExpectMsgAsync<CommandNack>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("destination", nack.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reminder_known_destination_key_routes_only_the_captured_binding()
    {
        var pipeline = CreatePipeline(TestActor);
        var sessionId = CreateSessionId("tenant-a", "conversation-reminder-explicit");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "activity-reminder-explicit",
                "tenant-a",
                "conversation-reminder-explicit"))).Disposition);
        ReceiveOutputSubscriber();

        actor.Tell(new TeamsBindingReminder(
            CreateReminder(sessionId, "reminder-known-accepted:1"),
            sessionId.Value));

        ReceiveNextOutputSubscriber();
    }

    [Fact]
    public async Task Reminder_output_after_a_destination_refresh_does_not_post_to_the_refreshed_destination()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-reminder-stale");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "activity-reminder-stale-first",
                "tenant-a",
                "conversation-reminder-stale",
                "https://service-first.invalid/"))).Disposition);
        ReceiveOutputSubscriber();

        const string deliveryKey = "reminder-stale:1";
        var observer = CreateTestProbe();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, deliveryKey, observer.Ref)));
        ReceiveNextOutputSubscriber();

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "activity-reminder-stale-refresh",
                "tenant-a",
                "conversation-reminder-stale",
                "https://service-refresh.invalid/"))).Disposition);
        ReceiveNextOutputSubscriber();

        actor.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("stale reminder output")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId(deliveryKey)
        }));

        var result = await observer.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Delivered);
        Assert.Empty(replyClient.Messages);
    }

    [Fact]
    public async Task Delivered_reminder_is_not_resent_after_binding_restart()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-reminder-idempotent");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-reminder-idempotent-first");
        var observer = CreateTestProbe();

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity(
                "activity-reminder-idempotent",
                "tenant-a",
                "conversation-reminder-idempotent"))).Disposition);
        ReceiveOutputSubscriber();

        const string deliveryKey = "reminder-idempotent:1";
        first.Tell(new TeamsBindingReminder(CreateReminder(sessionId, deliveryKey, observer.Ref)));
        ReceiveNextOutputSubscriber();
        first.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("reminder reply")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId(deliveryKey)
        }));

        var delivered = await observer.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(delivered.Delivered);
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-reminder-idempotent-recovered");
        var retryObserver = CreateTestProbe();
        var dispatcher = CreateTestProbe();
        recovered.Tell(new TeamsBindingReminder(CreateReminder(sessionId, deliveryKey, retryObserver.Ref)), dispatcher.Ref);

        await dispatcher.ExpectMsgAsync<CommandAck>(cancellationToken: TestContext.Current.CancellationToken);
        var repeatedDelivery = await retryObserver.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(repeatedDelivery.Delivered);
        Assert.Single(replyClient.Messages);
    }

    [Fact]
    public async Task Interrupted_sending_reminder_recovers_as_delivery_unknown_and_is_not_resent()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-reminder-interrupted");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-reminder-interrupted-first");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity(
                "activity-reminder-interrupted",
                "tenant-a",
                "conversation-reminder-interrupted"))).Disposition);
        ReceiveOutputSubscriber();

        const string deliveryKey = "reminder-interrupted:1";
        first.Tell(new TeamsBindingReminder(CreateReminder(sessionId, deliveryKey)));
        ReceiveNextOutputSubscriber();

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-reminder-interrupted-recovered");
        var dispatcher = CreateTestProbe();
        recovered.Tell(new TeamsBindingReminder(CreateReminder(sessionId, deliveryKey)), dispatcher.Ref);

        var nack = await dispatcher.ExpectMsgAsync<CommandNack>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("operator review", nack.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(replyClient.Messages);
    }

    [Fact]
    public async Task Channel_output_for_a_root_and_reply_uses_the_same_canonical_thread_destination()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            MentionOnly = true,
            AllowedTeamIds = ["team-a"],
            AllowedChannelIds = ["channel-a"]
        };
        var dependencies = new TeamsConversationDependencies(
            options,
            pipeline,
            replyClient,
            new TeamsOutputRenderer(),
            TimeProvider.System)
        {
            PromptInjectionDetector = SafeTeamsPromptInjectionDetector.Instance
        };
        const string conversationId = "conversation-output;messageid=root-a";
        var parent = Sys.ActorOf(TeamsConversationActor.CreateProps(CreateSessionId("tenant-a", conversationId), dependencies));

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteConversationAsync(parent, CreateChannelActivity("root-a", conversationId))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(new TextOutput("root result") { SessionId = CreateChannelSessionId(conversationId) });
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteConversationAsync(parent, CreateChannelActivity("reply-a", conversationId))).Disposition);
        var replySubscriber = ReceiveNextOutputSubscriber();
        replySubscriber.Tell(new TextOutput("reply result") { SessionId = CreateChannelSessionId(conversationId) });
        await AwaitAssertAsync(() => Assert.Equal(2, replyClient.Messages.Count), cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(replyClient.Messages, message =>
        {
            Assert.Equal(TeamsConversationScope.Channel, message.Destination.Scope);
            Assert.Equal("root-a", message.Destination.RootActivityId);
            Assert.Equal("root-a", message.ReplyToActivityId);
            Assert.Equal("team-a", message.Destination.TeamId);
            Assert.Equal("channel-a", message.Destination.ChannelId);
        });

        const string secondConversationId = "conversation-output;messageid=root-b";
        var secondParent = Sys.ActorOf(TeamsConversationActor.CreateProps(CreateSessionId("tenant-a", secondConversationId), dependencies));
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteConversationAsync(secondParent, CreateChannelActivity("root-b", secondConversationId, rootActivityId: "root-b"))).Disposition);
        var secondSubscriber = ReceiveOutputSubscriber();
        secondSubscriber.Tell(new TextOutput("second root result") { SessionId = CreateChannelSessionId(secondConversationId, "root-b") });
        await AwaitAssertAsync(() => Assert.Equal(3, replyClient.Messages.Count), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("root-b", replyClient.Messages[2].Destination.RootActivityId);
        Assert.Equal("root-b", replyClient.Messages[2].ReplyToActivityId);
    }

    [Fact]
    public async Task Empty_output_skips_delivery_and_typing_failure_does_not_block_the_final_reply()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient(new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "final"));
        replyClient.TypingResults.Enqueue(new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable));
        var sessionId = CreateSessionId("tenant-a", "conversation-processing");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-processing", "tenant-a", "conversation-processing"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        var telemetryBefore = ChannelTelemetry.For(ChannelType.Teams).GetSnapshot();
        subscriber.Tell(new TextOutput("  \r\n") { SessionId = sessionId });
        await AwaitAssertAsync(() => Assert.Empty(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);

        subscriber.Tell(new ProcessingStateOutput(true) { SessionId = sessionId });
        await AwaitAssertAsync(() => Assert.Single(replyClient.TypingDestinations), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(replyClient.Messages);
        subscriber.Tell(new TextOutput("final reply") { SessionId = sessionId });
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("final reply", replyClient.Messages[0].Text);
        var telemetryAfter = ChannelTelemetry.For(ChannelType.Teams).GetSnapshot();
        Assert.Equal(telemetryBefore.RepliesPosted + 1, telemetryAfter.RepliesPosted);
        Assert.Equal(telemetryBefore.RepliesFailed, telemetryAfter.RepliesFailed);
    }

    [Fact]
    public async Task Default_transport_uses_native_typing_then_posts_the_final_reply()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient(new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "final"));
        var sessionId = CreateSessionId("tenant-a", "conversation-processing-default-transport");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-processing-default-transport", "tenant-a", "conversation-processing-default-transport"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        var telemetryBefore = ChannelTelemetry.For(ChannelType.Teams).GetSnapshot();

        subscriber.Tell(new ProcessingStateOutput(true) { SessionId = sessionId });
        await AwaitAssertAsync(() => Assert.Single(replyClient.TypingDestinations), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(replyClient.Messages);
        subscriber.Tell(new TextOutput("final reply") { SessionId = sessionId });
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(replyClient.Messages, message => Assert.Null(message.UpdateActivityId));
        Assert.Equal("final reply", replyClient.Messages[0].Text);
        var telemetryAfter = ChannelTelemetry.For(ChannelType.Teams).GetSnapshot();
        Assert.Equal(telemetryBefore.RepliesPosted + 1, telemetryAfter.RepliesPosted);
        Assert.Equal(telemetryBefore.RepliesFailed, telemetryAfter.RepliesFailed);
    }

    [Fact]
    public async Task Channel_processing_uses_the_canonical_thread_destination()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            MentionOnly = true,
            AllowedTeamIds = ["team-a"],
            AllowedChannelIds = ["channel-a"]
        };
        const string conversationId = "conversation-processing-channel;messageid=root-a";
        var sessionId = CreateChannelSessionId(conversationId);
        var actor = Sys.ActorOf(TeamsConversationActor.CreateProps(
            CreateSessionId("tenant-a", conversationId),
            new TeamsConversationDependencies(
                options,
                pipeline,
                replyClient,
                new TeamsOutputRenderer(),
                TimeProvider.System)
            {
                PromptInjectionDetector = SafeTeamsPromptInjectionDetector.Instance
            }));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteConversationAsync(actor, CreateChannelActivity("root-a", conversationId))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(new ProcessingStateOutput(true) { SessionId = sessionId });
        await AwaitAssertAsync(() => Assert.Single(replyClient.TypingDestinations), cancellationToken: TestContext.Current.CancellationToken);

        var destination = Assert.Single(replyClient.TypingDestinations);
        Assert.Equal(TeamsConversationScope.Channel, destination.Scope);
        Assert.Equal("root-a", destination.RootActivityId);
        Assert.Equal("team-a", destination.TeamId);
        Assert.Equal("channel-a", destination.ChannelId);
    }

    [Fact]
    public async Task Recovered_binding_reports_an_unavailable_destination_without_a_delivery_attempt()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-output-recovered");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));
        var telemetryBefore = ChannelTelemetry.For(ChannelType.Teams).GetSnapshot();

        actor.Tell(new TeamsSessionBindingActor.BindingOutput(
            new TextOutput("late output") { SessionId = sessionId }));

        await AwaitAssertAsync(
            () => Assert.Equal(telemetryBefore.RepliesFailed + 1, ChannelTelemetry.For(ChannelType.Teams).GetSnapshot().RepliesFailed),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(replyClient.Messages);
    }

    [Fact]
    public async Task Final_delivery_failure_makes_no_retry_attempt()
    {
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var replyClient = new RecordingTeamsReplyClient(new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable));
        var sessionId = CreateSessionId("tenant-a", "conversation-output-failed");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-output-failed", "tenant-a", "conversation-output-failed"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(new TextOutput("final reply") { SessionId = sessionId });

        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        await AwaitAssertAsync(
            () => Assert.IsType<DeliveryFailed>(Assert.Single(pipeline.Feedback)),
            cancellationToken: TestContext.Current.CancellationToken);
        var feedback = Assert.IsType<DeliveryFailed>(Assert.Single(pipeline.Feedback));
        Assert.Equal(sessionId, feedback.SessionId);
        Assert.Equal(ChannelType.Teams, feedback.ChannelType);
        Assert.Equal(DeliveryFailureKind.TransportFailure, feedback.FailureKind);
    }

    [Fact]
    public async Task Delivery_feedback_failure_is_visible_to_binding_supervision()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-output-feedback-failed");
        var replyClient = new RecordingTeamsReplyClient(new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable));
        var dependencies = CreateDependencies(new ThrowingFeedbackPipeline(CreatePipeline(TestActor)), replyClient: replyClient);
        var observer = CreateTestProbe();
        Sys.ActorOf(
            Props.Create(() => new StopOnRecoveryFailureParent(
                TeamsSessionBindingActor.CreateProps(sessionId, dependencies),
                observer.Ref)),
            "teams-output-feedback-failed-parent");
        var binding = await observer.ExpectMsgAsync<IActorRef>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(binding, CreateActivity(
                "activity-output-feedback-failed",
                "tenant-a",
                "conversation-output-feedback-failed"))).Disposition);
        ReceiveOutputSubscriber();

        Watch(binding);
        binding.Tell(new TeamsSessionBindingActor.BindingOutput(
            new TextOutput("final reply") { SessionId = sessionId }));

        ExpectTerminated(binding, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Binding_restart_does_not_leave_an_output_subscription_that_can_deliver_twice()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-output-restart");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-output-first");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity("activity-output-first", "tenant-a", "conversation-output-restart"))).Disposition);
        var oldSubscriber = ReceiveOutputSubscriber();
        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        oldSubscriber.Tell(new TextOutput("stale output") { SessionId = sessionId });
        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-output-second");
        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(recovered, CreateActivity("activity-output-second", "tenant-a", "conversation-output-restart"))).Disposition);
        var currentSubscriber = ReceiveOutputSubscriber();
        currentSubscriber.Tell(new TextOutput("current output") { SessionId = sessionId });

        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("current output", Assert.Single(replyClient.Messages).Text);
    }

    [Fact]
    public async Task A_pending_approval_survives_a_binding_restart_and_continues_once()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-restart");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-approval-first");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity("activity-approval", "tenant-a", "conversation-approval-restart"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(new ToolInteractionRequest
        {
            SessionId = sessionId,
            Kind = "approval",
            CallId = new ToolCallId("call-approval"),
            ToolName = new ToolName("safe_tool"),
            DisplayText = "Approve safe tool use.",
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ],
            RequesterSenderId = new SenderId("user-a"),
            RequesterPrincipal = PrincipalClassification.TrustedInternal
        });
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var card = replyClient.Messages[0].ApprovalCard;
        Assert.NotNull(card);
        var approve = Assert.Single(card!.Actions, action => action.Action == ApprovalOptionKeys.ApproveOnce);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-approval-second");
        var action = CreateApprovalAction(
            "tenant-a",
            "conversation-approval-restart",
            approve.CorrelationId,
            approve.Nonce,
            "synthetic-activity",
            approve.Action,
            operatorDisplayName: "Ada Lovelace");
        var accepted = await recovered.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(action, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Accepted, accepted.Disposition);
        Assert.Contains(
            new TeamsApprovalCardField("Approved By", "Ada Lovelace"),
            accepted.TerminalCard?.Fields ?? []);
        await AwaitAssertAsync(() => Assert.Single(pipeline.Feedback), cancellationToken: TestContext.Current.CancellationToken);
        var feedback = Assert.IsType<ToolInteractionResponse>(pipeline.Feedback[0]);
        Assert.Equal(ApprovalOptionKeys.ApproveOnceKey, feedback.SelectedKey);

        var duplicate = await recovered.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(action, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(TeamsApprovalActionDisposition.AlreadyProcessed, duplicate.Disposition);
        Assert.Single(pipeline.Feedback);
    }

    [Fact]
    public async Task Approval_callback_uses_an_already_cached_operator_label_without_directory_io()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-cached-label");
        var cacheReads = 0;
        var dependencies = CreateDependencies(
            pipeline,
            replyClient: replyClient,
            cachedOperatorLabel: _ =>
            {
                cacheReads++;
                return "Grace Hopper <grace@example.test>";
            });
        var actor = CreateBindingActor(sessionId, dependencies, "teams-approval-cached-label");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-approval-cached-label", "tenant-a", "conversation-approval-cached-label"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(CreateApprovalRequest(sessionId, "call-approval-cached-label", CreateStandardApprovalOptions()));
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var approvalCard = Assert.IsType<TeamsApprovalCard>(Assert.Single(replyClient.Messages).ApprovalCard);
        var approve = Assert.Single(approvalCard.Actions, action => action.Action == ApprovalOptionKeys.ApproveOnce);

        var accepted = await actor.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-cached-label",
                    approve.CorrelationId,
                    approve.Nonce,
                    "synthetic-activity",
                    approve.Action),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Accepted, accepted.Disposition);
        Assert.Contains(
            new TeamsApprovalCardField("Approved By", "Grace Hopper <grace@example.test>"),
            accepted.TerminalCard?.Fields ?? []);
        Assert.Equal(1, cacheReads);
    }

    [Fact]
    public async Task Initial_approval_card_delivery_failure_reissues_a_fresh_card_after_restart()
    {
        var replyClient = new RecordingTeamsReplyClient(
            new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable),
            new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "recovered-activity"));
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-presentation-initial");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = CreateBindingActor(sessionId, dependencies, "approval-presentation-initial-first");
        var persistenceId = BindingPersistenceId(sessionId);

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity(
                "activity-approval-presentation-initial",
                "tenant-a",
                "conversation-approval-presentation-initial"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(CreateApprovalRequest(sessionId, "call-presentation-initial", CreateStandardApprovalOptions()));
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var failedCard = Assert.IsType<TeamsApprovalCard>(Assert.Single(replyClient.Messages).ApprovalCard);
        var failedApprove = Assert.Single(failedCard.Actions, action => action.Action == ApprovalOptionKeys.ApproveOnce);
        Assert.DoesNotContain(pipeline.Feedback, static feedback => feedback is ToolInteractionResponse);
        await AwaitAssertAsync(() =>
        {
            var events = ReadJournalAsync(persistenceId).GetAwaiter().GetResult();
            Assert.Contains(events, static persisted => persisted is TeamsApprovalCardReissued);
            Assert.DoesNotContain(events, static persisted => persisted is TeamsApprovalConsumed);
        }, cancellationToken: TestContext.Current.CancellationToken);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateBindingActor(sessionId, dependencies, "approval-presentation-initial-second");
        await AwaitAssertAsync(() => Assert.Equal(2, replyClient.Messages.Count), cancellationToken: TestContext.Current.CancellationToken);
        var freshCard = Assert.IsType<TeamsApprovalCard>(replyClient.Messages[1].ApprovalCard);
        var freshApprove = Assert.Single(freshCard.Actions, action => action.Action == ApprovalOptionKeys.ApproveOnce);
        Assert.NotEqual(failedApprove.Nonce, freshApprove.Nonce);

        var stale = await recovered.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-presentation-initial",
                    failedApprove.CorrelationId,
                    failedApprove.Nonce,
                    "recovered-activity",
                    failedApprove.Action),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(TeamsApprovalActionDisposition.Rejected, stale.Disposition);

        var accepted = await recovered.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-presentation-initial",
                    freshApprove.CorrelationId,
                    freshApprove.Nonce,
                    "recovered-activity",
                    freshApprove.Action),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Accepted, accepted.Disposition);
        await AwaitAssertAsync(
            () => Assert.Single(pipeline.Feedback.OfType<ToolInteractionResponse>()),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            ApprovalOptionKeys.ApproveOnce,
            Assert.Single(pipeline.Feedback.OfType<ToolInteractionResponse>()).SelectedKey.Value);
    }

    [Fact]
    public async Task Repeated_approval_card_delivery_failures_require_recovery_and_never_self_retry()
    {
        var replyClient = new RecordingTeamsReplyClient(
            new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable),
            new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable),
            new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable));
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-presentation-bounded");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = CreateBindingActor(sessionId, dependencies, "approval-presentation-bounded-first");
        var persistenceId = BindingPersistenceId(sessionId);

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity(
                "activity-approval-presentation-bounded",
                "tenant-a",
                "conversation-approval-presentation-bounded"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(CreateApprovalRequest(sessionId, "call-presentation-bounded", CreateStandardApprovalOptions()));
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        await AwaitAssertAsync(
            () => Assert.Single(ReadJournalAsync(persistenceId).GetAwaiter().GetResult().OfType<TeamsApprovalCardReissued>()),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(replyClient.Messages);
        Assert.DoesNotContain(pipeline.Feedback, static feedback => feedback is ToolInteractionResponse);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        var second = CreateBindingActor(sessionId, dependencies, "approval-presentation-bounded-second");
        await AwaitAssertAsync(() => Assert.Equal(2, replyClient.Messages.Count), cancellationToken: TestContext.Current.CancellationToken);
        await AwaitAssertAsync(
            () => Assert.Equal(3, ReadJournalAsync(persistenceId).GetAwaiter().GetResult().OfType<TeamsApprovalCardReissued>().Count()),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, replyClient.Messages.Count);

        Watch(second);
        second.Tell(PoisonPill.Instance);
        ExpectTerminated(second, cancellationToken: TestContext.Current.CancellationToken);
        CreateBindingActor(sessionId, dependencies, "approval-presentation-bounded-third");
        await AwaitAssertAsync(() => Assert.Equal(3, replyClient.Messages.Count), cancellationToken: TestContext.Current.CancellationToken);
        await AwaitAssertAsync(
            () => Assert.Equal(5, ReadJournalAsync(persistenceId).GetAwaiter().GetResult().OfType<TeamsApprovalCardReissued>().Count()),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, replyClient.Messages.Count);
        Assert.DoesNotContain(pipeline.Feedback, static feedback => feedback is ToolInteractionResponse);
    }

    [Fact]
    public async Task Approval_feedback_failure_keeps_the_selected_card_action_retryable_without_duplicate_execution()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var pipeline = new FailOnceApprovalFeedbackPipeline(CreatePipeline(TestActor));
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-feedback-retry");
        var actor = CreateBindingActor(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient),
            "teams-approval-feedback-retry");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "activity-approval-feedback-retry",
                "tenant-a",
                "conversation-approval-feedback-retry"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(CreateApprovalRequest(sessionId, "call-feedback-retry", CreateStandardApprovalOptions()));
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var initialCard = Assert.IsType<TeamsApprovalCard>(Assert.Single(replyClient.Messages).ApprovalCard);
        var approve = Assert.Single(initialCard.Actions, action => action.Action == ApprovalOptionKeys.ApproveOnce);

        var failed = await actor.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-feedback-retry",
                    approve.CorrelationId,
                    approve.Nonce,
                    "synthetic-activity",
                    approve.Action),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Unavailable, failed.Disposition);
        var retryCard = Assert.IsType<TeamsApprovalCard>(failed.TerminalCard);
        var retry = Assert.Single(retryCard.Actions);
        Assert.Equal(approve.Action, retry.Action);
        Assert.Equal(approve.Nonce, retry.Nonce);
        Assert.Single(pipeline.Feedback);

        var accepted = await actor.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-feedback-retry",
                    retry.CorrelationId,
                    retry.Nonce,
                    "synthetic-activity",
                    retry.Action),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Accepted, accepted.Disposition);
        Assert.Equal(2, pipeline.Feedback.Count);
        Assert.Equal(1, pipeline.AcceptedFeedbackCount);

        var duplicate = await actor.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-feedback-retry",
                    retry.CorrelationId,
                    retry.Nonce,
                    "synthetic-activity",
                    retry.Action),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.AlreadyProcessed, duplicate.Disposition);
        Assert.Equal(2, pipeline.Feedback.Count);
        Assert.Equal(1, pipeline.AcceptedFeedbackCount);
    }

    [Fact]
    public async Task Approval_forwarding_recovers_after_a_lost_feedback_response_without_reexecuting()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var pipeline = new LostApprovalResponsePipeline(CreatePipeline(TestActor));
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-feedback-recovery");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = CreateBindingActor(sessionId, dependencies, "teams-approval-feedback-recovery-first");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity(
                "activity-approval-feedback-recovery",
                "tenant-a",
                "conversation-approval-feedback-recovery"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(CreateApprovalRequest(sessionId, "call-feedback-recovery", CreateStandardApprovalOptions()));
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var initialCard = Assert.IsType<TeamsApprovalCard>(Assert.Single(replyClient.Messages).ApprovalCard);
        var approve = Assert.Single(initialCard.Actions, action => action.Action == ApprovalOptionKeys.ApproveOnce);

        var failed = await first.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-feedback-recovery",
                    approve.CorrelationId,
                    approve.Nonce,
                    "synthetic-activity",
                    approve.Action),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(TeamsApprovalActionDisposition.Unavailable, failed.Disposition);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateBindingActor(sessionId, dependencies, "teams-approval-feedback-recovery-second");
        await AwaitAssertAsync(() => Assert.Equal(2, pipeline.Feedback.Count), cancellationToken: TestContext.Current.CancellationToken);

        var duplicate = await recovered.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-feedback-recovery",
                    approve.CorrelationId,
                    approve.Nonce,
                    "synthetic-activity",
                    approve.Action),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.AlreadyProcessed, duplicate.Disposition);
        Assert.Equal(2, pipeline.Feedback.Count);
        Assert.Equal(1, pipeline.AcceptedFeedbackCount);
    }

    [Fact]
    public async Task Expired_approval_reissues_its_card_without_forwarding_a_core_decision()
    {
        const string correlationId = "expired-approval-correlation";
        const string nonce = "expired-approval-nonce";
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-expired");
        await SeedJournalAsync(
            BindingPersistenceId(sessionId),
            new TeamsApprovalPendingCreated
            {
                CallId = "expired-call",
                CorrelationId = correlationId,
                NonceHash = TeamsApprovalCardRenderer.HashNonce(nonce),
                RequesterSenderId = "user-a",
                ExpiresAtUnixMilliseconds = 1,
                OfferedOptionKeys = [ApprovalOptionKeys.Deny],
                ToolName = "safe_tool",
                RequestDisplayText = "Approve safe tool use.",
                PresentationPending = false
            });

        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var replyClient = new RecordingTeamsReplyClient();
        var actor = CreateBindingActor(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient),
            "teams-approval-expired");

        var result = await actor.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-expired",
                    correlationId,
                    nonce,
                    "synthetic-activity",
                    ApprovalOptionKeys.Deny),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Expired, result.Disposition);
        Assert.Equal("Approval Card Expired", result.TerminalCard?.Title);
        Assert.Equal("STATUS: NO DECISION RECORDED", result.TerminalCard?.Footer);
        Assert.Empty(result.TerminalCard?.Actions ?? []);
        Assert.DoesNotContain(pipeline.Feedback, static feedback => feedback is ToolInteractionResponse);
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var replacement = Assert.IsType<TeamsApprovalCard>(replyClient.Messages[0].ApprovalCard);
        var replacementDeny = Assert.Single(replacement.Actions, action => action.Action == ApprovalOptionKeys.Deny);
        Assert.NotEqual(nonce, replacementDeny.Nonce);

        var stale = await actor.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-expired",
                    correlationId,
                    nonce,
                    "synthetic-activity",
                    ApprovalOptionKeys.Deny),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Rejected, stale.Disposition);
        Assert.DoesNotContain(pipeline.Feedback, static feedback => feedback is ToolInteractionResponse);

        var accepted = await actor.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-expired",
                    replacementDeny.CorrelationId,
                    replacementDeny.Nonce,
                    "synthetic-activity",
                    replacementDeny.Action),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Accepted, accepted.Disposition);
        await AwaitAssertAsync(
            () => Assert.Single(pipeline.Feedback.OfType<ToolInteractionResponse>()),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            ApprovalOptionKeys.Deny,
            Assert.Single(pipeline.Feedback.OfType<ToolInteractionResponse>()).SelectedKey.Value);

        var duplicate = await actor.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-expired",
                    replacementDeny.CorrelationId,
                    replacementDeny.Nonce,
                    "synthetic-activity",
                    replacementDeny.Action),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.AlreadyProcessed, duplicate.Disposition);
        Assert.Single(pipeline.Feedback);
    }

    [Fact]
    public async Task Expired_card_replacement_delivery_failure_recovers_with_a_fresh_nonce_after_restart()
    {
        const string correlationId = "expired-replacement-failure-correlation";
        const string expiredNonce = "expired-replacement-failure-nonce";
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-expired-delivery-failure");
        await SeedJournalAsync(
            BindingPersistenceId(sessionId),
            new TeamsApprovalPendingCreated
            {
                CallId = "expired-replacement-failure-call",
                CorrelationId = correlationId,
                NonceHash = TeamsApprovalCardRenderer.HashNonce(expiredNonce),
                RequesterSenderId = "user-a",
                ExpiresAtUnixMilliseconds = 1,
                OfferedOptionKeys = [ApprovalOptionKeys.Deny],
                ToolName = "safe_tool",
                RequestDisplayText = "Approve safe tool use.",
                PresentationPending = false
            },
            new TeamsApprovalCardDelivered
            {
                CorrelationId = correlationId,
                PromptId = "expired-original-activity"
            });

        var replyClient = new RecordingTeamsReplyClient(
            new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable),
            new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "expired-recovered-activity"));
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = CreateBindingActor(sessionId, dependencies, "expired-replacement-failure-first");
        var persistenceId = BindingPersistenceId(sessionId);

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity(
                "activity-expired-replacement-failure",
                "tenant-a",
                "conversation-approval-expired-delivery-failure"))).Disposition);
        ReceiveOutputSubscriber();

        var expired = await first.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-expired-delivery-failure",
                    correlationId,
                    expiredNonce,
                    "expired-original-activity",
                    ApprovalOptionKeys.Deny),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Expired, expired.Disposition);
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var failedReplacement = Assert.IsType<TeamsApprovalCard>(Assert.Single(replyClient.Messages).ApprovalCard);
        var failedDeny = Assert.Single(failedReplacement.Actions);
        Assert.DoesNotContain(pipeline.Feedback, static feedback => feedback is ToolInteractionResponse);
        await AwaitAssertAsync(() =>
        {
            var events = ReadJournalAsync(persistenceId).GetAwaiter().GetResult();
            Assert.DoesNotContain(events, static persisted => persisted is TeamsApprovalConsumed);
        }, cancellationToken: TestContext.Current.CancellationToken);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "expired-replacement-failure-second");

        await AwaitAssertAsync(() => Assert.Equal(2, replyClient.Messages.Count), cancellationToken: TestContext.Current.CancellationToken);
        var freshCard = Assert.IsType<TeamsApprovalCard>(replyClient.Messages[1].ApprovalCard);
        var freshDeny = Assert.Single(freshCard.Actions);
        Assert.NotEqual(failedDeny.Nonce, freshDeny.Nonce);

        var oldNonce = await recovered.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-expired-delivery-failure",
                    correlationId,
                    expiredNonce,
                    "expired-recovered-activity",
                    ApprovalOptionKeys.Deny),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(TeamsApprovalActionDisposition.Rejected, oldNonce.Disposition);

        var failedNonce = await recovered.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-expired-delivery-failure",
                    correlationId,
                    failedDeny.Nonce,
                    "expired-recovered-activity",
                    ApprovalOptionKeys.Deny),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(TeamsApprovalActionDisposition.Rejected, failedNonce.Disposition);

        var accepted = await recovered.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-expired-delivery-failure",
                    correlationId,
                    freshDeny.Nonce,
                    "expired-recovered-activity",
                    ApprovalOptionKeys.Deny),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Accepted, accepted.Disposition);
        await AwaitAssertAsync(
            () => Assert.Single(pipeline.Feedback.OfType<ToolInteractionResponse>()),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            ApprovalOptionKeys.Deny,
            Assert.Single(pipeline.Feedback.OfType<ToolInteractionResponse>()).SelectedKey.Value);
    }

    [Fact]
    public async Task Approval_action_accepts_an_unbound_card_when_its_nonce_and_sender_are_valid()
    {
        var replyClient = new RecordingTeamsReplyClient(
            new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered),
            new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered));
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-unbound");
        var actor = CreateBindingActor(sessionId, CreateDependencies(pipeline, replyClient: replyClient), "teams-approval-unbound");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-approval-unbound", "tenant-a", "conversation-approval-unbound"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(CreateApprovalRequest(sessionId, "call-unbound", CreateStandardApprovalOptions()));
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var card = Assert.IsType<TeamsApprovalCard>(Assert.Single(replyClient.Messages).ApprovalCard);
        var approve = Assert.Single(card.Actions, action => action.Action == ApprovalOptionKeys.ApproveOnce);

        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient),
            "teams-approval-unbound-recovered");

        var result = await recovered.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-unbound",
                    approve.CorrelationId,
                    approve.Nonce,
                    "synthetic-activity",
                    approve.Action),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Accepted, result.Disposition);
        await AwaitAssertAsync(() => Assert.Single(pipeline.Feedback), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(replyClient.Messages);
        var terminalCard = Assert.IsType<TeamsApprovalCard>(result.TerminalCard);
        Assert.Equal("Approval Granted", terminalCard.Title);
        Assert.Empty(terminalCard.Actions);
        Assert.Contains(new TeamsApprovalCardField("Tool", "safe_tool"), terminalCard.Fields);
        Assert.Contains(new TeamsApprovalCardField("Request", "Approve safe tool use."), terminalCard.Fields);
        Assert.Contains(new TeamsApprovalCardField("Approval Scope", "One-time approval"), terminalCard.Fields);
        Assert.Equal("STATUS: EXECUTION AUTHORIZED", terminalCard.Footer);
    }

    [Fact]
    public async Task Approval_action_forwards_each_persisted_option_key_unchanged()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-options");
        var actor = CreateBindingActor(sessionId, CreateDependencies(pipeline, replyClient: replyClient), "teams-approval-options");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-approval-options", "tenant-a", "conversation-approval-options"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        var optionKeys = new[]
        {
            ApprovalOptionKeys.ApproveOnce,
            ApprovalOptionKeys.ApproveSession,
            ApprovalOptionKeys.ApproveAlways,
            ApprovalOptionKeys.ApproveEverywhere,
            ApprovalOptionKeys.Deny
        };

        for (var index = 0; index < optionKeys.Length; index++)
        {
            var optionKey = optionKeys[index];
            subscriber.Tell(CreateApprovalRequest(sessionId, $"call-option-{index}", CreateStandardApprovalOptions()));
            await AwaitAssertAsync(
                () => Assert.Equal(index + 1, replyClient.Messages.Count(message => message.ApprovalCard?.Actions.Count > 0)),
                cancellationToken: TestContext.Current.CancellationToken);
            var card = replyClient.Messages.Last(message => message.ApprovalCard?.Actions.Count > 0).ApprovalCard!;
            var selected = Assert.Single(card.Actions, action => action.Action == optionKey);

            var result = await actor.Ask<TeamsApprovalActionResult>(
                new TeamsBindingApprovalAction(
                    CreateApprovalAction(
                        "tenant-a",
                        "conversation-approval-options",
                        selected.CorrelationId,
                        selected.Nonce,
                        "synthetic-activity",
                        optionKey),
                    TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);

            Assert.Equal(TeamsApprovalActionDisposition.Accepted, result.Disposition);
            await AwaitAssertAsync(() => Assert.Equal(index + 1, pipeline.Feedback.Count), cancellationToken: TestContext.Current.CancellationToken);
            var feedback = Assert.IsType<ToolInteractionResponse>(pipeline.Feedback[index]);
            Assert.Equal(optionKey, feedback.SelectedKey.Value);
        }
    }

    [Fact]
    public async Task Approval_action_rejects_a_key_that_was_not_offered_without_consuming_the_prompt()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-unoffered");
        var actor = CreateBindingActor(sessionId, CreateDependencies(pipeline, replyClient: replyClient), "teams-approval-unoffered");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-approval-unoffered", "tenant-a", "conversation-approval-unoffered"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(CreateApprovalRequest(
            sessionId,
            "call-unoffered",
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]));
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var card = Assert.IsType<TeamsApprovalCard>(Assert.Single(replyClient.Messages).ApprovalCard);
        var approve = Assert.Single(card.Actions, action => action.Action == ApprovalOptionKeys.ApproveOnce);

        var rejected = await actor.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-unoffered",
                    approve.CorrelationId,
                    approve.Nonce,
                    "synthetic-activity",
                    ApprovalOptionKeys.ApproveEverywhere),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Rejected, rejected.Disposition);
        Assert.Empty(pipeline.Feedback);

        var accepted = await actor.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-unoffered",
                    approve.CorrelationId,
                    approve.Nonce,
                    "synthetic-activity",
                    ApprovalOptionKeys.ApproveOnce),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Accepted, accepted.Disposition);
        await AwaitAssertAsync(() => Assert.Single(pipeline.Feedback), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ApprovalOptionKeys.ApproveOnce, Assert.IsType<ToolInteractionResponse>(pipeline.Feedback[0]).SelectedKey.Value);
    }

    [Fact]
    public async Task Pre_pr10_pending_approval_recovers_as_unavailable()
    {
        const string correlationId = "legacy-approval-correlation";
        const string nonce = "legacy-approval-nonce";
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-legacy");
        await SeedJournalAsync(
            BindingPersistenceId(sessionId),
            new TeamsApprovalPendingCreated
            {
                CallId = "legacy-call",
                CorrelationId = correlationId,
                NonceHash = TeamsApprovalCardRenderer.HashNonce(nonce),
                RequesterSenderId = "user-a",
                ExpiresAtUnixMilliseconds = 4_102_444_800_000
            },
            new TeamsApprovalCardDelivered
            {
                CorrelationId = correlationId,
                PromptId = "synthetic-activity"
            });

        var replyClient = new RecordingTeamsReplyClient();
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var actor = CreateBindingActor(sessionId, CreateDependencies(pipeline, replyClient: replyClient), "teams-approval-legacy");

        var result = await actor.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(
                CreateApprovalAction(
                    "tenant-a",
                    "conversation-approval-legacy",
                    correlationId,
                    nonce,
                    "synthetic-activity",
                    "approve"),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Unavailable, result.Disposition);
        Assert.Empty(pipeline.Feedback);
        Assert.Empty(replyClient.Messages);
        var terminalCard = Assert.IsType<TeamsApprovalCard>(result.TerminalCard);
        Assert.Equal("Approval Unavailable", terminalCard.Title);
        Assert.Empty(terminalCard.Actions);
        Assert.Equal("This approval is no longer available.", terminalCard.Body);
    }

    private IActorRef CreateBindingActor(
        SessionId sessionId,
        TeamsConversationDependencies dependencies,
        string name) => Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), name);

    private static string BindingPersistenceId(SessionId sessionId) =>
        "teams-personal-binding-" + Uri.EscapeDataString(sessionId.Value);

    private static TeamsBindingSnapshot CreateBindingSnapshot(
        string conversationId,
        string serviceUrl,
        long generation,
        IReadOnlyList<TeamsProactiveDeliveryRecorded>? deliveries = null) => new([])
    {
        Destination = CreateDestination(conversationId, serviceUrl, generation),
        LastDestinationGeneration = generation,
        ProactiveDeliveries = deliveries ?? Array.Empty<TeamsProactiveDeliveryRecorded>()
    };

    private static TeamsProactiveDestinationCaptured CreateDestination(
        string conversationId,
        string serviceUrl,
        long generation) => new()
    {
        TenantId = "tenant-a",
        ConversationId = conversationId,
        Scope = (int)TeamsConversationScope.Personal,
        ServiceUrl = serviceUrl,
        UserId = "user-a",
        Generation = generation
    };

    private async Task SeedJournalAsync(string persistenceId, params object[] payloads) =>
        await SeedJournalAsync(persistenceId, 1, payloads);

    private async Task SeedJournalAsync(string persistenceId, long firstSequenceNumber, params object[] payloads)
    {
        var writer = CreateTestProbe();
        var records = payloads.Select((payload, index) => (IPersistentRepresentation)new Persistent(
            payload,
            firstSequenceNumber + index,
            persistenceId)).ToImmutableArray();
        JournalActorRef.Tell(new WriteMessages([new AtomicWrite(records)], writer.Ref, 0), writer.Ref);
        await writer.ExpectMsgAsync<WriteMessagesSuccessful>(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var _ in payloads)
            await writer.ExpectMsgAsync<WriteMessageSuccess>(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task SeedSnapshotAsync(string persistenceId, long sequenceNumber, object snapshot)
    {
        var writer = CreateTestProbe();
        SnapshotsActorRef.Tell(new SaveSnapshot(
            new SnapshotMetadata(persistenceId, sequenceNumber, DateTime.UtcNow),
            snapshot), writer.Ref);
        await writer.ExpectMsgAsync<SaveSnapshotSuccess>(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<object>> ReadJournalAsync(string persistenceId)
    {
        var reader = CreateTestProbe();
        JournalActorRef.Tell(new ReplayMessages(
            1,
            long.MaxValue,
            long.MaxValue,
            persistenceId,
            reader.Ref), reader.Ref);
        var messages = new List<object>();
        while (true)
        {
            var message = await reader.ExpectMsgAsync<object>(cancellationToken: TestContext.Current.CancellationToken);
            if (message is RecoverySuccess)
                return messages;
            messages.Add(Assert.IsType<ReplayedMessage>(message).Persistent.Payload);
        }
    }

    private async Task<TSnapshot> WaitForSnapshotAsync<TSnapshot>(string persistenceId)
        where TSnapshot : class
    {
        TSnapshot? snapshot = null;
        await AwaitAssertAsync(() =>
        {
            snapshot = LoadLatestSnapshotAsync(persistenceId).GetAwaiter().GetResult() as TSnapshot;
            Assert.NotNull(snapshot);
        }, cancellationToken: TestContext.Current.CancellationToken);
        return snapshot!;
    }

    private Task<TeamsBindingSnapshot> WaitForBindingSnapshotAsync(string persistenceId) =>
        WaitForSnapshotAsync<TeamsBindingSnapshot>(persistenceId);

    private async Task<object?> LoadLatestSnapshotAsync(string persistenceId)
    {
        var reader = CreateTestProbe();
        SnapshotsActorRef.Tell(new LoadSnapshot(
            persistenceId,
            SnapshotSelectionCriteria.Latest,
            long.MaxValue), reader.Ref);
        var loaded = await reader.ExpectMsgAsync<LoadSnapshotResult>(cancellationToken: TestContext.Current.CancellationToken);
        return loaded.Snapshot?.Snapshot;
    }

    private async Task<TeamsBindingProactiveDiagnostics> GetDiagnosticsAsync(IActorRef actor) =>
        await actor.Ask<TeamsBindingProactiveDiagnostics>(
            GetTeamsBindingProactiveDiagnostics.Instance,
            TestContext.Current.CancellationToken);

    private async Task<TeamsBindingProactiveDiagnostics> WaitForMigrationStateAsync(
        IActorRef actor,
        TeamsMigrationHealthState expected)
    {
        TeamsBindingProactiveDiagnostics? diagnostics = null;
        await AwaitAssertAsync(() =>
        {
            diagnostics = GetDiagnosticsAsync(actor).GetAwaiter().GetResult();
            Assert.Equal(expected, diagnostics.Migration);
        }, cancellationToken: TestContext.Current.CancellationToken);
        return diagnostics!;
    }

    private object DecodeLegacy<TMessage>(string manifest, TMessage message)
        where TMessage : IMessage
    {
        var serializer = Assert.IsType<NetclawProtobufSerializer>(
            Sys.Serialization.FindSerializerFor(new SessionId("legacy-fixture")));
        var decoded = serializer.FromBinary(message.ToByteArray(), manifest);
        Assert.IsType<LegacyChannelPersistenceEnvelope>(decoded);
        return decoded;
    }

    private static LegacyProto.TeamsApprovalPendingCreatedProto CreateLegacyApproval(string correlationId) => new()
    {
        CallId = "legacy-call",
        CorrelationId = correlationId,
        NonceHash = new string('a', 64),
        RequesterSenderId = "user-a",
        ExpiresAtUnixMilliseconds = 4_102_444_800_000
    };

    private static LegacyProto.TeamsProactiveDestinationCapturedProto CreateLegacyDestination(
        string conversationId,
        string serviceUrl = "https://service.invalid/") => new()
    {
        TenantId = "tenant-a",
        ConversationId = conversationId,
        Scope = (int)TeamsConversationScope.Personal,
        ServiceUrl = serviceUrl,
        UserId = "user-a"
    };

    private static LegacyProto.DurableActivityDispatchSnapshotProto CreateLegacyBindingSnapshot(
        string fingerprint,
        string conversationId,
        string correlationId,
        string deliveryKey)
    {
        var snapshot = new LegacyProto.DurableActivityDispatchSnapshotProto
        {
            TeamsDestination = new LegacyProto.TeamsProactiveDestinationSnapshotEntryProto
            {
                TenantId = "tenant-a",
                ConversationId = conversationId,
                Scope = (int)TeamsConversationScope.Personal,
                ServiceUrl = "https://service.invalid/",
                UserId = "user-a"
            }
        };
        snapshot.ActivityFingerprints.Add(fingerprint);
        snapshot.TeamsApprovals.Add(new LegacyProto.TeamsApprovalSnapshotEntryProto
        {
            CallId = "legacy-call",
            CorrelationId = correlationId,
            NonceHash = new string('a', 64),
            RequesterSenderId = "user-a",
            ExpiresAtUnixMilliseconds = 4_102_444_800_000
        });
        snapshot.TeamsProactiveDeliveries.Add(new LegacyProto.TeamsProactiveDeliverySnapshotEntryProto
        {
            DeliveryKey = deliveryKey,
            State = (int)TeamsProactiveDeliveryState.Sent
        });
        return snapshot;
    }

    private ISessionPipeline CreatePipeline(IActorRef sessionManager)
    {
        var registry = ActorRegistry.For(Sys);
        registry.Register<SessionManagerActorKey>(sessionManager);
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"teams-routing-{Guid.NewGuid():N}"));
        return new SessionPipeline(
            Sys,
            new RequiredActor<SessionManagerActorKey>(registry),
            new TestSessionStorageResolver(paths));
    }

    private async Task<WebApplication> BuildRequestIndependenceHostAsync(
        RequestLifetimeProbe requestLifetime,
        RequestIndependentReplyClient replyClient,
        IActorRef? sessionManager = null,
        bool includeChannel = false,
        bool useExactChannelUserAccess = false,
        bool useExactChannelGroupAccess = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var settings = new Dictionary<string, string?>
        {
            ["Teams:Enabled"] = "true",
            ["Teams:TenantId"] = "tenant-a",
            ["Teams:ClientId"] = "test-client",
            ["Teams:ClientSecret"] = "test-secret",
            ["Teams:AllowDirectMessages"] = "true",
            ["Teams:AllowedUserIds:0"] = "user-a",
            ["Teams:MentionOnly"] = includeChannel ? "true" : "false",
            ["Teams:BotId"] = "bot",
            ["Teams:AllowedTeamIds:0"] = "team-a",
            ["Teams:AllowedChannelIds:0"] = "channel-a"
        };
        if (useExactChannelUserAccess)
        {
            settings["Teams:AllowedUserIds:0"] = "user-b";
            settings["Teams:ChannelAccessOverrides:0:TeamId"] = "team-a";
            settings["Teams:ChannelAccessOverrides:0:ChannelId"] = "channel-a";
            settings["Teams:ChannelAccessOverrides:0:AllowedUserIds:0"] = "user-a";
        }
        else if (useExactChannelGroupAccess)
        {
            settings["Teams:AllowedUserIds:0"] = "user-b";
            settings["Teams:AllowedGroupIds:0"] = "group-b";
            settings["Teams:ChannelAccessOverrides:0:TeamId"] = "team-a";
            settings["Teams:ChannelAccessOverrides:0:ChannelId"] = "channel-a";
            settings["Teams:ChannelAccessOverrides:0:AllowedGroupIds:0"] = "group-a";
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddChannelIntegrations(builder.Configuration);
        builder.Services.AddSingleton<ActorSystem>(Sys);
        builder.Services.AddSingleton<ISessionPipeline>(CreatePipeline(sessionManager ?? TestActor));
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<IPromptInjectionDetector>(SafeTeamsPromptInjectionDetector.Instance);
        builder.Services.AddSingleton(requestLifetime);
        builder.Services.AddScoped<RequestScopeSentinel>();
        if (useExactChannelGroupAccess)
        {
            builder.Services.RemoveAll<ITeamsDirectory>();
            builder.Services.AddSingleton<ITeamsDirectory>(new ExactGroupTeamsDirectory());
        }
        builder.AddTeamsIngress();
        builder.Services
            .AddAuthentication(TestTeamsAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestTeamsAuthenticationHandler>(
                TestTeamsAuthenticationHandler.SchemeName,
                _ => { });
        builder.Services.RemoveAll<ITeamsReplyClient>();
        builder.Services.AddSingleton<ITeamsReplyClient>(replyClient);
        builder.Services.PostConfigure<AuthorizationOptions>(options =>
        {
            var testPolicy = new AuthorizationPolicyBuilder(TestTeamsAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build();
            options.DefaultPolicy = testPolicy;
            options.AddPolicy(TeamsActivityEndpointExtensions.AuthorizationPolicy, testPolicy);
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            _ = context.RequestServices.GetRequiredService<RequestScopeSentinel>();
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseTeamsIngress();
        app.MapTeamsActivityEndpoint();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpRequestMessage CreateTeamsActivityRequest(TeamsActivity activity)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TeamsActivityEndpointExtensions.ActivityPath)
        {
            Content = new StringContent(activity.ToJson(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "Bearer eyJhbGciOiJub25lIn0.eyJ0aWQiOiJ0ZW5hbnQtYSJ9.");
        return request;
    }

    private static MessageActivity CreateSdkPersonalMessage(string conversationId, string activityId) =>
        MessageActivity.FromActivity(CoreActivity.FromJsonString(JsonSerializer.Serialize(new
        {
            type = "message",
            text = "request input",
            id = activityId,
            from = new { id = "user-a" },
            conversation = new
            {
                id = conversationId,
                tenantId = "tenant-a",
                conversationType = TeamsConversationType.Personal.Value
            },
            serviceUrl = "https://request-service.invalid/"
        })));

    private static InvokeActivity CreateSdkPersonalApprovalAction(
        string conversationId,
        string promptActivityId,
        string correlationId,
        string nonce,
        string action)
    {
        var activity = InvokeActivity.FromActivity(CoreActivity.FromJsonString(JsonSerializer.Serialize(new
        {
            type = "invoke",
            name = "adaptiveCard/action",
            id = "request-approval-action",
            replyToId = promptActivityId,
            from = new { id = "user-a" },
            conversation = new
            {
                id = conversationId,
                tenantId = "tenant-a",
                conversationType = TeamsConversationType.Personal.Value
            },
            serviceUrl = "https://request-service.invalid/",
            value = new
            {
                action = new
                {
                    type = "Action.Execute",
                    data = new Dictionary<string, object>
                    {
                        ["correlation"] = correlationId,
                        ["nonce"] = nonce,
                        ["action"] = action
                    }
                }
            }
        })));

        // The native SDK's typed projection intentionally does not retain ReplyToId.
        // Restore the original wire value so this request exercises the host middleware
        // that preserves it before the projection occurs.
        ((CoreActivity)activity).ReplyToId = promptActivityId;
        return activity;
    }

    private static InvokeActivity CreateSdkChannelApprovalAction(
        string conversationId,
        string promptActivityId,
        string correlationId,
        string nonce,
        string action)
    {
        var activity = InvokeActivity.FromActivity(CoreActivity.FromJsonString(JsonSerializer.Serialize(new
        {
            type = "invoke",
            name = "adaptiveCard/action",
            id = "request-channel-approval-action",
            replyToId = promptActivityId,
            from = new { id = "user-a" },
            conversation = new
            {
                id = conversationId,
                tenantId = "tenant-a",
                conversationType = TeamsConversationType.Channel.Value
            },
            serviceUrl = "https://request-service.invalid/",
            value = new
            {
                action = new
                {
                    type = "Action.Execute",
                    data = new Dictionary<string, object>
                    {
                        ["correlation"] = correlationId,
                        ["nonce"] = nonce,
                        ["action"] = action
                    }
                }
            }
        })));

        // The live Action.Execute fixture omits channelData. Keep the raw
        // reply locator so the host middleware follows the SDK boundary.
        ((CoreActivity)activity).ReplyToId = promptActivityId;
        return activity;
    }

    private static MessageActivity CreateSdkChannelReplyMessage(string conversationId) =>
        MessageActivity.FromActivity(CoreActivity.FromJsonString(JsonSerializer.Serialize(new
        {
            type = "message",
            text = "<at>bot</at> thread approval input",
            id = "request-channel-approval-reply",
            from = new { id = "user-a" },
            recipient = new { id = "28:bot" },
            conversation = new
            {
                id = conversationId,
                tenantId = "tenant-a",
                conversationType = TeamsConversationType.Channel.Value
            },
            channelData = new
            {
                team = new { id = "team-a" },
                channel = new { id = "channel-a" }
            },
            entities = new[]
            {
                new
                {
                    type = "mention",
                    mentioned = new { id = "28:bot" },
                    text = "<at>bot</at>"
                }
            },
            serviceUrl = "https://request-service.invalid/"
        })));

    private static MessageActivity CreateSdkChannelRootMessage(string conversationId, string rootActivityId) =>
        MessageActivity.FromActivity(CoreActivity.FromJsonString(JsonSerializer.Serialize(new
        {
            type = "message",
            text = "<at>bot</at> request input",
            id = rootActivityId,
            from = new { id = "user-a" },
            recipient = new { id = "28:bot" },
            conversation = new
            {
                id = conversationId,
                tenantId = "tenant-a",
                conversationType = TeamsConversationType.Channel.Value
            },
            channelData = new
            {
                team = new { id = "team-a" },
                channel = new { id = "channel-a" }
            },
            entities = new[]
            {
                new
                {
                    type = "mention",
                    mentioned = new { id = "28:bot" },
                    text = "<at>bot</at>"
                }
            },
            serviceUrl = "https://request-service.invalid/"
        })));

    private static TeamsConversationDependencies CreateDependencies(
        ISessionPipeline pipeline,
        bool allowDirectMessages = true,
        string[]? allowedUserIds = null,
        string tenantId = "tenant-a",
        bool enabled = false,
        ITeamsReplyClient? replyClient = null,
        IPromptInjectionDetector? detector = null,
        Func<string, string?>? cachedOperatorLabel = null,
        bool allowAttachments = false,
        ITeamsAttachmentDownloader? attachmentDownloader = null,
        IContentScanner? contentScanner = null,
        ToolAudienceProfiles? audienceProfiles = null,
        ModelCapabilities? modelCapabilities = null,
        NetclawPaths? paths = null) => new(
        new TeamsChannelOptions
        {
            Enabled = enabled,
            TenantId = tenantId,
            AllowDirectMessages = allowDirectMessages,
            AllowedUserIds = allowedUserIds ?? ["user-a"],
            AllowAttachments = allowAttachments
        },
        pipeline,
        replyClient ?? new TestTeamsReplyClient(),
        new TeamsOutputRenderer(),
        TimeProvider.System)
    {
        PromptInjectionDetector = detector ?? SafeTeamsPromptInjectionDetector.Instance,
        CachedOperatorLabel = cachedOperatorLabel,
        AttachmentDownloader = attachmentDownloader,
        ContentScanner = contentScanner,
        AudienceProfiles = audienceProfiles,
        ModelCapabilities = modelCapabilities,
        StorageResolver = paths is null ? null : new TestSessionStorageResolver(paths)
    };

    private static TeamsConversationDependencies CreateAttachmentDependencies(
        ISessionPipeline pipeline,
        NetclawPaths paths,
        ITeamsReplyClient? replyClient = null) => CreateDependencies(
        pipeline,
        replyClient: replyClient,
        allowAttachments: true,
        attachmentDownloader: new TestTeamsAttachmentDownloader(),
        contentScanner: new NullContentScanner(),
        audienceProfiles: ToolAudienceProfileDefaults.CreateProfiles(),
        paths: paths);

    private static TeamsConversationDependencies CreateVerifiedAttachmentDependencies(
        ISessionPipeline pipeline,
        NetclawPaths paths,
        byte[] bytes,
        ITeamsReplyClient? replyClient = null) => CreateDependencies(
        pipeline,
        replyClient: replyClient,
        allowAttachments: true,
        attachmentDownloader: new BytesTeamsAttachmentDownloader(bytes),
        contentScanner: new MagicByteContentScanner(new ContentPolicy()),
        audienceProfiles: ToolAudienceProfileDefaults.CreateProfiles(),
        modelCapabilities: ImageModelCapabilities,
        paths: paths);

    private static TeamsConversationDependencies CreatePublicVerifiedAttachmentDependencies(
        ISessionPipeline pipeline,
        NetclawPaths paths,
        byte[] bytes,
        ITeamsReplyClient? replyClient = null,
        TeamsChannelOptions? options = null) => new(
        options ?? new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            MentionOnly = true,
            AllowedTeamIds = ["team-a"],
            AllowedChannelIds = ["channel-a"],
            AllowedUserIds = ["user-a"],
            AllowAttachments = true
        },
        pipeline,
        replyClient ?? new TestTeamsReplyClient(),
        new TeamsOutputRenderer(),
        TimeProvider.System)
    {
        PromptInjectionDetector = SafeTeamsPromptInjectionDetector.Instance,
        AttachmentDownloader = new BytesTeamsAttachmentDownloader(bytes),
        ContentScanner = new MagicByteContentScanner(new ContentPolicy()),
        AudienceProfiles = ToolAudienceProfileDefaults.CreateProfiles(),
        ModelCapabilities = ImageModelCapabilities,
        StorageResolver = new TestSessionStorageResolver(paths)
    };

    private static ModelCapabilities ImageModelCapabilities { get; } = new()
    {
        InputModalities = ModelModality.Text | ModelModality.Image
    };

    private static byte[] BytesFor(string mimeType) => mimeType switch
    {
        "image/png" => PngBytes,
        "image/jpeg" => TestImages.Image(16, 16, SKEncodedImageFormat.Jpeg),
        _ => throw new ArgumentOutOfRangeException(nameof(mimeType), mimeType, "Unsupported test MIME type.")
    };

    private static ServiceProvider CreateConversationServiceProvider(
        ISessionPipeline pipeline,
        bool includeDetector = true,
        ITeamsReplyClient? replyClient = null,
        TeamsPrincipalAuthorizer? principalAuthorizer = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(pipeline);
        services.AddSingleton<ITeamsReplyClient>(replyClient ?? new TestTeamsReplyClient());
        services.AddSingleton<TeamsOutputRenderer>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        if (includeDetector)
            services.AddSingleton<IPromptInjectionDetector>(SafeTeamsPromptInjectionDetector.Instance);
        if (principalAuthorizer is not null)
            services.AddSingleton(principalAuthorizer);

        return services.BuildServiceProvider();
    }

    private sealed class TestTeamsReplyClient : ITeamsReplyClient
    {
        public Task<TeamsDeliveryResult> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "synthetic-activity"));

        public Task<TeamsDeliveryResult> SendTypingAsync(TeamsOutboundDestination destination, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered));
    }

    private sealed class TestTeamsAttachmentDownloader : ITeamsAttachmentDownloader
    {
        public async Task<AttachmentDownloadResult> DownloadAsync(
            TeamsInboundActivity activity,
            TeamsAttachmentMetadata attachment,
            string stagingDirectory,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            var path = Path.Combine(stagingDirectory, $".teams-test-download-{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(path, PngBytes, cancellationToken);
            return new AttachmentDownloadResult(path, PngBytes.Length);
        }
    }

    private sealed class BytesTeamsAttachmentDownloader(byte[] bytes) : ITeamsAttachmentDownloader
    {
        private readonly byte[] _bytes = bytes;

        public async Task<AttachmentDownloadResult> DownloadAsync(
            TeamsInboundActivity activity,
            TeamsAttachmentMetadata attachment,
            string stagingDirectory,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            if (_bytes.Length > maximumBytes)
                throw new AttachmentTooLargeException(_bytes.Length, maximumBytes);

            var path = Path.Combine(stagingDirectory, $".teams-test-download-{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(path, _bytes, cancellationToken);
            return new AttachmentDownloadResult(path, _bytes.Length);
        }
    }

    private sealed class IndexedGatedTeamsAttachmentDownloader : ITeamsAttachmentDownloader
    {
        private readonly TaskCompletionSource[] _releases = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private readonly object _metricsLock = new();
        private int _active;
        public int CallCount { get; private set; }
        public int MaximumActive { get; private set; }
        public System.Threading.Channels.Channel<int> Started { get; } = System.Threading.Channels.Channel.CreateUnbounded<int>();
        public TaskCompletionSource ReleaseCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(int index) => _releases[index].TrySetResult();

        public async Task<AttachmentDownloadResult> DownloadAsync(
            TeamsInboundActivity activity,
            TeamsAttachmentMetadata attachment,
            string stagingDirectory,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            lock (_metricsLock)
            {
                CallCount++;
                MaximumActive = Math.Max(MaximumActive, ++_active);
            }
            try
            {
                await Started.Writer.WriteAsync(attachment.SourceIndex, cancellationToken);
                try
                {
                    await _releases[attachment.SourceIndex].Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Hold the cancelled operation until the test advances the complete batch clock.
                    await ReleaseCancellation.Task.WaitAsync(TestContext.Current.CancellationToken);
                    throw;
                }
                var bytes = TestImages.Image(16 + attachment.SourceIndex, 16);
                return await new BytesTeamsAttachmentDownloader(bytes).DownloadAsync(
                    activity, attachment, stagingDirectory, maximumBytes, cancellationToken);
            }
            finally
            {
                lock (_metricsLock)
                    _active--;
            }
        }
    }

    private sealed class GatedTeamsAttachmentDownloader(byte[] bytes) : ITeamsAttachmentDownloader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken DownloadToken { get; private set; }

        public async Task<AttachmentDownloadResult> DownloadAsync(
            TeamsInboundActivity activity,
            TeamsAttachmentMetadata attachment,
            string stagingDirectory,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            DownloadToken = cancellationToken;
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await new BytesTeamsAttachmentDownloader(bytes).DownloadAsync(
                activity, attachment, stagingDirectory, maximumBytes, cancellationToken);
        }
    }

    private sealed record ReadTeamsRouteObservation;

    private sealed record TeamsRouteObservation(int DispatchedTurns, ImmutableArray<object> RouteDeadLetters);

    private sealed class TeamsRouteObservationActor : ReceiveActor
    {
        private int _dispatchedTurns;
        private readonly List<object> _routeDeadLetters = [];

        public TeamsRouteObservationActor(IActorRef observer)
        {
            Receive<JoinSession>(message => observer.Tell(message));
            Receive<SendUserMessage>(message =>
            {
                _dispatchedTurns++;
                observer.Tell(message);
            });
            Receive<DeadLetter>(message =>
            {
                if (message.Message is TeamsBindingRouteResult or TeamsIngressRouteResult)
                    _routeDeadLetters.Add(message.Message);
            });
            Receive<ReadTeamsRouteObservation>(_ => Sender.Tell(
                new TeamsRouteObservation(_dispatchedTurns, _routeDeadLetters.ToImmutableArray())));
        }
    }

    private sealed class SafeTeamsPromptInjectionDetector : IPromptInjectionDetector
    {
        public static readonly SafeTeamsPromptInjectionDetector Instance = new();

        public Task<PromptInjectionResult> DetectAsync(
            string text,
            string sourceContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PromptInjectionResult.Safe());
    }

    private sealed class FixedTeamsPromptInjectionDetector(PromptInjectionResult result) : IPromptInjectionDetector
    {
        public Task<PromptInjectionResult> DetectAsync(
            string text,
            string sourceContext,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class ThrowingTeamsPromptInjectionDetector : IPromptInjectionDetector
    {
        public Task<PromptInjectionResult> DetectAsync(
            string text,
            string sourceContext,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("detector unavailable");
    }

    private sealed class ExactGroupTeamsDirectory : ITeamsDirectory
    {
        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>> SearchTeamsAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryTeam>> GetTeamAsync(
            string teamId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryTeam>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>> GetChannelsAsync(
            string teamId,
            int maximumResults,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryChannel>> GetChannelAsync(
            string teamId,
            string channelId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryChannel>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>> SearchUsersAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>> SearchGroupsAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroup>> GetGroupAsync(
            string groupId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryGroup>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryUser>> GetUserAsync(
            string userId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryUser>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlySet<string>>> CheckUserGroupMembershipAsync(
            string userId,
            IReadOnlyCollection<string> groupIds,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlySet<string>>.Available(
                new HashSet<string>(["group-a"], StringComparer.Ordinal)));
    }

    private sealed class RequestLifetimeProbe
    {
        private int _created;
        private int _disposed;

        public bool AllDisposed => Volatile.Read(ref _created) > 0
            && Volatile.Read(ref _created) == Volatile.Read(ref _disposed);

        public void Created() => Interlocked.Increment(ref _created);

        public void Disposed() => Interlocked.Increment(ref _disposed);
    }

    private sealed class RequestScopeSentinel : IDisposable
    {
        private readonly RequestLifetimeProbe _probe;

        public RequestScopeSentinel(RequestLifetimeProbe probe)
        {
            _probe = probe;
            _probe.Created();
        }

        public void Dispose() => _probe.Disposed();
    }

    private sealed class RequestIndependentReplyClient(RequestLifetimeProbe requestLifetime) : ITeamsReplyClient
    {
        public List<TeamsOutboundMessage> Messages { get; } = [];

        public Task<TeamsDeliveryResult> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken = default)
        {
            if (!requestLifetime.AllDisposed)
                throw new ObjectDisposedException("request_scope", "The reply client must not run during the inbound request.");

            Messages.Add(message);
            return Task.FromResult(new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "request-independent-activity"));
        }

        public Task<TeamsDeliveryResult> SendTypingAsync(TeamsOutboundDestination destination, CancellationToken cancellationToken = default)
        {
            if (!requestLifetime.AllDisposed)
                throw new ObjectDisposedException("request_scope", "The reply client must not run during the inbound request.");

            return Task.FromResult(new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered));
        }
    }

    private sealed class RecordingTeamsReplyClient(params TeamsDeliveryResult[] results) : ITeamsReplyClient
    {
        private readonly Queue<TeamsDeliveryResult> _results = new(results);

        public List<TeamsOutboundMessage> Messages { get; } = [];

        public List<TeamsOutboundDestination> TypingDestinations { get; } = [];

        public Queue<TeamsDeliveryResult> TypingResults { get; } = [];

        public Task<TeamsDeliveryResult> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(_results.Count == 0
                ? new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "synthetic-activity")
                : _results.Dequeue());
        }

        public Task<TeamsDeliveryResult> SendTypingAsync(TeamsOutboundDestination destination, CancellationToken cancellationToken = default)
        {
            TypingDestinations.Add(destination);
            return Task.FromResult(TypingResults.Count == 0
                ? new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered)
                : TypingResults.Dequeue());
        }
    }

    private static SessionId CreateSessionId(string tenantId, string conversationId)
    {
        Assert.True(TeamsSessionIdentifierCodec.TryCreatePersonal(tenantId, conversationId, out var sessionId, out _));
        return sessionId;
    }

    private static SessionId CreateChannelSessionId(string conversationId, string rootActivityId = "root-a")
    {
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel("tenant-a", conversationId, rootActivityId, out var sessionId, out _));
        return sessionId;
    }

    private static TeamsInboundActivity CreateGroupChatActivity(
        string activityId,
        bool isMentioned = true,
        string senderId = "user-a") => new(
        new TeamsIngressTrustContext(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            senderId,
            "tenant-a",
            "19:group-chat@thread.v2",
            TeamsConversationScope.GroupChat,
            activityId,
            TimeProvider.System.GetUtcNow()),
        "hello",
        new TeamsReplyMetadata(null, null, "https://service.invalid/"),
        isMentioned: isMentioned);

    private static TeamsInboundActivity CreateActivity(
        string activityId,
        string tenantId,
        string conversationId,
        string serviceUrl = "https://service.invalid/") => new(
        new TeamsIngressTrustContext(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            "user-a",
            tenantId,
            conversationId,
            TeamsConversationScope.Personal,
            activityId,
            TimeProvider.System.GetUtcNow()),
        "hello",
        new TeamsReplyMetadata(null, null, serviceUrl));

    private static TeamsInboundActivity CreateAttachmentActivity(
        string activityId,
        string text,
        ImmutableArray<TeamsAttachmentMetadata> attachments) => new(
        new TeamsIngressTrustContext(
            TrustAudience.Personal,
            PrincipalClassification.TrustedInternal,
            TrustBoundary.Personal,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            "user-a",
            "tenant-a",
            "attachment-conversation",
            TeamsConversationScope.Personal,
            activityId,
            TimeProvider.System.GetUtcNow()),
        text,
        new TeamsReplyMetadata(null, null, "https://service.invalid/"),
        attachments: attachments);

    private static TeamsAttachmentMetadata CreateInboundAttachment(
        string name,
        TeamsInboundAttachmentKind kind,
        int sourceIndex,
        string contentType = "image/png") => new(name, contentType, 4)
    {
        Kind = kind,
        SourceIndex = sourceIndex
    };

    private static TeamsInboundActivity CreateChannelActivity(
        string activityId,
        string conversationId,
        TeamsIngressActivityKind kind = TeamsIngressActivityKind.Message,
        string rootActivityId = "root-a",
        bool isMentioned = true,
        string senderId = "user-a",
        string text = "hello",
        ImmutableArray<TeamsAttachmentMetadata> attachments = default) => new(
        new TeamsIngressTrustContext(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            senderId,
            "tenant-a",
            conversationId,
            TeamsConversationScope.Channel,
            activityId,
            TimeProvider.System.GetUtcNow()),
        kind == TeamsIngressActivityKind.Message ? text : string.Empty,
        new TeamsReplyMetadata(null, rootActivityId, "https://service.invalid/"),
        isMentioned: isMentioned,
        attachments: attachments,
        kind: kind,
        teamId: "team-a",
        channelId: "channel-a");

    private static TeamsApprovalAction CreateApprovalAction(
        string tenantId,
        string conversationId,
        string correlationId,
        string nonce,
        string promptActivityId,
        string action,
        string? operatorDisplayName = null) => new(
        new TeamsIngressTrustContext(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            "user-a",
            tenantId,
            conversationId,
            TeamsConversationScope.Personal,
            "invoke-approval",
            TimeProvider.System.GetUtcNow()),
        correlationId,
        nonce,
        action,
        null,
        null,
        null,
        promptActivityId,
        "https://service.invalid/",
        operatorDisplayName);

    private static TeamsApprovalAction CreateGroupChatApprovalAction(
        string senderId,
        string correlationId,
        string nonce,
        string action) => new(
        new TeamsIngressTrustContext(
            TrustAudience.Team,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Team,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            senderId,
            "tenant-a",
            "19:group-chat@thread.v2",
            TeamsConversationScope.GroupChat,
            "group-invoke-approval",
            TimeProvider.System.GetUtcNow()),
        correlationId,
        nonce,
        action,
        null,
        null,
        null,
        "synthetic-activity",
        "https://service.invalid/");

    private static ToolInteractionRequest CreateApprovalRequest(
        SessionId sessionId,
        string callId,
        IReadOnlyList<ToolInteractionOption> options) => new()
    {
        SessionId = sessionId,
        Kind = "approval",
        CallId = new ToolCallId(callId),
        ToolName = new ToolName("safe_tool"),
        DisplayText = "Approve safe tool use.",
        Options = options,
        RequesterSenderId = new SenderId("user-a"),
        RequesterPrincipal = PrincipalClassification.TrustedInternal
    };

    private static IReadOnlyList<ToolInteractionOption> CreateStandardApprovalOptions() =>
    [
        new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel),
        new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
    ];

    private static DeliverTrustedSessionTurn CreateReminder(
        SessionId sessionId,
        string deliveryKey,
        IActorRef? observer = null) => new(
        sessionId,
        "Run the scheduled reminder.",
        new MessageSource
        {
            ChannelType = ChannelType.Teams,
            SenderId = new SenderId("reminder-system"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.VerifiedAutomation,
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
            {
                SourceKind = new SourceKind("reminder")
            },
            ReceivedAt = TimeProvider.System.GetUtcNow(),
            ReminderId = new ReminderId(deliveryKey),
            DeliveryObserver = observer
        });

    private static Task<TeamsBindingRouteResult> RouteAsync(
        IActorRef actor,
        TeamsInboundActivity activity) => actor.Ask<TeamsBindingRouteResult>(
        new TeamsBindingIngress(activity, TestContext.Current.CancellationToken),
        TestContext.Current.CancellationToken);

    private static Task<TeamsBindingRouteResult> RouteConversationAsync(
        IActorRef actor,
        TeamsInboundActivity activity) => actor.Ask<TeamsBindingRouteResult>(
        new TeamsConversationIngress(activity, TestContext.Current.CancellationToken),
        TestContext.Current.CancellationToken);

    private SendUserMessage ReceiveDispatchedMessage()
    {
        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        return ExpectMsg<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
    }

    private SendUserMessage ReceiveGroupChatMessage()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var message = ExpectMsg<object>(cancellationToken: TestContext.Current.CancellationToken);
            if (message is SendUserMessage dispatched)
            {
                return dispatched;
            }

            Assert.IsType<JoinSession>(message);
        }

        throw new Xunit.Sdk.XunitException("Expected a group chat message after session initialization.");
    }

    private IActorRef ReceiveGroupChatOutputSubscriber()
    {
        var subscriber = ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken).Subscriber;
        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        ExpectMsg<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
        return subscriber;
    }

    private IActorRef ReceiveOutputSubscriber()
    {
        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        return ReceiveNextOutputSubscriber();
    }

    private IActorRef ReceiveNextOutputSubscriber()
    {
        var subscription = ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        ExpectMsg<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
        return subscription.Subscriber;
    }

    private sealed class TestTeamsAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestTeams";

        public TestTeamsAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "teams-test-user"),
                new Claim("tid", "tenant-a")
            ], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class DiscardActor : ReceiveActor
    {
        public static Props Create() => Props.Create(() => new DiscardActor());
    }

    private sealed class StopOnRecoveryFailureParent : ReceiveActor
    {
        private readonly Props _childProps;
        private readonly IActorRef _observer;

        public StopOnRecoveryFailureParent(Props childProps, IActorRef observer)
        {
            _childProps = childProps;
            _observer = observer;
        }

        protected override void PreStart()
        {
            _observer.Tell(Context.ActorOf(_childProps, "binding"));
            base.PreStart();
        }

        protected override SupervisorStrategy SupervisorStrategy() =>
            new OneForOneStrategy(_ => Directive.Stop, loggingEnabled: false);
    }

    private sealed class ApprovalRecordingPipeline(ISessionPipeline fallback) : ISessionPipeline
    {
        public List<IWithSessionId> Feedback { get; } = [];

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null,
            CancellationToken cancellationToken = default) =>
            fallback.CreateAsync(sessionId, options, materializer, cancellationToken);

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
        {
            Feedback.Add(feedback);
            return Task.CompletedTask;
        }

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
        {
            Feedback.Add(feedback);
            return Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
        }
    }

    private sealed class FailOnceApprovalFeedbackPipeline(ISessionPipeline fallback) : ISessionPipeline
    {
        private bool _failFirst = true;

        public List<IWithSessionId> Feedback { get; } = [];
        public int AcceptedFeedbackCount { get; private set; }

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null,
            CancellationToken cancellationToken = default) =>
            fallback.CreateAsync(sessionId, options, materializer, cancellationToken);

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            fallback.SendFeedbackAsync(feedback, ct);

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
        {
            Feedback.Add(feedback);
            if (_failFirst)
            {
                _failFirst = false;
                return Task.FromException<ISessionResponse>(new InvalidOperationException("synthetic feedback failure"));
            }

            AcceptedFeedbackCount++;
            return Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
        }
    }

    private sealed class LostApprovalResponsePipeline(ISessionPipeline fallback) : ISessionPipeline
    {
        private int _attempt;

        public List<IWithSessionId> Feedback { get; } = [];
        public int AcceptedFeedbackCount { get; private set; }

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null,
            CancellationToken cancellationToken = default) =>
            fallback.CreateAsync(sessionId, options, materializer, cancellationToken);

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            fallback.SendFeedbackAsync(feedback, ct);

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
        {
            Feedback.Add(feedback);
            if (_attempt++ == 0)
            {
                // Simulate a session that committed the decision, then lost its
                // acknowledgement before the Teams binding could observe it.
                AcceptedFeedbackCount++;
                return Task.FromException<ISessionResponse>(new InvalidOperationException("synthetic lost session response"));
            }

            return Task.FromResult<ISessionResponse>(CommandNack.For(feedback.SessionId, ApprovalNackReasons.PromptExpired));
        }
    }

    private sealed class ThrowingFeedbackPipeline(ISessionPipeline fallback) : ISessionPipeline
    {
        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null,
            CancellationToken cancellationToken = default) =>
            fallback.CreateAsync(sessionId, options, materializer, cancellationToken);

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Task.FromException(new InvalidOperationException("synthetic delivery feedback failure"));

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            fallback.SendFeedbackAndWaitAsync(feedback, ct);
    }

    private sealed class FailThenDelegatePipeline(ISessionPipeline fallback) : ISessionPipeline
    {
        private bool _shouldFail = true;

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            if (_shouldFail)
            {
                _shouldFail = false;
                return Task.FromException<MaterializedSession>(new InvalidOperationException("synthetic pipeline failure"));
            }

            return fallback.CreateAsync(sessionId, options, materializer, cancellationToken);
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            fallback.SendFeedbackAsync(feedback, ct);

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            fallback.SendFeedbackAndWaitAsync(feedback, ct);
    }

    private sealed class CancelThenDelegatePipeline(
        ISessionPipeline fallback,
        CancellationTokenSource cancellation) : ISessionPipeline
    {
        private bool _shouldCancel = true;

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            if (_shouldCancel)
            {
                _shouldCancel = false;
                cancellation.Cancel();
                return Task.FromCanceled<MaterializedSession>(cancellation.Token);
            }

            return fallback.CreateAsync(sessionId, options, materializer, cancellationToken);
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            fallback.SendFeedbackAsync(feedback, ct);

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            fallback.SendFeedbackAndWaitAsync(feedback, ct);
    }
}
