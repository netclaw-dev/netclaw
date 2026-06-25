// -----------------------------------------------------------------------
// <copyright file="DiscordSessionBindingContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Discord;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class DiscordSessionBindingContractTests(ITestOutputHelper output)
    : SessionBindingContractTests(output)
{
    private RecordingDiscordReplyClient _replyClient = new();

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
        var options = new DiscordChannelOptions();
        var deps = new DiscordGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: options,
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.DiscordWithProcessingRenderer(_replyClient),
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            Paths: TestDiscordGatewayDeps.NewTestPaths(),
            PromptInjectionDetector: detector);

        return Sys.ActorOf(DiscordSessionBindingActor.CreateProps(
            sessionId,
            new DiscordChannelId("ch-test"),
            new DiscordReplyChannelId("reply-test"),
            new DiscordThreadOrMessageId("thread-test"),
            rootMessageId: null,
            deps));
    }

    protected override object CreateInboundMessage(string text, string senderId)
        => new DiscordThreadInbound(
            SessionId: new SessionId("ignored"),
            ChannelId: new DiscordChannelId("ch-test"),
            ReplyChannelId: new DiscordReplyChannelId("reply-test"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-test"),
            RootMessageId: null,
            EventId: new DiscordEventId($"evt-{Guid.NewGuid():N}"),
            SenderId: new DiscordUserId(senderId),
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            {
                SourceKind = new Netclaw.Actors.Channels.SourceKind("discord")
            },
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());

    protected override object CreateApprovalResponse(string callId, string selectedKey, string senderId)
        => new DiscordApprovalResponse(
            ChannelId: new DiscordChannelId("ch-test"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-test"),
            CallId: new Netclaw.Tools.ToolCallId(callId),
            SelectedKey: selectedKey,
            SenderId: new DiscordUserId(senderId));

    protected override IReadOnlyList<string> GetPostedTexts()
        => _replyClient.Posts.Select(p => p.Text).ToList();

    protected override void ClearPostedTexts()
        => _replyClient.Posts.Clear();

    protected override void SetReplyClientThrows(Exception ex)
        => _replyClient.ThrowOnPost = ex;

    protected override void SetReplyClientThrowsOnce(Exception ex)
        => _replyClient.ThrowOnceOnPost = ex;

    protected override void ClearReplyClientThrows()
        => _replyClient.ThrowOnPost = null;

    protected override ChannelType ExpectedChannelType => ChannelType.Discord;

    protected override bool SupportsThreadHydration => true;

    protected override IActorRef CreateBindingActorWithHydration(
        SessionId sessionId,
        RecordingSessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector,
        IThreadHistoryFetcher historyFetcher)
    {
        ResetReplyClient();
        return CreateActorCore(sessionId, pipeline, detector, historyFetcher);
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
                MessageId = (900_000_000_000_000_000UL + (ulong)i).ToString(),
                Contents = [new Microsoft.Extensions.AI.TextContent($"history message {i}")],
                ReceivedAt = TimeProvider.System.GetUtcNow().AddMinutes(-count + i),
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.Public,
                Principal = PrincipalClassification.UntrustedExternal,
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            });
        }

        return items;
    }

    private long _hydrationEventCounter;

    protected override object CreateHydrationTriggerInboundMessage(string text, string senderId)
    {
        var snowflake = 1_000_000_000_000_000_000UL + (ulong)Interlocked.Increment(ref _hydrationEventCounter);
        return new DiscordThreadInbound(
            SessionId: new SessionId("ignored"),
            ChannelId: new DiscordChannelId("ch-test"),
            ReplyChannelId: new DiscordReplyChannelId("reply-test"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-test"),
            RootMessageId: null,
            EventId: new DiscordEventId(snowflake.ToString()),
            SenderId: new DiscordUserId(senderId),
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            {
                SourceKind = new Netclaw.Actors.Channels.SourceKind("discord")
            },
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());
    }

    private void ResetReplyClient()
    {
        var pendingThrow = _replyClient.ThrowOnPost;
        var pendingUploadThrow = _replyClient.ThrowOnUpload;
        _replyClient = new RecordingDiscordReplyClient
        {
            ThrowOnPost = pendingThrow,
            ThrowOnUpload = pendingUploadThrow
        };
    }

    private IActorRef CreateActorCore(
        SessionId sessionId,
        ISessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector,
        IThreadHistoryFetcher? historyFetcher = null,
        DiscordChannelOptions? options = null)
    {
        var deps = new DiscordGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: options ?? new DiscordChannelOptions(),
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.DiscordWithProcessingRenderer(_replyClient),
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            Paths: TestDiscordGatewayDeps.NewTestPaths(),
            PromptInjectionDetector: detector,
            ThreadHistoryFetcher: historyFetcher);

        return Sys.ActorOf(DiscordSessionBindingActor.CreateProps(
            sessionId,
            new DiscordChannelId("ch-test"),
            new DiscordReplyChannelId("reply-test"),
            new DiscordThreadOrMessageId("thread-test"),
            rootMessageId: null,
            deps));
    }

    [Fact]
    public async Task FileOutput_uploads_file_to_discord_reply_channel()
    {
        var ct = TestContext.Current.CancellationToken;
        var sid = new SessionId("session-discord-file-output");
        var paths = TestDiscordGatewayDeps.NewTestPaths();
        var filePath = Path.Combine(paths.BasePath, $"discord-upload-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(filePath, "hello discord", ct);

        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new FileOutput
            {
                SessionId = sid,
                FilePath = filePath,
                FileName = "report.txt",
                MimeType = new Netclaw.Media.MimeType("text/plain")
            },
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(1) }
        ]);

        CreateActorCore(sid, pipeline, new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe()));

        await AwaitAssertAsync(() =>
        {
            var upload = Assert.Single(_replyClient.Uploads);
            Assert.Equal("reply-test", upload.ReplyChannelId.Value);
            Assert.Equal(filePath, upload.FilePath);
            Assert.Equal("report.txt", upload.FileName);
            Assert.Contains("report.txt", upload.Text, StringComparison.Ordinal);
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Deferred_hydration_completes_on_first_authorized_inbound()
    {
        // Discord parity for the proactive-thread deferral case: the binding
        // actor's startup hydration sees only a non-authorized bot root, finds
        // no authorized trigger, defers, and re-arms. The first authorized
        // inbound completes the deferred hydration and adopts the bot root.
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-discord-deferred-hydration");

        const string botRootText = "scheduled report: nothing actionable";
        var botRoot = MakeHistoryItem("bot-account", "900000000000000000", botRootText);
        var reply = MakeHistoryItem("replier-user", "1000000000000000001", "same as yesterday?");

        // Fetch #1 (startup hydration) sees only the bot root; fetch #2 (the
        // re-armed hydration) sees the root plus the reply.
        var fetcher = new CountingHistoryFetcher(call =>
            call >= 2 ? [botRoot, reply] : [botRoot]);

        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new TextOutput("reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1) }
        ]);

        // The bot ("bot-account") is not an allowed user, so the proactively
        // posted root classifies as pending and startup hydration defers.
        var actor = CreateActorCore(
            sid, pipeline, detector, fetcher,
            new DiscordChannelOptions { AllowedUserIds = ["replier-user"] });

        await AwaitAssertAsync(() => Assert.Equal(1, fetcher.FetchCount), cancellationToken: ct);

        actor.Tell(MakeInbound("1000000000000000001", "replier-user", "same as yesterday?"), TestActor);

        await AwaitAssertAsync(() =>
        {
            Assert.True(pipeline.CapturedInputs.TryPeek(out var input));
            Assert.True(input.HasAdoptedContext);
            var text = string.Join("\n", input.Contents
                .OfType<Microsoft.Extensions.AI.TextContent>()
                .Select(t => t.Text));
            Assert.Contains("[adopted-context]", text, StringComparison.Ordinal);
            Assert.Contains(botRootText, text, StringComparison.Ordinal);
            Assert.Contains("same as yesterday?", text, StringComparison.Ordinal);
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Deferred_hydration_stays_armed_for_unauthorized_inbound()
    {
        // An inbound from a non-allowed user while hydration is deferred must
        // not perform the deferred hydration; it stays re-armed for the first
        // authorized inbound.
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-discord-deferred-unauthorized");

        var botRoot = MakeHistoryItem("bot-account", "900000000000000000", "scheduled report");
        var reply = MakeHistoryItem("replier-user", "1000000000000000002", "authorized reply");
        var fetcher = new CountingHistoryFetcher(call =>
            call >= 2 ? [botRoot, reply] : [botRoot]);

        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new TextOutput("reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1) }
        ]);
        var actor = CreateActorCore(
            sid, pipeline, detector, fetcher,
            new DiscordChannelOptions { AllowedUserIds = ["replier-user"] });

        await AwaitAssertAsync(() => Assert.Equal(1, fetcher.FetchCount), cancellationToken: ct);

        // Unauthorized inbound: no deferred hydration, no re-fetch.
        actor.Tell(MakeInbound("1000000000000000001", "intruder-user", "who are you"), TestActor);
        await AwaitAssertAsync(
            () => Assert.True(pipeline.CapturedInputs.Count >= 1), cancellationToken: ct);
        Assert.Equal(1, fetcher.FetchCount);

        // The first authorized inbound completes the still-armed hydration.
        actor.Tell(MakeInbound("1000000000000000002", "replier-user", "authorized reply"), TestActor);
        await AwaitAssertAsync(() => Assert.Equal(2, fetcher.FetchCount), cancellationToken: ct);
    }

    [Fact]
    public async Task Deferred_hydration_fetch_failure_executes_turn_and_stays_armed()
    {
        // If the re-armed thread-history fetch fails, the authorized inbound
        // still executes (without an adopted window) and hydration stays
        // re-armed so a later authorized inbound retries.
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-discord-deferred-fetch-failure");

        var botRoot = MakeHistoryItem("bot-account", "900000000000000000", "scheduled report");
        var reply = MakeHistoryItem("replier-user", "1000000000000000003", "later reply");
        var fetcher = new CountingHistoryFetcher(call => call switch
        {
            1 => [botRoot],
            2 => throw new InvalidOperationException("history API down"),
            _ => [botRoot, reply]
        });

        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new TextOutput("reply") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1) }
        ]);
        var actor = CreateActorCore(
            sid, pipeline, detector, fetcher,
            new DiscordChannelOptions { AllowedUserIds = ["replier-user"] });

        await AwaitAssertAsync(() => Assert.Equal(1, fetcher.FetchCount), cancellationToken: ct);

        // Re-armed fetch throws: the turn still executes without adopted context.
        actor.Tell(MakeInbound("1000000000000000001", "replier-user", "first reply"), TestActor);
        await AwaitAssertAsync(() =>
        {
            Assert.True(pipeline.CapturedInputs.TryPeek(out var input));
            Assert.False(input.HasAdoptedContext);
        }, cancellationToken: ct);

        // Still re-armed: a later authorized inbound retries the fetch.
        actor.Tell(MakeInbound("1000000000000000003", "replier-user", "later reply"), TestActor);
        await AwaitAssertAsync(() => Assert.Equal(3, fetcher.FetchCount), cancellationToken: ct);
    }

    [Fact]
    public async Task Proactive_thread_can_apply_session_title_updates()
    {
        var ct = TestContext.Current.CancellationToken;
        var sid = new SessionId("session-discord-proactive-title");
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var pipeline = new RecordingSessionPipeline(_ =>
        [
            new TextOutput("hello back") { SessionId = sid },
            new SessionTitleOutput("Release Readout") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1) }
        ], reactive: true);

        var actor = CreateActorCore(sid, pipeline, detector);

        actor.Tell(new StartProactiveThread(
            new DiscordChannelId("ch-test"),
            new DiscordReplyChannelId("reply-test"),
            new DiscordThreadOrMessageId("thread-test"),
            sid), TestActor);

        await ExpectMsgAsync<ProactiveThreadAck>(cancellationToken: ct);

        actor.Tell(MakeInbound("1000000000000000004", "replier-user", "what changed?"), TestActor);

        await AwaitAssertAsync(() =>
        {
            Assert.Single(_replyClient.ThreadRenames);
            Assert.Equal("reply-test", _replyClient.ThreadRenames[0].ThreadId.Value);
            Assert.Equal("Release Readout", _replyClient.ThreadRenames[0].Name);
        }, cancellationToken: ct);
    }

    private static ChannelInput MakeHistoryItem(string senderId, string messageId, string text)
        => new()
        {
            SenderId = new SenderId(senderId),
            ChannelId = "ch-test",
            MessageId = messageId,
            Contents = [new Microsoft.Extensions.AI.TextContent(text)],
            ReceivedAt = TimeProvider.System.GetUtcNow(),
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Public,
            Principal = PrincipalClassification.UntrustedExternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
        };

    private static DiscordThreadInbound MakeInbound(string eventId, string senderId, string text)
        => new(
            SessionId: new SessionId("ignored"),
            ChannelId: new DiscordChannelId("ch-test"),
            ReplyChannelId: new DiscordReplyChannelId("reply-test"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-test"),
            RootMessageId: null,
            EventId: new DiscordEventId(eventId),
            SenderId: new DiscordUserId(senderId),
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
            {
                SourceKind = new Netclaw.Actors.Channels.SourceKind("discord")
            },
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());

    /// <summary>
    /// History fetcher that returns a different result per call, keyed on the
    /// 1-based call index — lets a test stage the proactive deferral (fetch #1)
    /// separately from the re-armed completion (fetch #2). A <c>byCall</c> that
    /// throws simulates a transient history-API failure.
    /// </summary>
    private sealed class CountingHistoryFetcher(Func<int, IReadOnlyList<ChannelInput>> byCall)
        : IThreadHistoryFetcher
    {
        private int _count;

        public int FetchCount => Volatile.Read(ref _count);

        public async Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
            SessionId sessionId, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _count);
            await Task.Yield();
            return byCall(call);
        }
    }

    // Regression for #939: cold-spawn redraw via the Discord button-interaction
    // payload's message ID. When the binding has no in-memory pending approval
    // (passivation), the binding must still update the prompt message using the
    // payload-provided ID.
    [Fact]
    public async Task Cold_button_approval_response_redraws_prompt_via_payload_messageId()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-discord-cold-redraw");

        var pipeline = new RecordingSessionPipeline(_ => []);
        var actor = CreateBindingActor(sid, pipeline, detector);

        var payloadMessageId = new DiscordMessageId("987654321098765432");
        actor.Tell(new DiscordApprovalResponse(
            ChannelId: new DiscordChannelId("ch-test"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-test"),
            CallId: new Netclaw.Tools.ToolCallId("call-discord-cold-redraw"),
            SelectedKey: ApprovalOptionKeys.Deny,
            SenderId: new DiscordUserId("user-1"),
            RequesterSenderId: null,
            PromptMessageId: payloadMessageId));

        await AwaitAssertAsync(() =>
        {
            var feedback = pipeline.RecordedFeedback.OfType<ToolInteractionResponse>().ToList();
            Assert.Single(feedback);
            Assert.Equal("call-discord-cold-redraw", feedback[0].CallId.Value);

            var update = Assert.Single(_replyClient.Updates);
            Assert.Equal(payloadMessageId, update.MessageId);
            Assert.True(update.RemoveComponents);
            Assert.Contains("resolved", update.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Denied", update.Text, StringComparison.Ordinal);
        }, cancellationToken: ct);
    }

    [Fact]
    public async Task Recovered_text_approval_response_redraws_prompt_via_persisted_messageId()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-discord-recovered-text-redraw");

        var initialPipeline = new RecordingSessionPipeline(_ =>
        [
            new ToolInteractionRequest
            {
                SessionId = sid,
                Kind = "approval",
                CallId = new Netclaw.Tools.ToolCallId("call-discord-recovered-text-redraw"),
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

        var stopProbe = CreateTestProbe("discord-recovered-text-stop");
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
            Assert.Equal("call-discord-recovered-text-redraw", feedback[0].CallId.Value);
            Assert.Equal(ApprovalOptionKeys.ApproveOnce, feedback[0].SelectedKey.Value);

            var update = Assert.Single(_replyClient.Updates);
            Assert.Equal(new DiscordMessageId("msg-1"), update.MessageId);
            Assert.True(update.RemoveComponents);
            Assert.Contains("resolved", update.Text, StringComparison.OrdinalIgnoreCase);

            // PendingApprovalPromptTracked now carries the tool name + display
            // text through cold recovery, so the resolved message regains the
            // Tool/Action detail the hot path renders. End-to-end guarantee
            // that the binding journals and rehydrates the fields.
            Assert.Contains("shell_execute", update.Text, StringComparison.Ordinal);
            Assert.Contains("git status", update.Text, StringComparison.Ordinal);
        }, cancellationToken: ct);
    }

    // Code-review regression (#939): a non-requester click on a cold-spawned
    // binding MUST NOT redraw. Session WrongRequester nack surfaces the warning
    // and leaves the prompt UI intact.
    [Fact]
    public async Task Cold_button_approval_response_skips_redraw_on_wrong_requester_nack()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-discord-cold-wrong-requester");

        var pipeline = new RecordingSessionPipeline(_ => [])
        {
            ResponseFactory = (_, _) =>
                Task.FromResult<ISessionResponse>(CommandNack.For(sid, ApprovalNackReasons.WrongRequester))
        };
        var actor = CreateBindingActor(sid, pipeline, detector);

        actor.Tell(new DiscordApprovalResponse(
            ChannelId: new DiscordChannelId("ch-test"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-test"),
            CallId: new Netclaw.Tools.ToolCallId("call-discord-cold-wrong-requester"),
            SelectedKey: ApprovalOptionKeys.ApproveOnce,
            SenderId: new DiscordUserId("attacker"),
            RequesterSenderId: null,
            PromptMessageId: new DiscordMessageId("987654321098765432")));

        await AwaitAssertAsync(() =>
        {
            Assert.Single(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>());
            Assert.Contains(_replyClient.Posts, p => p.Text.Contains("Only the requesting user", StringComparison.Ordinal));
        }, cancellationToken: ct);

        Assert.Empty(_replyClient.Updates);
    }

    // Code-review regression (#939): stale re-click after resolution.
    [Fact]
    public async Task Cold_button_approval_response_skips_redraw_on_stale_call_nack()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = new ConfigurablePromptInjectionDetector(PromptInjectionResult.Safe());
        var sid = new SessionId("session-discord-cold-stale");

        var pipeline = new RecordingSessionPipeline(_ => [])
        {
            ResponseFactory = (_, _) =>
                Task.FromResult<ISessionResponse>(CommandNack.For(sid, "no_pending_call"))
        };
        var actor = CreateBindingActor(sid, pipeline, detector);

        actor.Tell(new DiscordApprovalResponse(
            ChannelId: new DiscordChannelId("ch-test"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-test"),
            CallId: new Netclaw.Tools.ToolCallId("call-discord-stale"),
            SelectedKey: ApprovalOptionKeys.Deny,
            SenderId: new DiscordUserId("user-1"),
            RequesterSenderId: null,
            PromptMessageId: new DiscordMessageId("987654321098765999")));

        await AwaitAssertAsync(
            () => Assert.Single(pipeline.RecordedFeedback.OfType<ToolInteractionResponse>()),
            cancellationToken: ct);

        Assert.Empty(_replyClient.Updates);
    }
}
