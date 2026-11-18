// -----------------------------------------------------------------------
// <copyright file="MattermostSessionBindingContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Mattermost;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class MattermostSessionBindingContractTests(ITestOutputHelper output)
    : SessionBindingContractTests(output)
{
    private RecordingMattermostReplyClient _replyClient = new();
    private int _actorCounter;

    protected override SessionIngressGate IngressGate { get; } = new();

    protected override Config? Config => ConfigurationFactory.ParseString("""
        akka.scheduler.implementation = "Akka.TestKit.TestScheduler, Akka.TestKit"
        """).WithFallback(base.Config);

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
            IngressGate: IngressGate,
            TimeProvider: Clock,
            Options: options,
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.MattermostWithProcessingRenderer(_replyClient),
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
                        StorageResolver: Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance,
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

    protected override object CreateInboundWithAttachment(string text, string senderId)
        => ((MattermostThreadInbound)CreateInboundMessage(text, senderId)) with
        {
            Attachments = [new MattermostFileReference("gate.txt", "text/plain", 1, "https://mattermost.example/gate")]
        };

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
        => _replyClient.Clear();

    protected override void SetReplyClientThrows(Exception ex)
        => _replyClient.ThrowOnPost = ex;

    protected override void SetReplyClientThrowsOnce(Exception ex)
        => _replyClient.ThrowOnceOnPost = ex;

    protected override void SetReplyClientThrowsInOrder(params Exception[] exceptions)
    {
        foreach (var exception in exceptions)
            _replyClient.ThrowInOrderOnPosts.Enqueue(exception);
    }

    protected override void ClearReplyClientThrows()
        => _replyClient.ThrowOnPost = null;

    protected override ChannelType ExpectedChannelType => ChannelType.Mattermost;

    protected override IReadOnlyList<RenderedPrompt> GetPromptRedraws()
        => _replyClient.Updates.Select(update => new RenderedPrompt(
            string.Join("\n", new[] { update.Text }.Concat((update.Attachments ?? [])
                .SelectMany(attachment => new[] { attachment.Text ?? string.Empty, attachment.Fallback ?? string.Empty }))),
            (update.Attachments ?? []).Any(attachment => attachment.Actions is { Count: > 0 }))).ToList();

    // A Mattermost binding renders buttons only with a callback URL. The child prompt specs need the button mode.
    protected override IActorRef CreateButtonBindingActor(
        SessionId sessionId,
        RecordingSessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector)
    {
        ResetReplyClient();
        var options = new MattermostChannelOptions { CallbackUrl = "https://example.test/api/mattermost/actions" };
        var deps = new MattermostGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: IngressGate,
            TimeProvider: Clock,
            Options: options,
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.MattermostWithProcessingRenderer(_replyClient),
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
            StorageResolver: Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance,
            CallbackUrl: options.CallbackUrl,
            PromptInjectionDetector: detector,
            CallbackActionStore: new MattermostCallbackActionStore(TimeProvider.System));

        return Sys.ActorOf(MattermostSessionBindingActor.CreateProps(
            sessionId,
            new MattermostChannelId("ch-test"),
            new MattermostRootPostId("root-test"),
            deps), $"mm-session-buttons-{Interlocked.Increment(ref _actorCounter)}");
    }

    protected override string GetCursorPersistenceId(SessionId sessionId)
        => $"mattermost-session-cursor-{Uri.EscapeDataString(sessionId.Value)}";

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

    private void CarryOver(IEnumerable<Exception> pendingOrdered)
    {
        foreach (var exception in pendingOrdered)
            _replyClient.ThrowInOrderOnPosts.Enqueue(exception);
    }

    private void ResetReplyClient()
    {
        var pendingThrow = _replyClient.ThrowOnPost;
        var pendingUploadThrow = _replyClient.ThrowOnUpload;
        // Keep the one-shot failure too. Dropping it made every "fail one post" contract test pass
        // without a failed post.
        var pendingThrowOnce = _replyClient.ThrowOnceOnPost;
        var pendingOrdered = _replyClient.ThrowInOrderOnPosts.ToArray();
        _replyClient = new RecordingMattermostReplyClient
        {
            ThrowOnPost = pendingThrow,
            ThrowOnceOnPost = pendingThrowOnce,
            ThrowOnUpload = pendingUploadThrow
        };
        CarryOver(pendingOrdered);
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
            IngressGate: IngressGate,
            TimeProvider: Clock,
            Options: options,
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.MattermostWithProcessingRenderer(_replyClient),
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
                        StorageResolver: Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance,
            PromptInjectionDetector: detector,
            ThreadHistoryFetcher: historyFetcher);

        var name = $"mm-session-contract-{Interlocked.Increment(ref _actorCounter)}";
        return Sys.ActorOf(MattermostSessionBindingActor.CreateProps(
            sessionId,
            new MattermostChannelId("ch-test"),
            new MattermostRootPostId("root-test"),
            deps), name);
    }

    [Fact]
    public async Task FileOutput_uploads_file_and_posts_file_id_to_mattermost_thread()
    {
        var ct = TestContext.Current.CancellationToken;
        var sid = new SessionId("session-mm-file-output");
        await using var paths = TestMattermostGatewayDeps.NewTestPaths();
        var filePath = Path.Join(paths.Paths.BasePath, $"mattermost-upload-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(filePath, "hello mattermost", ct);

        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new FileOutput
            {
                SessionId = sid,
                FilePath = filePath,
                FileName = "report.txt",
                MimeType = new Netclaw.Media.MimeType("text/plain")
            },
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(1), OpenChildRunIds = [] }
        ]);

        CreateActorCore(sid, pipeline, new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe()));

        await AwaitAssertAsync(() =>
        {
            var upload = Assert.Single(_replyClient.Uploads);
            Assert.Equal("ch-test", upload.ChannelId.Value);
            Assert.Equal(filePath, upload.FilePath);
            Assert.Equal("report.txt", upload.FileName);

            var post = Assert.Single(_replyClient.Posts);
            Assert.NotNull(post.FileIds);
            Assert.Single(post.FileIds!);
            Assert.Contains("report.txt", post.Text, StringComparison.Ordinal);
            Assert.Equal("root-test", post.RootPostId?.Value);
        }, cancellationToken: ct);
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
        // The transport post is recorded before the actor persists prompt
        // tracking. Round-trip through the mailbox before stopping so recovery
        // observes the persisted post id instead of racing the persist callback.
        await firstActor.Ask<ActorIdentity>(
            new Identify("prompt-tracking-persisted"),
            TimeSpan.FromSeconds(3),
            ct);

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
            IngressGate: IngressGate,
            TimeProvider: Clock,
            Options: options,
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.MattermostWithProcessingRenderer(_replyClient),
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
                        StorageResolver: Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance,
            CallbackUrl: options.CallbackUrl,
            PromptInjectionDetector: detector,
            CallbackActionStore: store);

        var actor = Sys.ActorOf(MattermostSessionBindingActor.CreateProps(
            sid,
            new MattermostChannelId("ch-test"),
            new MattermostRootPostId("root-test"),
            deps), $"mm-session-store-{Interlocked.Increment(ref _actorCounter)}");

        // The fake transport records the post before the binding calls
        // AssociatePromptPostId. Waiting on the post alone reads PromptPostId while
        // it is still null, so poll the whole association invariant instead.
        await AwaitAssertAsync(
            () =>
            {
                var post = Assert.Single(_replyClient.Posts);
                foreach (var action in post.Attachments![0].Actions!)
                {
                    Assert.True(store.TryGet(action.Context["action_token"], out var stored));
                    Assert.Equal("post-1", stored!.PromptPostId);
                    Assert.Equal("call-mm-association", stored.CallId);
                }
            },
            cancellationToken: ct);
    }

    // Design D3 (S7): a child prompt has buttons only. A Mattermost binding with no callback URL can post only text, so
    // it cannot show a child prompt. It posts no text prompt and reports the prompt as unavailable, and the session
    // withdraws the request. A parent prompt keeps the text-only mode.
    // Planted bug: the text-only binding posts the child prompt with "Reply with" (today).
    [Fact]
    public async Task Text_only_binding_reports_a_child_prompt_unavailable_and_posts_no_text_prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-mm-text-only-child-prompt");
        var request = ChildApprovalRequest(sid);
        var pipeline = new RecordingSessionPipeline(_ => [request]);

        CreateBindingActor(sid, pipeline, detector);

        await AwaitAssertAsync(() =>
        {
            var feedback = Assert.Single(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>());
            Assert.Equal(request.CallId, feedback.CallId);
            Assert.Equal(ApprovalOptionKeys.PromptUnavailable, feedback.SelectedKey.Value);
        }, cancellationToken: ct);
        Assert.DoesNotContain(_replyClient.Posts, post => post.Text.Contains("reply with", StringComparison.OrdinalIgnoreCase));
    }

    // Review fix S1 of PR 2: the typed Lifetime is the one signal for a child prompt. The call ID shape is not. A parent
    // prompt whose call ID holds "/subagent-approval/" keeps the lifetime "until turn end", so a text-only binding posts
    // it as a text prompt with its reply hint and does not refuse it.
    // Planted bug: the binding detects a child prompt by the call ID shape (today), so it refuses the parent prompt as
    // prompt_unavailable and the session withdraws a call that a person could answer.
    [Fact]
    public async Task Text_only_binding_posts_a_parent_prompt_with_a_child_shaped_call_id_as_a_text_prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-mm-text-only-child-shaped-parent-prompt");
        var request = ChildApprovalRequest(sid) with { Lifetime = ToolInteractionLifetime.UntilTurnEnd };
        var pipeline = new RecordingSessionPipeline(_ =>
        [
            request,
            // The binding handles outputs in order, so this post proves that it handled the prompt.
            new TextOutput("after-prompt-marker") { SessionId = sid }
        ]);

        CreateBindingActor(sid, pipeline, detector);

        await AwaitAssertAsync(() =>
        {
            var texts = _replyClient.Posts.Select(post => post.Text).ToList();
            Assert.Contains(texts, text => text.Contains("after-prompt-marker", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("gh pr view 2558", StringComparison.Ordinal)
                && text.Contains("reply with", StringComparison.OrdinalIgnoreCase));
        }, cancellationToken: ct);
        Assert.DoesNotContain(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>(),
            feedback => feedback.SelectedKey.Value == ApprovalOptionKeys.PromptUnavailable);
    }
    [Fact]
    public async Task Trusted_turn_checks_session_and_ingress_then_preserves_authority_and_ack_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var sid = new SessionId("session-trusted-authority");
        var pipeline = new RecordingSessionPipeline(_ => []);
        var binding = CreateBindingActor(sid, pipeline,
            new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe()));
        var source = new MessageSource
        {
            ChannelType = ExpectedChannelType,
            SenderId = new SenderId("reminder-owner"),
            MessageId = "reminder-fire-key",
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Principal = PrincipalClassification.UntrustedExternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            {
                SourceKind = new SourceKind("reminder")
            },
            RequestedDeliveryTarget = new Netclaw.Tools.ChannelDeliveryTargetInfo(
                ExpectedChannelType.ToWireValue(), "destination", "other-channel", threadOrRootId: "other-thread"),
            ReminderId = new Netclaw.Actors.Reminders.ReminderId("reminder-fire-key")
        };
        IngressGate.TryClose("contract restart");
        var rejected = await binding.Ask<CommandNack>(
            new DeliverTrustedSessionTurn(new SessionId("wrong-session"), "reject this", source),
            TimeSpan.FromSeconds(15), ct);
        Assert.Equal(sid, rejected.SessionId);
        Assert.Equal("Session id mismatch", rejected.Reason);
        var closed = await binding.Ask<CommandNack>(
            new DeliverTrustedSessionTurn(sid, "reject closed ingress", source),
            TimeSpan.FromSeconds(15), ct);
        Assert.Equal("contract restart", closed.Reason);
        IngressGate.Reopen();

        var originalSender = CreateTestProbe();
        binding.Tell(new DeliverTrustedSessionTurn(sid, "keep this authority", source), originalSender.Ref);
        await AwaitAssertAsync(() =>
        {
            var input = Assert.Single(pipeline.CapturedInputs);
            Assert.Equal(source.SenderId, input.SenderId);
            Assert.Equal(source.MessageId, input.MessageId);
            Assert.Equal(source.Audience, input.Audience);
            Assert.Equal(source.Boundary, input.Boundary);
            Assert.Equal(source.Principal, input.Principal);
            Assert.Equal(source.Provenance, input.Provenance);
            Assert.Equal(source.RequestedDeliveryTarget, input.RequestedDeliveryTarget);
            Assert.Equal(source.ReminderId, input.ReminderId);
            Assert.Equal(originalSender.Ref, input.AckTarget);
            Assert.Equal("keep this authority", Assert.IsType<TextContent>(Assert.Single(input.Contents)).Text);
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Empty_turn_posts_fallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-empty-turn");
        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new TurnCompleted { SessionId = sid, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1), OpenChildRunIds = [] }
        ]);

        CreateBindingActor(sid, pipeline, detector);
        await AwaitAssertAsync(() =>
        {
            var texts = GetPostedTexts();
            Assert.Contains(texts, t => t.Contains("didn't manage to produce a reply", StringComparison.OrdinalIgnoreCase)
                                        || t.Contains("warning", StringComparison.OrdinalIgnoreCase));
        }, cancellationToken: ct);
    }

    // Rule of Aaron (18 Oct 2026): a person never sees a Netclaw error unless the model narrates it. The session ends
    // a refused loop quietly with no reply when the model ignored the escalated nudge. The binding must then
    // post no empty-turn fallback, because "I didn't manage to produce a reply" is Netclaw text, and its advice
    // to send the message again starts the same loop again.
    [Fact]
    public async Task Quiet_end_with_no_text_posts_no_fallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-quiet-end");
        // Turn 2 is a barrier: once its reply is visible, turn 1 (including its fallback decision) is processed.
        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new TurnCompleted
            {
                SessionId = sid, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1), QuietEnd = true, OpenChildRunIds = []
            },
            new TextOutput("barrier reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(2), OpenChildRunIds = [] }
        ]);

        CreateBindingActor(sid, pipeline, detector);

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(GetPostedTexts(), t => t.Contains("barrier reply", StringComparison.Ordinal));
        }, cancellationToken: ct);

        var texts = GetPostedTexts();
        Assert.DoesNotContain(texts, t => t.Contains("didn't manage to produce a reply", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(texts, t => t.Contains(":warning:", StringComparison.Ordinal));
    }

    // The quiet end skips only the fallback. An empty turn without the flag still posts it, also directly after
    // a quiet end: the flag belongs to one turn.
    [Fact]
    public async Task Empty_turn_after_a_quiet_end_still_posts_fallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-quiet-end-then-empty");
        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new TurnCompleted
            {
                SessionId = sid, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1), QuietEnd = true, OpenChildRunIds = []
            },
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(2), OpenChildRunIds = [] },
            new TextOutput("after-empty-turn") { SessionId = sid }
        ]);

        CreateBindingActor(sid, pipeline, detector);

        await AwaitAssertAsync(() =>
        {
            var texts = GetPostedTexts();
            Assert.Contains("after-empty-turn", texts);
            Assert.Single(texts, IsEmptyTurnFallback);
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Delivered_turn_no_fallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-delivered-turn");
        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new TextOutput("real reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(1), OpenChildRunIds = [] },
            new TextOutput("after-completed-turn") { SessionId = sid }
        ]);

        CreateBindingActor(sid, pipeline, detector);
        await AwaitAssertAsync(() =>
        {
            var texts = GetPostedTexts();
            Assert.Contains("real reply", texts);
            Assert.Contains("after-completed-turn", texts);
            Assert.DoesNotContain(texts, IsEmptyTurnFallback);
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Init_failure_stops_actor()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var failingPipeline = new FailingSessionPipeline(
            new InvalidOperationException("init boom"));
        var sid = new SessionId("session-init-fail");

        var actor = CreateBindingActorWithPipeline(sid, failingPipeline, detector);

        var probe = CreateTestProbe();
        probe.Watch(actor);
        await probe.ExpectTerminatedAsync(actor, cancellationToken: ct);
    }

    [Fact]
    public async Task Stashes_messages_during_init()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-stash");
        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new TurnCompleted { SessionId = sid, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1), OpenChildRunIds = [] }
        ], reactive: true);

        pipeline.InstallCreationGate();
        var actor = CreateBindingActor(sid, pipeline, detector);
        await pipeline.Created.WaitAsync(ct);
        actor.Tell(CreateInboundMessage("stashed message", "user-1"), TestActor);
        pipeline.ReleaseCreationGate();

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(pipeline.CapturedInputs, input =>
                input.Contents.OfType<TextContent>().Any(content => content.Text == "stashed message"));
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Thread_history_fetched_at_most_once_per_actor_lifetime()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-hydration-once-only");
        var historyFetcher = new RecordingThreadHistoryFetcher();
        historyFetcher.SetHistory(CreateHistoryItems(1));

        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new TextOutput("reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1), OpenChildRunIds = [] }
        ]);

        var actor = CreateBindingActorWithHydration(sid, pipeline, detector, historyFetcher);
        await pipeline.Created.WaitAsync(ct);

        // Hydration runs once at startup.
        await AwaitAssertAsync(() => Assert.Equal(1, historyFetcher.FetchCount), cancellationToken: ct);

        // Subsequent live inbounds must not re-trigger hydration. This is the
        // core invariant that prevents the duplicate-image bug: in-flight
        // turns and concurrent inbounds never re-derive historical content
        // from the channel's history API.
        actor.Tell(CreateHydrationTriggerInboundMessage("first live", "user-1"), TestActor);
        actor.Tell(CreateHydrationTriggerInboundMessage("second live", "user-1"), TestActor);

        await AwaitAssertAsync(() => Assert.True(pipeline.CapturedInputs.Count >= 3),
            cancellationToken: ct);

        Assert.Equal(1, historyFetcher.FetchCount);
    }

    [Fact]
    public async Task Inbounds_arriving_during_hydration_are_stashed_and_unstash_after()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-hydration-stash");
        var historyFetcher = new RecordingThreadHistoryFetcher();
        historyFetcher.SetHistory(CreateHistoryItems(1));
        historyFetcher.InstallGate();

        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new TextOutput("reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1), OpenChildRunIds = [] }
        ]);

        var actor = CreateBindingActorWithHydration(sid, pipeline, detector, historyFetcher);
        await pipeline.Created.WaitAsync(ct);

        // Wait until hydration's fetcher call is in flight (blocked on the
        // gate). At this point the actor is provably in Hydrating behavior.
        await historyFetcher.FetchCalledTask.WaitAsync(ct);

        actor.Tell(CreateHydrationTriggerInboundMessage("stashed live", "user-1"), TestActor);

        historyFetcher.ReleaseGate();

        await AwaitAssertAsync(() => Assert.Equal(2, pipeline.CapturedInputs.Count), cancellationToken: ct);
        var inputs = pipeline.CapturedInputs.ToArray();
        Assert.Contains("history message 0", string.Join("\n", inputs[0].Contents.OfType<TextContent>().Select(content => content.Text)));
        Assert.Equal("stashed live", inputs[1].ExecutableText);
    }

    [Fact]
    public async Task Image_in_history_flows_through_at_most_once_across_multiple_inbounds()
    {
        // Direct regression test for the duplicate-image compaction bug.
        // Under the pre-fix design, every live inbound re-fetched thread
        // history and re-emitted any in-flight message's DataContent, so the
        // same image accumulated into history N times — destroying session
        // compaction budget. The fix: hydration runs once at actor startup,
        // and live inbounds never re-fetch history. The image flows through
        // exactly once.
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-image-flows-once");
        var historyFetcher = new RecordingThreadHistoryFetcher();
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var baseItem = Assert.Single(CreateHistoryItems(1));
        historyFetcher.SetHistory(
        [
            baseItem with
            {
                Contents = [.. baseItem.Contents, new DataContent(imageBytes, "image/png")]
            }
        ]);

        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new TextOutput("reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1), OpenChildRunIds = [] }
        ]);

        var actor = CreateBindingActorWithHydration(sid, pipeline, detector, historyFetcher);
        await pipeline.Created.WaitAsync(ct);

        // Wait for hydration backfill to land.
        await AwaitAssertAsync(() => Assert.True(pipeline.CapturedInputs.Count >= 1),
            cancellationToken: ct);

        // Send three live inbounds in quick succession — the kind of scenario
        // that produced 8 image copies under the pre-fix design.
        actor.Tell(CreateHydrationTriggerInboundMessage("live 1", "user-1"), TestActor);
        actor.Tell(CreateHydrationTriggerInboundMessage("live 2", "user-1"), TestActor);
        actor.Tell(CreateHydrationTriggerInboundMessage("live 3", "user-1"), TestActor);

        await AwaitAssertAsync(() => Assert.True(pipeline.CapturedInputs.Count >= 4),
            cancellationToken: ct);

        var dataContentCount = pipeline.CapturedInputs
            .Sum(input => input.Contents.OfType<DataContent>().Count());
        Assert.Equal(1, dataContentCount);
    }

    [Fact]
    public async Task Approval_response_replies_with_ack_to_sender()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-approval-ack");
        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new ToolInteractionRequest
            {
                SessionId = sid,
                Kind = "approval",
                CallId = new Netclaw.Tools.ToolCallId("call-ack-1"),
                ToolName = new Netclaw.Tools.ToolName("execute_shell"),
                DisplayText = "touch /tmp/file",
                RequesterSenderId = new SenderId("user-1"),
                Options =
                [
                    new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                    new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                ]
            }
        ]);

        var actor = CreateBindingActor(sid, pipeline, detector);

        await AwaitAssertAsync(() =>
        {
            var texts = GetPostedTexts();
            Assert.Contains(texts, t => t.Contains("execute_shell"));
        }, cancellationToken: ct);

        var probe = CreateTestProbe();
        actor.Tell(CreateApprovalResponse("call-ack-1", ApprovalOptionKeys.ApproveOnce, "user-1"), probe.Ref);

        var ack = await probe.ExpectMsgAsync<CommandAck>(cancellationToken: ct);
        Assert.Equal(sid, ack.SessionId);
    }

    [Fact]
    public async Task Button_approval_from_wrong_requester_replies_with_nack()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-wrong-button-nack");
        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new ToolInteractionRequest
            {
                SessionId = sid,
                Kind = "approval",
                CallId = new Netclaw.Tools.ToolCallId("call-wr-nack-1"),
                ToolName = new Netclaw.Tools.ToolName("execute_shell"),
                DisplayText = "rm -rf /tmp",
                RequesterSenderId = new SenderId("user-A"),
                Options =
                [
                    new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                    new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                ]
            }
        ]);

        var actor = CreateBindingActor(sid, pipeline, detector);
        await AwaitAssertAsync(() =>
        {
            var texts = GetPostedTexts();
            Assert.Contains(texts, t => t.Contains("execute_shell"));
        }, cancellationToken: ct);

        var probe = CreateTestProbe();
        actor.Tell(CreateApprovalResponse("call-wr-nack-1", ApprovalOptionKeys.ApproveOnce, "user-B"), probe.Ref);

        var nack = await probe.ExpectMsgAsync<CommandNack>(cancellationToken: ct);
        Assert.Equal(sid, nack.SessionId);
        Assert.Equal(ApprovalNackReasons.WrongRequester, nack.Reason);
    }

}
