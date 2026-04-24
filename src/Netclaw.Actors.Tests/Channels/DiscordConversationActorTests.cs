using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Discord;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordConversationActorTests(ITestOutputHelper output) : TestKit(output: output)
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

        conversation.Tell(CreateMessage(channelId: "ch-1", threadOrMessageId: "th-42", text: "hello"));

        var inbound = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-1/th-42", inbound.SessionId.Value);
        Assert.Equal("hello", inbound.Text);
    }

    [Fact]
    public async Task Same_thread_routes_to_same_session_binding()
    {
        var sink = CreateTestProbe("same-thread");
        var conversation = CreateConversation("ch-1", sink);

        conversation.Tell(CreateMessage(channelId: "ch-1", threadOrMessageId: "th-42", text: "first"));
        var first = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);

        conversation.Tell(CreateMessage(
            channelId: "ch-1", threadOrMessageId: "th-42", text: "second", eventId: "ev-2"));
        var second = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(first.SessionId, second.SessionId);
    }

    [Fact]
    public async Task Thread_promotion_routes_promoted_thread_messages_to_original_binding()
    {
        var sink = CreateTestProbe("thread-promotion");
        var conversation = CreateConversation("ch-1", sink);

        // Initial message creates a session binding keyed by original message ID
        conversation.Tell(CreateMessage(
            channelId: "ch-1", threadOrMessageId: "msg-100", rootMessageId: "msg-100",
            text: "start conversation"));

        var first = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-1/msg-100", first.SessionId.Value);

        // Simulate thread promotion: the session binding actor tells the parent
        // that a thread was created with a new channel ID.
        conversation.Tell(new ThreadPromoted(
            OriginalThreadOrMessageId: new DiscordThreadOrMessageId("msg-100"),
            ThreadChannelId: new DiscordReplyChannelId("thread-ch-999")));

        // Follow-up message arrives with the new thread channel ID as ThreadOrMessageId
        conversation.Tell(CreateMessage(
            channelId: "ch-1", threadOrMessageId: "thread-ch-999",
            text: "follow up in promoted thread", eventId: "ev-2"));

        var second = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        // Should route to the SAME session with the ORIGINAL session ID
        Assert.Equal("ch-1/msg-100", second.SessionId.Value);
        Assert.Equal("follow up in promoted thread", second.Text);
    }

    [Fact]
    public async Task Button_interaction_routes_via_thread_alias()
    {
        var sink = CreateTestProbe("interaction-alias");
        var conversation = CreateConversation("ch-1", sink);

        // Create the session binding with an initial message
        conversation.Tell(CreateMessage(
            channelId: "ch-1", threadOrMessageId: "msg-200", rootMessageId: "msg-200",
            text: "start"));
        await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);

        // Register thread promotion alias
        conversation.Tell(new ThreadPromoted(
            OriginalThreadOrMessageId: new DiscordThreadOrMessageId("msg-200"),
            ThreadChannelId: new DiscordReplyChannelId("thread-ch-500")));

        // Send an interaction using the promoted thread channel ID
        conversation.Tell(new DiscordGatewayInteraction(
            ChannelId: new DiscordChannelId("ch-1"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-ch-500"),
            CallId: "call-1",
            SelectedKey: ApprovalOptionKeys.ApproveOnce,
            SenderId: new DiscordUserId("u-1"),
            RequesterSenderId: new DiscordUserId("u-1"),
            ReceivedAt: TimeProvider.System.GetUtcNow()));

        var approval = await sink.ExpectMsgAsync<DiscordApprovalResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("call-1", approval.CallId);
    }

    [Fact]
    public async Task ACL_denied_messages_not_routed()
    {
        var sink = CreateTestProbe("acl-denied");
        // ch-99 is not in AllowedChannelIds, so ACL will deny it
        var conversation = CreateConversation("ch-99", sink);

        conversation.Tell(CreateMessage(channelId: "ch-99", text: "should be denied"));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Bot_messages_filtered()
    {
        var sink = CreateTestProbe("bot-filter");
        var conversation = CreateConversation("ch-1", sink);

        conversation.Tell(new DiscordGatewayMessage(
            EventId: new DiscordEventId("ev-bot"),
            ChannelId: new DiscordChannelId("ch-1"),
            ReplyChannelId: new DiscordReplyChannelId("ch-1"),
            MessageId: new DiscordMessageId("m-bot"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("m-bot"),
            RootMessageId: new DiscordMessageId("m-bot"),
            SenderId: new DiscordUserId("u-bot"),
            IsBotMessage: true,
            IsDirectMessage: false,
            ContainsBotMention: false,
            Text: "bot output",
            ReceivedAt: TimeProvider.System.GetUtcNow()));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MentionOnly_filters_non_mention_messages()
    {
        var sink = CreateTestProbe("mention-filter");
        var options = new DiscordChannelOptions
        {
            Enabled = true,
            MentionOnly = true,
            AllowedChannelIds = ["ch-1"]
        };
        var conversation = CreateConversation("ch-1", sink, options);

        conversation.Tell(CreateMessage(
            channelId: "ch-1", text: "no mention", rootMessageId: "m-1",
            containsBotMention: false));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MentionOnly_allows_mention_messages()
    {
        var sink = CreateTestProbe("mention-allow");
        var options = new DiscordChannelOptions
        {
            Enabled = true,
            MentionOnly = true,
            AllowedChannelIds = ["ch-1"]
        };
        var conversation = CreateConversation("ch-1", sink, options);

        conversation.Tell(CreateMessage(
            channelId: "ch-1", text: "<@123> hello", rootMessageId: "m-1",
            containsBotMention: true));

        var inbound = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("hello", inbound.Text);
    }

    [Fact]
    public async Task MentionOnly_allows_existing_thread_without_mention()
    {
        var sink = CreateTestProbe("mention-thread-continue");
        var options = new DiscordChannelOptions
        {
            Enabled = true,
            MentionOnly = true,
            AllowedChannelIds = ["ch-1"]
        };
        var conversation = CreateConversation("ch-1", sink, options);

        // Start thread with mention
        conversation.Tell(CreateMessage(
            channelId: "ch-1", threadOrMessageId: "th-1", rootMessageId: "m-1",
            text: "<@123> start", containsBotMention: true));
        await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);

        // Follow-up in same thread without mention (ContinueOnly)
        conversation.Tell(CreateMessage(
            channelId: "ch-1", threadOrMessageId: "th-1",
            text: "follow up without mention", eventId: "ev-2",
            containsBotMention: false));

        var second = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("follow up without mention", second.Text);
    }

    [Fact]
    public async Task Empty_text_filtered()
    {
        var sink = CreateTestProbe("empty-text");
        var conversation = CreateConversation("ch-1", sink);

        conversation.Tell(CreateMessage(channelId: "ch-1", text: "   "));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
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
    public async Task DeliverTrustedSessionTurn_routes_to_existing_session()
    {
        var sink = CreateTestProbe("trusted-turn");
        var conversation = CreateConversation("ch-1", sink);

        // Create a session binding first
        conversation.Tell(CreateMessage(channelId: "ch-1", threadOrMessageId: "th-50", text: "setup"));
        await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);

        // Deliver trusted turn
        conversation.Tell(new DeliverTrustedSessionTurn(
            SessionId: new SessionId("ch-1/th-50"),
            Content: "reminder content",
            Source: CreateReminderSource()));

        var forwarded = await sink.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-1/th-50", forwarded.SessionId.Value);
    }

    [Fact]
    public async Task DeliverTrustedSessionTurn_recreates_passivated_binding()
    {
        var sink = CreateTestProbe("trusted-turn-recreate");
        var conversation = CreateConversation("ch-1", sink);

        // Deliver trusted turn WITHOUT an existing session binding.
        // Fix #3: should re-create the binding, not NACK.
        conversation.Tell(new DeliverTrustedSessionTurn(
            SessionId: new SessionId("ch-1/th-99"),
            Content: "reminder for passivated session",
            Source: CreateReminderSource()));

        var forwarded = await sink.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-1/th-99", forwarded.SessionId.Value);
    }

    [Fact]
    public async Task DeliverTrustedSessionTurn_nacks_channel_mismatch()
    {
        var sink = CreateTestProbe("trusted-turn-mismatch");
        var conversation = CreateConversation("ch-1", sink);

        var probe = CreateTestProbe("nack-receiver");
        conversation.Tell(
            new DeliverTrustedSessionTurn(
                SessionId: new SessionId("ch-99/th-50"),
                Content: "wrong channel",
                Source: CreateReminderSource()),
            probe.Ref);

        var nack = await probe.ExpectMsgAsync<CommandNack>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("mismatch", nack.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mention_tag_stripped_from_text()
    {
        var sink = CreateTestProbe("mention-strip");
        var deps = CreateDependencies(
            sessionPropsFactory: (_, _, _, _, _, _) =>
                Props.Create(() => new ForwardActor(sink.Ref)),
            botUserId: new DiscordUserId("12345"));
        var conversation = Sys.ActorOf(
            DiscordConversationActor.CreateProps(new DiscordChannelId("ch-1"), deps),
            "conv-mention-strip");

        conversation.Tell(CreateMessage(
            channelId: "ch-1", text: "<@12345> what is the weather?",
            rootMessageId: "m-1", containsBotMention: true));

        var inbound = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("what is the weather?", inbound.Text);
    }

    [Fact]
    public async Task Interaction_for_missing_session_ignored()
    {
        var sink = CreateTestProbe("interaction-missing");
        var conversation = CreateConversation("ch-1", sink);

        conversation.Tell(new DiscordGatewayInteraction(
            ChannelId: new DiscordChannelId("ch-1"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("nonexistent"),
            CallId: "call-1",
            SelectedKey: ApprovalOptionKeys.ApproveOnce,
            SenderId: new DiscordUserId("u-1"),
            RequesterSenderId: null,
            ReceivedAt: TimeProvider.System.GetUtcNow()));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
    }

    private IActorRef CreateConversation(
        string channelId,
        Akka.TestKit.TestProbe sink,
        DiscordChannelOptions? options = null,
        SessionIngressGate? ingressGate = null)
    {
        var deps = CreateDependencies(
            options: options,
            ingressGate: ingressGate,
            sessionPropsFactory: (_, _, _, _, _, _) =>
                Props.Create(() => new ForwardActor(sink.Ref)));

        return Sys.ActorOf(
            DiscordConversationActor.CreateProps(new DiscordChannelId(channelId), deps),
            $"discord-conv-{channelId}-{Guid.NewGuid():N}");
    }

    private static DiscordGatewayDependencies CreateDependencies(
        DiscordChannelOptions? options = null,
        SessionIngressGate? ingressGate = null,
        DiscordUserId? botUserId = null,
        Func<SessionId, DiscordChannelId, DiscordReplyChannelId, DiscordThreadOrMessageId, DiscordMessageId?, DiscordGatewayDependencies, Props>? sessionPropsFactory = null)
    {
        return new DiscordGatewayDependencies(
            Pipeline: null!,
            IngressGate: ingressGate,
            TimeProvider: TimeProvider.System,
            Options: options ?? new DiscordChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowedChannelIds = ["ch-1"]
            },
            DefaultChannelId: null,
            ReplyClient: new UnconfiguredDiscordReplyClient(),
            BotUserId: botUserId,
            SessionPropsFactory: sessionPropsFactory);
    }

    private static DiscordGatewayMessage CreateMessage(
        string channelId,
        string text,
        string eventId = "ev-1",
        string threadOrMessageId = "m-1",
        string? rootMessageId = null,
        string senderId = "u-1",
        bool containsBotMention = false)
    {
        return new DiscordGatewayMessage(
            EventId: new DiscordEventId(eventId),
            ChannelId: new DiscordChannelId(channelId),
            ReplyChannelId: new DiscordReplyChannelId(channelId),
            MessageId: new DiscordMessageId(threadOrMessageId),
            ThreadOrMessageId: new DiscordThreadOrMessageId(threadOrMessageId),
            RootMessageId: rootMessageId is not null ? new DiscordMessageId(rootMessageId) : null,
            SenderId: new DiscordUserId(senderId),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: containsBotMention,
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());
    }

    private static MessageSource CreateReminderSource() => new()
    {
        ChannelType = ChannelType.Discord,
        SenderId = "reminder-system",
        MessageId = "reminder-1",
        Audience = TrustAudience.Team,
        Boundary = "trusted-instance",
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance
        {
            TransportAuthenticity = TransportAuthenticity.Verified,
            SourceKind = "reminder"
        },
        ReminderId = "rem-1"
    };
}
