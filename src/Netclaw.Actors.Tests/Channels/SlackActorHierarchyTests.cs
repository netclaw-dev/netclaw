using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Slack;
using Netclaw.Security;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SlackActorHierarchyTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public void Gateway_deduplicates_same_event_id()
    {
        var sink = CreateTestProbe("gateway-sink");
        var deps = CreateDependencies(
            conversationPropsFactory: (_, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(SlackGatewayActor.CreateProps(deps), "slack-gateway-test-1");
        var message = CreateMessage(eventId: "C1:100", channelId: "C1", eventTs: "100.1");

        gateway.Tell(message);
        gateway.Tell(message);

        sink.ExpectMsg<SlackInboundMessage>();
        sink.ExpectNoMsg(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void Conversation_requires_mention_to_start_and_allows_thread_followups()
    {
        var sink = CreateTestProbe("conversation-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(SlackConversationActor.CreateProps("C1", deps), "slack-conversation-test-1");

        conversation.Tell(CreateMessage(
            eventId: "C1:200",
            channelId: "C1",
            eventTs: "200.1",
            text: "no mention",
            threadTs: null));

        sink.ExpectNoMsg(TimeSpan.FromMilliseconds(250));

        conversation.Tell(CreateAppMention(
            eventId: "C1:201",
            channelId: "C1",
            eventTs: "201.1",
            text: "<@UBOT> start"));

        var first = sink.ExpectMsg<SlackThreadInbound>();
        Assert.Equal("C1/201.1", first.SessionId.Value);
        Assert.Equal("start", first.Text);

        conversation.Tell(CreateMessage(
            eventId: "C1:202",
            channelId: "C1",
            eventTs: "202.1",
            text: "follow up",
            threadTs: "201.1"));

        var second = sink.ExpectMsg<SlackThreadInbound>();
        Assert.Equal("C1/201.1", second.SessionId.Value);
        Assert.Equal("follow up", second.Text);
    }

    [Fact]
    public void Conversation_allows_dm_without_mention_when_enabled()
    {
        var sink = CreateTestProbe("dm-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(SlackConversationActor.CreateProps("D1", deps), "slack-conversation-test-2");

        conversation.Tell(CreateMessage(
            eventId: "D1:300",
            channelId: "D1",
            eventTs: "300.1",
            text: "hello from dm",
            isDirectMessage: true,
            threadTs: null));

        var inbound = sink.ExpectMsg<SlackThreadInbound>();
        Assert.Equal("D1/300.1", inbound.SessionId.Value);
        Assert.Equal("hello from dm", inbound.Text);
    }

    [Fact]
    public void Conversation_ignores_bot_messages_to_prevent_feedback_loop()
    {
        var sink = CreateTestProbe("bot-loop-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(SlackConversationActor.CreateProps("C1", deps), "slack-conversation-test-3");

        conversation.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: "C1:400",
            ChannelId: "C1",
            ThreadTs: "400.1",
            EventTs: "400.2",
            UserId: "UBOT",
            BotId: "B1",
            Text: "bot output",
            Subtype: "bot_message",
            Hidden: false,
            IsDirectMessage: false));

        sink.ExpectNoMsg(TimeSpan.FromMilliseconds(250));
    }

    private static SlackGatewayDependencies CreateDependencies(
        Func<string, SlackGatewayDependencies, Props>? conversationPropsFactory = null,
        Func<SessionId, string, string, SlackGatewayDependencies, Props>? threadPropsFactory = null)
    {
        return new SlackGatewayDependencies(
            Pipeline: null!,
            ActorSystem: null!,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                MentionOnly = true,
                AllowDirectMessages = true,
                AllowedChannelIds = ["C1"]
            },
            BotUserId: "UBOT",
            DefaultChannelId: null,
            ReplyClient: new NoopReplyClient(),
            ContentScanner: new NullContentScanner(),
            ConversationPropsFactory: conversationPropsFactory,
            ThreadPropsFactory: threadPropsFactory);
    }

    private static SlackInboundMessage CreateMessage(
        string eventId,
        string channelId,
        string eventTs,
        string text = "hello",
        string? threadTs = null,
        bool isDirectMessage = false)
    {
        return new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: eventId,
            ChannelId: channelId,
            ThreadTs: threadTs,
            EventTs: eventTs,
            UserId: "U1",
            BotId: null,
            Text: text,
            Subtype: null,
            Hidden: false,
            IsDirectMessage: isDirectMessage);
    }

    private static SlackInboundMessage CreateAppMention(
        string eventId,
        string channelId,
        string eventTs,
        string text)
    {
        return new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: eventId,
            ChannelId: channelId,
            ThreadTs: null,
            EventTs: eventTs,
            UserId: "U1",
            BotId: null,
            Text: text,
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false);
    }

    private sealed class ForwardActor : ReceiveActor
    {
        public ForwardActor(IActorRef target)
        {
            ReceiveAny(msg => target.Tell(msg));
        }
    }

    private sealed class NoopReplyClient : ISlackReplyClient
    {
        public Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UploadFileToThreadAsync(string channelId, string threadTs, string filePath, string? filename = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
