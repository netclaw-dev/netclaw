// -----------------------------------------------------------------------
// <copyright file="MattermostSessionBindingContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Mattermost;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class MattermostSessionBindingContractTests(ITestOutputHelper output)
    : SessionBindingContractTests(output)
{
    private RecordingMattermostReplyClient _replyClient = new();
    private int _actorCounter;

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // WithNetclawSerialization registers the strict-serialization checker the
        // daemon uses, so any persisted event lacking INetclawSerializableMessage
        // or a proto manifest will fail loudly in tests instead of silently
        // surviving on the in-memory journal — matches Slack/Discord contract tests.
        builder.WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization();
    }

    protected override IActorRef CreateBindingActor(
        SessionId sessionId,
        RecordingSessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector)
    {
        ResetReplyClient();
        return CreateActorCore(sessionId, pipeline, detector);
    }

    protected override IActorRef CreateBindingActorWithPipeline(
        SessionId sessionId,
        ISessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector)
    {
        ResetReplyClient();
        var options = new MattermostChannelOptions();
        var deps = new MattermostGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: options,
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
            Paths: TestMattermostGatewayDeps.NewTestPaths(),
            PromptInjectionDetector: detector);

        var name = $"mm-session-fail-{Interlocked.Increment(ref _actorCounter)}";
        return Sys.ActorOf(MattermostSessionBindingActor.CreateProps(
            sessionId,
            new MattermostChannelId("ch-test"),
            new MattermostRootPostId("root-test"),
            deps), name);
    }

    protected override object CreateInboundMessage(string text, string senderId)
        => new MattermostThreadInbound(
            SessionId: new SessionId("ignored"),
            ChannelId: new MattermostChannelId("ch-test"),
            PostId: new MattermostPostId($"post-{Guid.NewGuid():N}"),
            RootPostId: new MattermostRootPostId("root-test"),
            EventId: new MattermostEventId($"evt-{Guid.NewGuid():N}"),
            SenderId: new MattermostUserId(senderId),
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            {
                SourceKind = new SourceKind("mattermost")
            },
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());

    protected override object CreateApprovalResponse(string callId, string selectedKey, string senderId)
        => new MattermostApprovalResponse(
            ChannelId: new MattermostChannelId("ch-test"),
            RootPostId: new MattermostRootPostId("root-test"),
            CallId: new Netclaw.Tools.ToolCallId(callId),
            SelectedKey: selectedKey,
            SenderId: new MattermostUserId(senderId));

    protected override IReadOnlyList<string> GetPostedTexts()
        => _replyClient.Posts.Select(p => p.Text).ToList();

    protected override void ClearPostedTexts()
        => _replyClient.Posts.Clear();

    protected override void SetReplyClientThrows(Exception ex)
        => _replyClient.ThrowOnPost = ex;

    protected override void ClearReplyClientThrows()
        => _replyClient.ThrowOnPost = null;

    protected override ChannelType ExpectedChannelType => ChannelType.Mattermost;

    protected override bool SupportsApprovalSenderReplies => true;

    protected override bool SupportsThreadHydration => true;

    private long _hydrationEventCounter;

    protected override IActorRef CreateBindingActorWithHydration(
        SessionId sessionId,
        RecordingSessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector,
        IThreadHistoryFetcher historyFetcher)
    {
        ResetReplyClient();
        return CreateActorCore(sessionId, pipeline, detector, historyFetcher: historyFetcher);
    }

    protected override IReadOnlyList<ChannelInput> CreateHistoryItems(int count)
    {
        var items = new List<ChannelInput>();
        for (var i = 0; i < count; i++)
        {
            items.Add(new ChannelInput
            {
                SenderId = new SenderId($"history-user-{i}"),
                ChannelId = "ch-test",
                MessageId = $"post-history-{900_000 + i}",
                Contents = [new TextContent($"history message {i}")],
                ReceivedAt = TimeProvider.System.GetUtcNow().AddMinutes(-count + i),
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.Public,
                Principal = PrincipalClassification.UntrustedExternal,
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            });
        }

        return items;
    }

    protected override object CreateHydrationTriggerInboundMessage(string text, string senderId)
    {
        var postId = $"post-live-{1_000_000 + Interlocked.Increment(ref _hydrationEventCounter)}";
        return new MattermostThreadInbound(
            SessionId: new SessionId("ignored"),
            ChannelId: new MattermostChannelId("ch-test"),
            PostId: new MattermostPostId(postId),
            RootPostId: new MattermostRootPostId("root-test"),
            EventId: new MattermostEventId(postId),
            SenderId: new MattermostUserId(senderId),
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            {
                SourceKind = new SourceKind("mattermost")
            },
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());
    }

    private void ResetReplyClient()
    {
        var pendingThrow = _replyClient.ThrowOnPost;
        _replyClient = new RecordingMattermostReplyClient { ThrowOnPost = pendingThrow };
    }

    private IActorRef CreateActorCore(
        SessionId sessionId,
        ISessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector,
        IThreadHistoryFetcher? historyFetcher = null)
    {
        var options = new MattermostChannelOptions();
        var deps = new MattermostGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: options,
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
            Paths: TestMattermostGatewayDeps.NewTestPaths(),
            PromptInjectionDetector: detector,
            ThreadHistoryFetcher: historyFetcher);

        var name = $"mm-session-contract-{Interlocked.Increment(ref _actorCounter)}";
        return Sys.ActorOf(MattermostSessionBindingActor.CreateProps(
            sessionId,
            new MattermostChannelId("ch-test"),
            new MattermostRootPostId("root-test"),
            deps), name);
    }

    // Regression for #939: cold-spawn redraw via the Mattermost action callback's
    // post_id. When the binding has no in-memory pending approval (passivation),
    // the binding must update the original prompt post using the payload-provided
    // post ID so the buttons clear.
    [Fact]
    public async Task Cold_button_approval_response_redraws_prompt_via_payload_postId()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-mm-cold-redraw");

        var pipeline = new RecordingSessionPipeline(_ => []);
        var actor = CreateBindingActor(sid, pipeline, detector);

        var payloadPostId = new MattermostPostId("post-from-callback-abc");
        actor.Tell(new MattermostApprovalResponse(
            ChannelId: new MattermostChannelId("ch-test"),
            RootPostId: new MattermostRootPostId("root-test"),
            CallId: new Netclaw.Tools.ToolCallId("call-mm-cold-redraw"),
            SelectedKey: ApprovalOptionKeys.Deny,
            SenderId: new MattermostUserId("user-1"),
            RequesterSenderId: null,
            PromptPostId: payloadPostId));

        await AwaitAssertAsync(() =>
        {
            var feedback = pipeline.RecordedFeedback.OfType<ToolInteractionResponse>().ToList();
            Assert.Single(feedback);
            Assert.Equal("call-mm-cold-redraw", feedback[0].CallId.Value);

            var update = Assert.Single(_replyClient.Updates);
            Assert.Equal(payloadPostId, update.PostId);
            Assert.Contains("resolved", update.Text, StringComparison.OrdinalIgnoreCase);
            // Mattermost surfaces the decision via the option's label
            // (`ApprovalOptionKeys.LabelFor`) rather than a separate "Denied"
            // word — match the channel-specific style.
            Assert.Contains(ApprovalOptionKeys.DenyLabel, update.Text, StringComparison.Ordinal);
            // Attachment carries no actions on resolve.
            Assert.NotNull(update.Attachments);
            Assert.All(update.Attachments!, a => Assert.Null(a.Actions));
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Recovered_text_approval_response_redraws_prompt_via_persisted_postId()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-mm-recovered-text-redraw");

        var initialPipeline = new RecordingSessionPipeline(_ =>
        [
            new ToolInteractionRequest
            {
                SessionId = sid,
                Kind = "approval",
                CallId = new Netclaw.Tools.ToolCallId("call-mm-recovered-text-redraw"),
                ToolName = new Netclaw.Tools.ToolName("shell_execute"),
                DisplayText = "git status",
                RequesterSenderId = new SenderId("user-1"),
                Options =
                [
                    new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                    new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                ]
            }
        ]);

        var firstActor = CreateBindingActor(sid, initialPipeline, detector);
        await AwaitAssertAsync(() => Assert.Single(_replyClient.Posts), cancellationToken: ct);

        var stopProbe = CreateTestProbe("mattermost-recovered-text-stop");
        stopProbe.Watch(firstActor);
        Sys.Stop(firstActor);
        await stopProbe.ExpectTerminatedAsync(firstActor, cancellationToken: ct);

        var recoveryPipeline = new RecordingSessionPipeline(_ => []);
        var recoveredActor = CreateBindingActor(sid, recoveryPipeline, detector);
        recoveredActor.Tell(CreateInboundMessage("A", "user-1"), TestActor);

        await AwaitAssertAsync(() =>
        {
            var feedback = recoveryPipeline.RecordedFeedback.OfType<ToolInteractionResponse>().ToList();
            Assert.Single(feedback);
            Assert.Equal("call-mm-recovered-text-redraw", feedback[0].CallId.Value);
            Assert.Equal(ApprovalOptionKeys.ApproveOnce, feedback[0].SelectedKey.Value);

            var update = Assert.Single(_replyClient.Updates);
            Assert.Equal(new MattermostPostId("post-1"), update.PostId);
            Assert.Contains("resolved", update.Text, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(update.Attachments);
            Assert.All(update.Attachments!, attachment => Assert.Null(attachment.Actions));

            // PendingApprovalPromptTracked now carries the tool name + display
            // text through cold recovery, so the resolved attachment regains
            // the Tool/Action detail the hot path renders. End-to-end guarantee
            // that the binding journals and rehydrates the fields.
            Assert.Contains("shell_execute", update.Text, StringComparison.Ordinal);
            Assert.Contains("git status", update.Text, StringComparison.Ordinal);
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Button_prompt_post_associates_returned_post_id_with_tokens_for_that_prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-mm-store-association");
        var store = new MattermostCallbackActionStore(TimeProvider.System);

        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new ToolInteractionRequest
            {
                SessionId = sid,
                Kind = "approval",
                CallId = new Netclaw.Tools.ToolCallId("call-mm-association"),
                ToolName = new Netclaw.Tools.ToolName("shell_execute"),
                DisplayText = "git status",
                RequesterSenderId = new SenderId("user-1"),
                Options =
                [
                    new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                    new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                ]
            }
        ]);

        ResetReplyClient();
        var options = new MattermostChannelOptions { CallbackUrl = "https://example.test/api/mattermost/actions" };
        var deps = new MattermostGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: options,
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
            Paths: TestMattermostGatewayDeps.NewTestPaths(),
            CallbackUrl: options.CallbackUrl,
            PromptInjectionDetector: detector,
            CallbackActionStore: store);

        var actor = Sys.ActorOf(MattermostSessionBindingActor.CreateProps(
            sid,
            new MattermostChannelId("ch-test"),
            new MattermostRootPostId("root-test"),
            deps), $"mm-session-store-{Interlocked.Increment(ref _actorCounter)}");

        await AwaitAssertAsync(() => Assert.Single(_replyClient.Posts), cancellationToken: ct);

        var actions = _replyClient.Posts[0].Attachments![0].Actions!;
        foreach (var action in actions)
        {
            Assert.True(store.TryGet(action.Context["action_token"], out var stored));
            Assert.Equal("post-1", stored!.PromptPostId);
            Assert.Equal("call-mm-association", stored.CallId);
        }
    }
}
