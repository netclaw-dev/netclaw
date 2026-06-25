// -----------------------------------------------------------------------
// <copyright file="DiscordGatewayActorTests.cs" company="Petabridge, LLC">
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
using Netclaw.Channels.Discord;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordGatewayActorTests(ITestOutputHelper output) : TestKit(output: output)
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
    public async Task Gateway_routes_messages_to_conversation_actor_by_channel_id()
    {
        var sink = CreateTestProbe("discord-sink-route");
        var deps = CreateDependencies(
            conversationPropsFactory: (_, _) =>
                Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gateway-test-route");

        gateway.Tell(CreateMessage(
            eventId: "ev-1",
            channelId: "ch-7",
            replyChannelId: "th-42",
            messageId: "m-1",
            threadOrMessageId: "th-42",
            rootMessageId: null,
            senderId: "u-1",
            text: "hello"));

        var first = await sink.ExpectMsgAsync<DiscordGatewayMessage>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-7", first.ChannelId.Value);
        Assert.Equal("hello", first.Text);
    }

    [Fact]
    public async Task Gateway_routes_same_channel_messages_to_same_conversation_actor()
    {
        var sink = CreateTestProbe("discord-sink-same-channel");
        var deps = CreateDependencies(
            conversationPropsFactory: (_, _) =>
                Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gateway-test-same-channel");

        gateway.Tell(CreateMessage(
            eventId: "ev-1",
            channelId: "ch-7",
            replyChannelId: "th-42",
            messageId: "m-1",
            threadOrMessageId: "th-42",
            rootMessageId: null,
            senderId: "u-1",
            text: "hello"));

        var first = await sink.ExpectMsgAsync<DiscordGatewayMessage>(
            cancellationToken: TestContext.Current.CancellationToken);

        gateway.Tell(CreateMessage(
            eventId: "ev-2",
            channelId: "ch-7",
            replyChannelId: "th-42",
            messageId: "m-2",
            threadOrMessageId: "th-42",
            rootMessageId: null,
            senderId: "u-1",
            text: "follow up"));

        var second = await sink.ExpectMsgAsync<DiscordGatewayMessage>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("follow up", second.Text);
    }

    [Fact]
    public async Task Gateway_forwards_raw_message_preserving_text()
    {
        var sink = CreateTestProbe("discord-sink-slash");
        var deps = CreateDependencies(
            conversationPropsFactory: (_, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gateway-test-slash");
        const string slashText = "/netclaw-operations check daemon health";

        gateway.Tell(CreateMessage(
            eventId: "ev-slash",
            channelId: "ch-7",
            replyChannelId: "ch-7",
            messageId: "m-slash",
            threadOrMessageId: "m-slash",
            rootMessageId: "m-slash",
            senderId: "u-1",
            text: slashText));

        var inbound = await sink.ExpectMsgAsync<DiscordGatewayMessage>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(slashText, inbound.Text);
    }

    [Fact]
    public async Task Gateway_forwards_interaction_to_conversation_actor()
    {
        var sink = CreateTestProbe("discord-sink-interaction");
        var deps = CreateDependencies(
            conversationPropsFactory: (_, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gateway-test-interaction");

        gateway.Tell(new DiscordGatewayInteraction(
            ChannelId: new DiscordChannelId("ch-7"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("m-setup"),
            CallId: "call-1",
            SelectedKey: ApprovalOptionKeys.ApproveOnce,
            SenderId: new DiscordUserId("u-1"),
            RequesterSenderId: new DiscordUserId("u-1"),
            ReceivedAt: TimeProvider.System.GetUtcNow()));

        var interaction = await sink.ExpectMsgAsync<DiscordGatewayInteraction>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("call-1", interaction.CallId);
        Assert.Equal(ApprovalOptionKeys.ApproveOnce, interaction.SelectedKey);
        Assert.Equal("u-1", interaction.SenderId.Value);
    }

    [Fact]
    public async Task Gateway_rejects_messages_with_empty_event_id()
    {
        var sink = CreateTestProbe("discord-sink-empty-eid");
        var deps = CreateDependencies(
            conversationPropsFactory: (_, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gateway-test-empty-eid");

        gateway.Tell(CreateMessage(
            eventId: "",
            channelId: "ch-7",
            replyChannelId: "ch-7",
            messageId: "m-1",
            threadOrMessageId: "m-1",
            rootMessageId: "m-1",
            senderId: "u-1",
            text: "no event id"));

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Gateway_forwards_bot_messages_to_conversation_actor()
    {
        var sink = CreateTestProbe("discord-sink-bot");
        var deps = CreateDependencies(
            conversationPropsFactory: (_, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gateway-test-bot");

        var botMessage = new DiscordGatewayMessage(
            EventId: new DiscordEventId("ev-bot"),
            ChannelId: new DiscordChannelId("ch-7"),
            ReplyChannelId: new DiscordReplyChannelId("ch-7"),
            MessageId: new DiscordMessageId("m-1"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("m-1"),
            RootMessageId: new DiscordMessageId("m-1"),
            SenderId: new DiscordUserId("u-bot"),
            IsBotMessage: true,
            IsDirectMessage: false,
            ContainsBotMention: false,
            Text: "bot message",
            ReceivedAt: TimeProvider.System.GetUtcNow());

        gateway.Tell(botMessage);

        // Gateway no longer filters bot messages -- that is the conversation actor's job.
        var forwarded = await sink.ExpectMsgAsync<DiscordGatewayMessage>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(forwarded.IsBotMessage);
    }

    [Fact]
    public async Task Gateway_deduplicates_events()
    {
        var sink = CreateTestProbe("discord-sink-dedup");
        var deps = CreateDependencies(
            conversationPropsFactory: (_, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gateway-test-dedup");

        var msg = CreateMessage(
            eventId: "ev-dup",
            channelId: "ch-7",
            replyChannelId: "ch-7",
            messageId: "m-1",
            threadOrMessageId: "m-1",
            rootMessageId: "m-1",
            senderId: "u-1",
            text: "first send");

        gateway.Tell(msg);
        await sink.ExpectMsgAsync<DiscordGatewayMessage>(cancellationToken: TestContext.Current.CancellationToken);

        // Send same event ID again
        gateway.Tell(msg);
        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Gateway_routes_trusted_session_turn_to_conversation_actor()
    {
        var sink = CreateTestProbe("discord-sink-trusted");
        var deps = CreateDependencies(
            conversationPropsFactory: (_, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gateway-test-trusted");

        var turn = new DeliverTrustedSessionTurn(
            SessionId: new SessionId("ch-7/th-42"),
            Content: "reminder content",
            Source: new MessageSource
            {
                ChannelType = ChannelType.Discord,
                SenderId = new SenderId("system"),
                MessageId = "reminder-1",
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.TrustedInstance,
                Principal = PrincipalClassification.TrustedInternal,
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
                {
                    SourceKind = new Netclaw.Actors.Channels.SourceKind("reminder")
                },
                ReminderId = new ReminderId("rem-1")
            });

        gateway.Tell(turn);

        var forwarded = await sink.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-7/th-42", forwarded.SessionId.Value);
    }

    [Fact]
    public async Task Gateway_nacks_trusted_session_turn_with_invalid_session_id()
    {
        var sink = CreateTestProbe("discord-sink-nack");
        var deps = CreateDependencies(
            conversationPropsFactory: (_, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gateway-test-nack");

        var turn = new DeliverTrustedSessionTurn(
            SessionId: new SessionId("invalid-no-slash"),
            Content: "reminder content",
            Source: new MessageSource
            {
                ChannelType = ChannelType.Discord,
                SenderId = new SenderId("system"),
                MessageId = "reminder-1",
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.TrustedInstance,
                Principal = PrincipalClassification.TrustedInternal,
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
                {
                    SourceKind = new Netclaw.Actors.Channels.SourceKind("reminder")
                },
                ReminderId = new ReminderId("rem-1")
            });

        gateway.Tell(turn, TestActor);

        var nack = await ExpectMsgAsync<CommandNack>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Invalid Discord SessionId format", nack.Reason);
    }

    private static DiscordGatewayDependencies CreateDependencies(
        DiscordChannelOptions? options = null,
        Func<DiscordChannelId, DiscordGatewayDependencies, Props>? conversationPropsFactory = null)
    {
        var replyClient = new UnconfiguredDiscordReplyClient();

        return new DiscordGatewayDependencies(
            Pipeline: null!,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: options ?? new DiscordChannelOptions
            {
                Enabled = true,
                AllowedChannelIds = ["ch-7"]
            },
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.DiscordWithProcessingRenderer(replyClient),
            ReplyClient: replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            Paths: TestDiscordGatewayDeps.NewTestPaths(),
            ConversationPropsFactory: conversationPropsFactory,
            PromptInjectionDetector: SafePromptInjectionDetector.Instance);
    }

    private static DiscordGatewayMessage CreateMessage(
        string eventId,
        string channelId,
        string replyChannelId,
        string messageId,
        string threadOrMessageId,
        string? rootMessageId,
        string senderId,
        string text)
    {
        return new DiscordGatewayMessage(
            EventId: new DiscordEventId(eventId),
            ChannelId: new DiscordChannelId(channelId),
            ReplyChannelId: new DiscordReplyChannelId(replyChannelId),
            MessageId: new DiscordMessageId(messageId),
            ThreadOrMessageId: new DiscordThreadOrMessageId(threadOrMessageId),
            RootMessageId: rootMessageId is null ? null : new DiscordMessageId(rootMessageId),
            SenderId: new DiscordUserId(senderId),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: false,
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());
    }
}
