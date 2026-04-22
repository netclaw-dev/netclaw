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
using Xunit;

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
    public async Task Gateway_routes_threaded_and_root_messages_to_stable_session_ids()
    {
        var sink = CreateTestProbe("discord-sink-route");
        var deps = CreateDependencies(
            sessionPropsFactory: (sessionId, channelId, replyChannelId, threadOrMessageId, rootMessageId, _) =>
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

        var first = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-7/th-42", first.SessionId.Value);

        gateway.Tell(CreateMessage(
            eventId: "ev-2",
            channelId: "ch-7",
            replyChannelId: "th-42",
            messageId: "m-2",
            threadOrMessageId: "th-42",
            rootMessageId: null,
            senderId: "u-1",
            text: "follow up"));

        var second = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-7/th-42", second.SessionId.Value);

        gateway.Tell(CreateMessage(
            eventId: "ev-3",
            channelId: "ch-7",
            replyChannelId: "ch-7",
            messageId: "m-9001",
            threadOrMessageId: "m-9001",
            rootMessageId: "m-9001",
            senderId: "u-1",
            text: "root"));

        var root = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-7/m-9001", root.SessionId.Value);
    }

    [Fact]
    public async Task Gateway_preserves_leading_slash_text()
    {
        var sink = CreateTestProbe("discord-sink-slash");
        var deps = CreateDependencies(
            sessionPropsFactory: (_, _, _, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

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

        var inbound = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(slashText, inbound.Text);
    }

    [Fact]
    public async Task Gateway_routes_interaction_response_to_existing_session_binding()
    {
        var sink = CreateTestProbe("discord-sink-interaction");
        var deps = CreateDependencies(
            sessionPropsFactory: (_, _, _, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gateway-test-interaction");

        gateway.Tell(CreateMessage(
            eventId: "ev-setup",
            channelId: "ch-7",
            replyChannelId: "ch-7",
            messageId: "m-setup",
            threadOrMessageId: "m-setup",
            rootMessageId: "m-setup",
            senderId: "u-1",
            text: "seed"));

        await sink.ExpectMsgAsync<DiscordThreadInbound>(cancellationToken: TestContext.Current.CancellationToken);

        gateway.Tell(new DiscordGatewayInteraction(
            ChannelId: new DiscordChannelId("ch-7"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("m-setup"),
            CallId: "call-1",
            SelectedKey: ApprovalOptionKeys.ApproveOnce,
            SenderId: new DiscordUserId("u-1"),
            RequesterSenderId: new DiscordUserId("u-1"),
            ReceivedAt: TimeProvider.System.GetUtcNow()));

        var interaction = await sink.ExpectMsgAsync<DiscordApprovalResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("call-1", interaction.CallId);
        Assert.Equal(ApprovalOptionKeys.ApproveOnce, interaction.SelectedKey);
        Assert.Equal("u-1", interaction.SenderId.Value);
    }

    [Fact]
    public async Task Gateway_accepts_messages_with_empty_event_id()
    {
        var sink = CreateTestProbe("discord-sink-empty-eid");
        var deps = CreateDependencies(
            sessionPropsFactory: (_, _, _, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

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

        await sink.ExpectMsgAsync<DiscordThreadInbound>(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Gateway_drops_bot_messages()
    {
        var sink = CreateTestProbe("discord-sink-bot");
        var deps = CreateDependencies(
            sessionPropsFactory: (_, _, _, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

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
            Text: "bot message",
            ReceivedAt: TimeProvider.System.GetUtcNow());

        gateway.Tell(botMessage);

        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
    }

    private static DiscordGatewayDependencies CreateDependencies(
        DiscordChannelOptions? options = null,
        Func<SessionId, DiscordChannelId, DiscordReplyChannelId, DiscordThreadOrMessageId, DiscordMessageId?, DiscordGatewayDependencies, Props>? sessionPropsFactory = null)
    {
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
            ReplyClient: new UnconfiguredDiscordReplyClient(),
            SessionPropsFactory: sessionPropsFactory);
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
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());
    }

}
