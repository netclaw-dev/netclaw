// -----------------------------------------------------------------------
// <copyright file="SlackSessionBindingContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class SlackSessionBindingContractTests(ITestOutputHelper output)
    : SessionBindingContractTests(output)
{
    private RecordingSlackReplyClient _replyClient = new();
    private int _actorCounter;

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
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
        return CreateActorCore(sessionId, pipeline, detector, nameSuffix: "fail");
    }

    protected override object CreateInboundMessage(string text, string senderId)
        => new SlackThreadInbound(
            SessionId: new SessionId("ignored"),
            ChannelId: new SlackChannelId("C-test"),
            ThreadTs: new SlackThreadTs("1000.1"),
            EventId: new SlackEventId($"evt-{Guid.NewGuid():N}"),
            TurnId: new Netclaw.Actors.Protocol.TurnId(Guid.NewGuid().ToString("N")),
            SenderId: new SenderId(senderId),
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            {
                SourceKind = new Netclaw.Actors.Channels.SourceKind("slack")
            },
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());

    protected override object CreateApprovalResponse(string callId, string selectedKey, string senderId)
        => new SlackApprovalResponse(
            ChannelId: new SlackChannelId("C-test"),
            ThreadTs: new SlackThreadTs("1000.1"),
            CallId: new Netclaw.Tools.ToolCallId(callId),
            SelectedKey: selectedKey,
            SenderId: new SenderId(senderId));

    protected override IReadOnlyList<string> GetPostedTexts()
        => _replyClient.Posts.Select(p => p.Text).ToList();

    protected override void ClearPostedTexts()
        => _replyClient.Clear();

    protected override void SetReplyClientThrows(Exception ex)
        => _replyClient.ThrowOnPost = ex;

    protected override void ClearReplyClientThrows()
        => _replyClient.ThrowOnPost = null;

    protected override ChannelType ExpectedChannelType => ChannelType.Slack;

    protected override bool SupportsThreadHydration => true;

    private int _hydrationEventCounter;

    protected override object CreateHydrationTriggerInboundMessage(string text, string senderId)
        => ((SlackThreadInbound)CreateInboundMessage(text, senderId)) with
        {
            EventId = new SlackEventId($"C-test:{1000 + Interlocked.Increment(ref _hydrationEventCounter)}.1")
        };

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
                SenderId = new SenderId($"user-history-{i}"),
                ChannelId = "C-test",
                MessageId = $"C-test:{900 + i}.1",
                Contents = [new TextContent($"history message {i}")],
                ReceivedAt = TimeProvider.System.GetUtcNow().AddMinutes(-(count - i)),
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.TrustedInstance,
                Principal = PrincipalClassification.UntrustedExternal,
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            });
        }
        return items;
    }

    private void ResetReplyClient()
    {
        var pendingThrow = _replyClient.ThrowOnPost;
        _replyClient = new RecordingSlackReplyClient { ThrowOnPost = pendingThrow };
    }

    private IActorRef CreateActorCore(
        SessionId sessionId,
        ISessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector,
        string nameSuffix = "",
        IThreadHistoryFetcher? historyFetcher = null)
    {
        var paths = TestSlackGatewayDeps.NewTestPaths();
        var deps = new SlackGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            ActorSystem: Sys,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                Enabled = true,
                AllowDirectMessages = true,
                BotToken = new SensitiveString("xoxb-fake")
            },
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            ThreadHistoryFetcher: historyFetcher ?? EmptyThreadHistoryFetcher.Instance,
            AudienceProfiles: TestSlackGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestSlackGatewayDeps.DefaultTextOnlyModel,
            Paths: paths,
            PromptInjectionDetector: detector);

        var suffix = string.IsNullOrEmpty(nameSuffix) ? "" : $"-{nameSuffix}";
        var name = $"slack-thread-contract{suffix}-{Interlocked.Increment(ref _actorCounter)}";
        return Sys.ActorOf(SlackThreadBindingActor.CreateProps(
            sessionId,
            new SlackChannelId("C-test"),
            new SlackThreadTs("1000.1"),
            deps), name);
    }

    // Regression for #939: when the binding has no in-memory pending approval
    // (passivation between prompt and click) the Slack button payload still
    // carries the prompt's message TS. The binding must use it to redraw the
    // original message — otherwise the buttons stay live forever.
    [Fact]
    public async Task Cold_button_approval_response_redraws_prompt_via_payload_messageTs()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-cold-redraw-via-payload");

        // No ToolInteractionRequest emitted, so _pendingApprovalRequests stays empty.
        var pipeline = new RecordingSessionPipeline(_ => []);
        var actor = CreateBindingActor(sid, pipeline, detector);

        var payloadTs = new SlackEventTs("1234567890.000111");
        actor.Tell(new SlackApprovalResponse(
            ChannelId: new SlackChannelId("C-test"),
            ThreadTs: new SlackThreadTs("1000.1"),
            CallId: new Netclaw.Tools.ToolCallId("call-cold-redraw"),
            SelectedKey: ApprovalOptionKeys.Deny,
            SenderId: new SenderId("user-1"),
            RequesterSenderId: null,
            PromptMessageTs: payloadTs));

        await AwaitAssertAsync(() =>
        {
            // Approval routed to session.
            var feedback = pipeline.RecordedFeedback.OfType<ToolInteractionResponse>().ToList();
            Assert.Single(feedback);
            Assert.Equal("call-cold-redraw", feedback[0].CallId.Value);
            Assert.Equal(ApprovalOptionKeys.Deny, feedback[0].SelectedKey.Value);

            // Prompt redrawn using the payload-provided TS, with no action buttons.
            var update = Assert.Single(_replyClient.Updates);
            Assert.Equal(payloadTs, update.MessageTs);
            Assert.Contains("resolved", update.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Denied", update.Text, StringComparison.Ordinal);
            Assert.NotNull(update.Blocks);
            Assert.DoesNotContain(update.Blocks!, b => b is SlackNet.Blocks.ActionsBlock);
        }, cancellationToken: ct);
    }

    // Hot-path regression: when the binding still holds the original request,
    // the redraw uses the captured PromptMessageTs (not the payload), and the
    // resolved block includes verb/location detail from the request.
    [Fact]
    public async Task Hot_button_approval_response_redraws_prompt_via_pending_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-hot-redraw");
        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new ToolInteractionRequest
            {
                SessionId = sid,
                Kind = "approval",
                CallId = new Netclaw.Tools.ToolCallId("call-hot-redraw"),
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
        var actor = CreateBindingActor(sid, pipeline, detector);

        // Wait until the prompt has been posted (and PromptMessageTs is captured).
        await AwaitAssertAsync(() => Assert.Single(_replyClient.Posts), cancellationToken: ct);

        // Send a button click WITHOUT a payload TS — must still redraw because the
        // binding has captured the TS in _pendingApprovalRequests.
        actor.Tell(new SlackApprovalResponse(
            ChannelId: new SlackChannelId("C-test"),
            ThreadTs: new SlackThreadTs("1000.1"),
            CallId: new Netclaw.Tools.ToolCallId("call-hot-redraw"),
            SelectedKey: ApprovalOptionKeys.ApproveOnce,
            SenderId: new SenderId("user-1"),
            RequesterSenderId: null,
            PromptMessageTs: null));

        await AwaitAssertAsync(() =>
        {
            var update = Assert.Single(_replyClient.Updates);
            // RecordingSlackReplyClient's PostThreadReplyWithTsAsync returns "fake.ts".
            Assert.Equal(new SlackEventTs("fake.ts"), update.MessageTs);
            // Full resolved block: includes the verb and tool name from the request.
            Assert.Contains("shell_execute", update.Text, StringComparison.Ordinal);
            Assert.Contains("git status", update.Text, StringComparison.Ordinal);
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Recovered_text_approval_response_redraws_prompt_via_persisted_messageTs()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-recovered-text-redraw");

        var initialPipeline = new RecordingSessionPipeline(_ =>
        [
            new ToolInteractionRequest
            {
                SessionId = sid,
                Kind = "approval",
                CallId = new Netclaw.Tools.ToolCallId("call-recovered-text-redraw"),
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

        var stopProbe = CreateTestProbe("slack-recovered-text-stop");
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
            Assert.Equal("call-recovered-text-redraw", feedback[0].CallId.Value);
            Assert.Equal(ApprovalOptionKeys.ApproveOnce, feedback[0].SelectedKey.Value);

            var update = Assert.Single(_replyClient.Updates);
            Assert.Equal(new SlackEventTs("fake.ts"), update.MessageTs);
            Assert.Contains("resolved", update.Text, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(update.Blocks);
            Assert.DoesNotContain(update.Blocks!, block => block is SlackNet.Blocks.ActionsBlock);

            // The cold-spawn redraw now pulls the tool name + display text out of
            // the persisted PendingApprovalPromptTracked record, so the resolved
            // banner regains the Tool/Request detail the hot path renders. This
            // pins the wire-level guarantee end-to-end: the binding cold-recovers
            // those fields from its journal and threads them into the builder.
            Assert.Contains("shell_execute", update.Text, StringComparison.Ordinal);
            Assert.Contains("git status", update.Text, StringComparison.Ordinal);
        }, cancellationToken: ct);
    }

    // Code-review regression (#939): on the cold-spawn path, a non-requester
    // click MUST NOT redraw the prompt. The session is the authority on whether
    // the sender is allowed; the binding awaits the result before touching the
    // UI. A CommandNack with WrongRequester surfaces the warning text but the
    // buttons stay live for the legitimate requester.
    [Fact]
    public async Task Cold_button_approval_response_skips_redraw_on_wrong_requester_nack()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-cold-wrong-requester");

        var pipeline = new RecordingSessionPipeline(_ => [])
        {
            ResponseFactory = (_, _) =>
                Task.FromResult<ICommandReply>(CommandNack.For(sid, ApprovalNackReasons.WrongRequester))
        };
        var actor = CreateBindingActor(sid, pipeline, detector);

        actor.Tell(new SlackApprovalResponse(
            ChannelId: new SlackChannelId("C-test"),
            ThreadTs: new SlackThreadTs("1000.1"),
            CallId: new Netclaw.Tools.ToolCallId("call-cold-wrong-requester"),
            SelectedKey: ApprovalOptionKeys.ApproveOnce,
            SenderId: new SenderId("attacker"),
            RequesterSenderId: null,
            PromptMessageTs: new SlackEventTs("1234567890.000222")));

        // Wait for the session interaction + the local wrong-requester warning post.
        await AwaitAssertAsync(() =>
        {
            Assert.Single(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>());
            // The wrong-requester warning is posted as a new thread reply.
            Assert.Contains(_replyClient.Posts, p => p.Text.Contains("Only the requesting user", StringComparison.Ordinal));
        }, cancellationToken: ct);

        // Crucially: no UpdateThreadMessageAsync — the original prompt is untouched.
        Assert.Empty(_replyClient.Updates);
    }

    // Code-review regression (#939): a stale re-click on an already-resolved
    // prompt MUST NOT overwrite the resolution banner with a new (rejected)
    // decision. Session Nack on any reason (not just WrongRequester) skips the
    // redraw.
    [Fact]
    public async Task Cold_button_approval_response_skips_redraw_on_stale_call_nack()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-cold-stale-click");

        var pipeline = new RecordingSessionPipeline(_ => [])
        {
            ResponseFactory = (_, _) =>
                Task.FromResult<ICommandReply>(CommandNack.For(sid, "no_pending_call"))
        };
        var actor = CreateBindingActor(sid, pipeline, detector);

        actor.Tell(new SlackApprovalResponse(
            ChannelId: new SlackChannelId("C-test"),
            ThreadTs: new SlackThreadTs("1000.1"),
            CallId: new Netclaw.Tools.ToolCallId("call-stale"),
            SelectedKey: ApprovalOptionKeys.Deny,
            SenderId: new SenderId("user-1"),
            RequesterSenderId: null,
            PromptMessageTs: new SlackEventTs("1234567890.000333")));

        await AwaitAssertAsync(
            () => Assert.Single(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>()),
            cancellationToken: ct);

        Assert.Empty(_replyClient.Updates);
    }
}
