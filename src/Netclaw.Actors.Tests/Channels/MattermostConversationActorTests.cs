// -----------------------------------------------------------------------
// <copyright file="MattermostConversationActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Mattermost;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostConversationActorTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override Config? Config =>
        ConfigurationFactory.ParseString("akka.test.default-timeout = 5s");

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Routes_messages_to_session_binding_by_thread_id()
    {
        var sink = CreateTestProbe("route-by-thread");
        var conversation = CreateConversation("ch-1", sink);

        conversation.Tell(CreateMessage(channelId: "ch-1", rootPostId: "root-42", text: "hello"));

        var inbound = await sink.ExpectMsgAsync<MattermostThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-1/root-42", inbound.SessionId.Value);
        Assert.Equal("hello", inbound.Text);
    }

    [Fact]
    public async Task Creates_new_session_binding_for_top_level_messages()
    {
        var sink = CreateTestProbe("top-level");
        var conversation = CreateConversation("ch-1", sink);

        // Top-level message has empty RootPostId, so PostId becomes the root
        conversation.Tell(CreateMessage(
            channelId: "ch-1", postId: "post-100", rootPostId: "", text: "new conversation"));

        var inbound = await sink.ExpectMsgAsync<MattermostThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-1/post-100", inbound.SessionId.Value);
    }

    [Fact]
    public async Task Reuses_existing_session_binding_for_same_thread()
    {
        var sink = CreateTestProbe("same-thread");
        var conversation = CreateConversation("ch-1", sink);

        conversation.Tell(CreateMessage(
            channelId: "ch-1", rootPostId: "root-42", text: "first"));
        var first = await sink.ExpectMsgAsync<MattermostThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);

        conversation.Tell(CreateMessage(
            channelId: "ch-1", rootPostId: "root-42", text: "second", eventId: "ev-2"));
        var second = await sink.ExpectMsgAsync<MattermostThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(first.SessionId, second.SessionId);
    }

    [Fact]
    public async Task Filters_bot_messages()
    {
        var sink = CreateTestProbe("bot-filter");
        var conversation = CreateConversation("ch-1", sink);

        conversation.Tell(new MattermostGatewayMessage(
            EventId: new MattermostEventId("ev-bot"),
            ChannelId: new MattermostChannelId("ch-1"),
            PostId: new MattermostPostId("p-bot"),
            RootPostId: new MattermostRootPostId("p-bot"),
            SenderId: new MattermostUserId("u-bot"),
            IsBotMessage: true,
            IsDirectMessage: false,
            ContainsBotMention: false,
            Text: "bot output",
            ReceivedAt: TimeProvider.System.GetUtcNow()));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filters_empty_text_messages()
    {
        var sink = CreateTestProbe("empty-text");
        var conversation = CreateConversation("ch-1", sink);

        conversation.Tell(CreateMessage(channelId: "ch-1", text: "   "));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Truncates_oversized_inbound_text()
    {
        var sink = CreateTestProbe("truncate");
        var conversation = CreateConversation("ch-1", sink);

        var longText = new string('x', 5000);
        conversation.Tell(CreateMessage(channelId: "ch-1", text: longText));

        var inbound = await sink.ExpectMsgAsync<MattermostThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4000, inbound.Text.Length);
    }

    [Fact]
    public async Task Enforces_ACL_denies_non_allowed_users()
    {
        var sink = CreateTestProbe("acl-user-denied");
        var options = new MattermostChannelOptions
        {
            Enabled = true,
            AllowDirectMessages = true,
            MentionOnly = false,
            AllowedChannelIds = ["ch-1"],
            AllowedUserIds = ["u-allowed"]
        };
        var conversation = CreateConversation("ch-1", sink, options);

        conversation.Tell(CreateMessage(
            channelId: "ch-1", senderId: "u-denied", text: "should be denied"));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Enforces_ACL_denies_non_allowed_channels()
    {
        var sink = CreateTestProbe("acl-channel-denied");
        // ch-99 is not in AllowedChannelIds
        var conversation = CreateConversation("ch-99", sink);

        conversation.Tell(CreateMessage(channelId: "ch-99", text: "should be denied"));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Enforces_ACL_denies_DMs_when_disabled()
    {
        var sink = CreateTestProbe("dm-denied");
        var options = new MattermostChannelOptions
        {
            Enabled = true,
            AllowDirectMessages = false,
            MentionOnly = false,
            AllowedChannelIds = ["ch-1"]
        };
        var conversation = CreateConversation("ch-1", sink, options);

        conversation.Tell(new MattermostGatewayMessage(
            EventId: new MattermostEventId("ev-dm"),
            ChannelId: new MattermostChannelId("ch-1"),
            PostId: new MattermostPostId("p-dm"),
            RootPostId: new MattermostRootPostId(""),
            SenderId: new MattermostUserId("u-1"),
            IsBotMessage: false,
            IsDirectMessage: true,
            ContainsBotMention: false,
            Text: "hi from DM",
            ReceivedAt: TimeProvider.System.GetUtcNow()));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Enforces_routing_policy_mention_only()
    {
        var sink = CreateTestProbe("mention-filter");
        var options = new MattermostChannelOptions
        {
            Enabled = true,
            MentionOnly = true,
            AllowedChannelIds = ["ch-1"]
        };
        var conversation = CreateConversation("ch-1", sink, options);

        // Top-level message without mention and no existing thread
        conversation.Tell(CreateMessage(
            channelId: "ch-1", postId: "p-1", rootPostId: "",
            text: "no mention here", containsBotMention: false));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MentionOnly_allows_mention_messages()
    {
        var sink = CreateTestProbe("mention-allow");
        var options = new MattermostChannelOptions
        {
            Enabled = true,
            MentionOnly = true,
            AllowedChannelIds = ["ch-1"]
        };
        var conversation = CreateConversation("ch-1", sink, options, botUsername: "netclaw");

        conversation.Tell(CreateMessage(
            channelId: "ch-1", postId: "p-1", rootPostId: "",
            text: "@netclaw hello", containsBotMention: true));

        var inbound = await sink.ExpectMsgAsync<MattermostThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("hello", inbound.Text);
    }

    [Fact]
    public async Task MentionOnly_allows_existing_thread_without_mention()
    {
        var sink = CreateTestProbe("mention-thread-continue");
        var options = new MattermostChannelOptions
        {
            Enabled = true,
            MentionOnly = true,
            AllowedChannelIds = ["ch-1"]
        };
        var conversation = CreateConversation("ch-1", sink, options, botUsername: "netclaw");

        // Start thread with mention
        conversation.Tell(CreateMessage(
            channelId: "ch-1", rootPostId: "root-1",
            text: "@netclaw start", containsBotMention: true));
        await sink.ExpectMsgAsync<MattermostThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);

        // Follow-up in same thread without mention (ContinueOnly)
        conversation.Tell(CreateMessage(
            channelId: "ch-1", rootPostId: "root-1",
            text: "follow up without mention", eventId: "ev-2",
            containsBotMention: false));

        var second = await sink.ExpectMsgAsync<MattermostThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("follow up without mention", second.Text);
    }

    [Fact]
    public async Task Strips_bot_mention_tag_from_text()
    {
        var sink = CreateTestProbe("mention-strip");
        var conversation = CreateConversation("ch-1", sink, botUsername: "netclaw");

        conversation.Tell(CreateMessage(
            channelId: "ch-1", text: "@netclaw what is the weather?",
            containsBotMention: true));

        var inbound = await sink.ExpectMsgAsync<MattermostThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("what is the weather?", inbound.Text);
    }

    [Fact]
    public async Task Routes_interactions_to_correct_session_binding()
    {
        var sink = CreateTestProbe("interaction-route");
        var conversation = CreateConversation("ch-1", sink);

        // Create the session binding with a threaded message
        conversation.Tell(CreateMessage(
            channelId: "ch-1", rootPostId: "root-500", text: "start"));
        await sink.ExpectMsgAsync<MattermostThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);

        // Send an interaction using the same root post ID
        conversation.Tell(new MattermostGatewayInteraction(
            ChannelId: new MattermostChannelId("ch-1"),
            RootPostId: new MattermostRootPostId("root-500"),
            CallId: "call-1",
            SelectedKey: ApprovalOptionKeys.ApproveOnce,
            SenderId: new MattermostUserId("u-1"),
            RequesterSenderId: new MattermostUserId("u-1"),
            ReceivedAt: TimeProvider.System.GetUtcNow()));

        var approval = await sink.ExpectMsgAsync<MattermostApprovalResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("call-1", approval.CallId.Value);
    }

    [Fact]
    public async Task Rejects_interactions_for_missing_session_bindings()
    {
        var sink = CreateTestProbe("interaction-missing");
        var conversation = CreateConversation("ch-1", sink);

        conversation.Tell(new MattermostGatewayInteraction(
            ChannelId: new MattermostChannelId("ch-1"),
            RootPostId: new MattermostRootPostId("nonexistent"),
            CallId: "call-1",
            SelectedKey: ApprovalOptionKeys.ApproveOnce,
            SenderId: new MattermostUserId("u-1"),
            RequesterSenderId: null,
            ReceivedAt: TimeProvider.System.GetUtcNow()));

        var approval = await sink.ExpectMsgAsync<MattermostApprovalResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("call-1", approval.CallId.Value);
    }

    [Fact]
    public async Task Rejects_interactions_from_non_allowed_users()
    {
        var sink = CreateTestProbe("interaction-user-denied");
        var options = new MattermostChannelOptions
        {
            Enabled = true,
            AllowDirectMessages = true,
            MentionOnly = false,
            AllowedChannelIds = ["ch-1"],
            AllowedUserIds = ["u-allowed"]
        };
        var conversation = CreateConversation("ch-1", sink, options);

        // Create the session binding first (from allowed user)
        conversation.Tell(CreateMessage(
            channelId: "ch-1", rootPostId: "root-600", text: "setup",
            senderId: "u-allowed"));
        await sink.ExpectMsgAsync<MattermostThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);

        // Send interaction from non-allowed user
        conversation.Tell(new MattermostGatewayInteraction(
            ChannelId: new MattermostChannelId("ch-1"),
            RootPostId: new MattermostRootPostId("root-600"),
            CallId: "call-1",
            SelectedKey: ApprovalOptionKeys.ApproveOnce,
            SenderId: new MattermostUserId("u-denied"),
            RequesterSenderId: null,
            ReceivedAt: TimeProvider.System.GetUtcNow()));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeliverTrustedSessionTurn_routes_to_existing_session()
    {
        var sink = CreateTestProbe("trusted-turn");
        var conversation = CreateConversation("ch-1", sink);

        // Create a session binding first
        conversation.Tell(CreateMessage(channelId: "ch-1", rootPostId: "root-50", text: "setup"));
        await sink.ExpectMsgAsync<MattermostThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);

        // Deliver trusted turn
        conversation.Tell(new DeliverTrustedSessionTurn(
            SessionId: new SessionId("ch-1/root-50"),
            Content: "reminder content",
            Source: CreateReminderSource()));

        var forwarded = await sink.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-1/root-50", forwarded.SessionId.Value);
    }

    [Fact]
    public async Task DeliverTrustedSessionTurn_recreates_passivated_binding()
    {
        var sink = CreateTestProbe("trusted-turn-recreate");
        var conversation = CreateConversation("ch-1", sink);

        // Deliver trusted turn WITHOUT an existing session binding — should re-create
        conversation.Tell(new DeliverTrustedSessionTurn(
            SessionId: new SessionId("ch-1/root-99"),
            Content: "reminder for passivated session",
            Source: CreateReminderSource()));

        var forwarded = await sink.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-1/root-99", forwarded.SessionId.Value);
    }

    [Fact]
    public async Task DeliverTrustedSessionTurn_nacks_channel_mismatch()
    {
        var sink = CreateTestProbe("trusted-turn-mismatch");
        var conversation = CreateConversation("ch-1", sink);

        var probe = CreateTestProbe("nack-receiver");
        conversation.Tell(
            new DeliverTrustedSessionTurn(
                SessionId: new SessionId("ch-99/root-50"),
                Content: "wrong channel",
                Source: CreateReminderSource()),
            probe.Ref);

        var nack = await probe.ExpectMsgAsync<CommandNack>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("mismatch", nack.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ingress_gate_blocks_messages()
    {
        var sink = CreateTestProbe("ingress-gate");
        var gate = new SessionIngressGate();
        gate.TryClose("test-drain");
        var conversation = CreateConversation("ch-1", sink, ingressGate: gate);

        conversation.Tell(CreateMessage(channelId: "ch-1", text: "should be blocked"));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Ingress_gate_posts_drain_reply()
    {
        var replyClient = new RecordingMattermostReplyClient();
        var gate = new SessionIngressGate();
        gate.TryClose("restarting");

        var deps = CreateDependencies(
            ingressGate: gate,
            replyClient: replyClient,
            sessionPropsFactory: (_, _, _, _) =>
                Props.Create(() => new ForwardActor(TestActor)));

        var conversation = Sys.ActorOf(
            MattermostConversationActor.CreateProps(new MattermostChannelId("ch-1"), deps),
            $"conv-gate-reply-{Guid.NewGuid():N}");

        conversation.Tell(CreateMessage(channelId: "ch-1", text: "blocked"));

        await AwaitAssertAsync(() =>
        {
            Assert.Single(replyClient.Posts);
            Assert.Contains("restarting", replyClient.Posts[0].Text, StringComparison.OrdinalIgnoreCase);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Ingress_gate_reply_failure_does_not_crash_actor()
    {
        var replyClient = new RecordingMattermostReplyClient
        {
            ThrowOnPost = new InvalidOperationException("API down")
        };
        var gate = new SessionIngressGate();
        gate.TryClose("restarting");

        var deps = CreateDependencies(
            ingressGate: gate,
            replyClient: replyClient,
            sessionPropsFactory: (_, _, _, _) =>
                Props.Create(() => new ForwardActor(TestActor)));

        var conversation = Sys.ActorOf(
            MattermostConversationActor.CreateProps(new MattermostChannelId("ch-1"), deps),
            $"conv-gate-fail-{Guid.NewGuid():N}");

        conversation.Tell(CreateMessage(channelId: "ch-1", text: "blocked"));

        // Actor should survive the reply failure
        var probe = CreateTestProbe();
        probe.Watch(conversation);

        await AwaitAssertAsync(() =>
        {
            Assert.False(probe.HasMessages, "Actor should not have terminated");
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private IActorRef CreateConversation(
        string channelId,
        Akka.TestKit.TestProbe sink,
        MattermostChannelOptions? options = null,
        SessionIngressGate? ingressGate = null,
        string? botUsername = null)
    {
        var deps = CreateDependencies(
            options: options,
            ingressGate: ingressGate,
            botUsername: botUsername,
            sessionPropsFactory: (_, _, _, _) =>
                Props.Create(() => new ForwardActor(sink.Ref)));

        return Sys.ActorOf(
            MattermostConversationActor.CreateProps(new MattermostChannelId(channelId), deps),
            $"mm-conv-{channelId}-{Guid.NewGuid():N}");
    }

    private static MattermostGatewayDependencies CreateDependencies(
        MattermostChannelOptions? options = null,
        SessionIngressGate? ingressGate = null,
        string? botUsername = null,
        IMattermostReplyClient? replyClient = null,
        Func<SessionId, MattermostChannelId, MattermostRootPostId, MattermostGatewayDependencies, Props>? sessionPropsFactory = null)
    {
        return new MattermostGatewayDependencies(
            Pipeline: null!,
            IngressGate: ingressGate,
            TimeProvider: TimeProvider.System,
            Options: options ?? new MattermostChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowDirectMessages = true,
                AllowedChannelIds = ["ch-1"]
            },
            DefaultChannelId: null,
            ReplyClient: replyClient ?? new UnconfiguredMattermostReplyClient(),
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
            Paths: TestMattermostGatewayDeps.NewTestPaths(),
            BotUsername: botUsername,
            SessionPropsFactory: sessionPropsFactory);
    }

    private static MattermostGatewayMessage CreateMessage(
        string channelId,
        string text,
        string eventId = "ev-1",
        string postId = "p-1",
        string rootPostId = "root-1",
        string senderId = "u-1",
        bool containsBotMention = false,
        bool isDirectMessage = false)
    {
        return new MattermostGatewayMessage(
            EventId: new MattermostEventId(eventId),
            ChannelId: new MattermostChannelId(channelId),
            PostId: new MattermostPostId(postId),
            RootPostId: new MattermostRootPostId(rootPostId),
            SenderId: new MattermostUserId(senderId),
            IsBotMessage: false,
            IsDirectMessage: isDirectMessage,
            ContainsBotMention: containsBotMention,
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());
    }

    private static MessageSource CreateReminderSource() => new()
    {
        ChannelType = ChannelType.Mattermost,
        SenderId = new SenderId("reminder-system"),
        MessageId = "reminder-1",
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.TrustedInstance,
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        {
            SourceKind = new SourceKind("reminder")
        },
        ReminderId = new ReminderId("rem-1")
    };
}
