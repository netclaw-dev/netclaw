using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Slack;
using Netclaw.Security;
using Xunit;

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
    public async Task Gateway_deduplicates_same_event_id()
    {
        var sink = CreateTestProbe("gateway-sink");
        var deps = CreateDependencies(
            conversationPropsFactory: (_, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(SlackGatewayActor.CreateProps(deps), "slack-gateway-test-1");
        var message = CreateMessage(eventId: "C1:100", channelId: "C1", eventTs: "100.1");

        gateway.Tell(message);
        gateway.Tell(message);

        await sink.ExpectMsgAsync<SlackInboundMessage>(cancellationToken: TestContext.Current.CancellationToken);
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Gateway_routes_approval_response_through_conversation_to_thread()
    {
        var sink = CreateTestProbe("approval-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var gateway = Sys.ActorOf(SlackGatewayActor.CreateProps(deps), "slack-gateway-approval-test");

        gateway.Tell(CreateMessage(
            eventId: "D1:401",
            channelId: "D1",
            eventTs: "401.1",
            text: "hello from dm",
            isDirectMessage: true,
            threadTs: null));

        await sink.ExpectMsgAsync<SlackThreadInbound>(cancellationToken: TestContext.Current.CancellationToken);

        gateway.Tell(new SlackApprovalResponse(
            new SlackChannelId("D1"),
            new SlackThreadTs("401.1"),
            "call-1",
            ApprovalOptionKeys.ApproveOnce,
            "U1"));

        var routed = await sink.ExpectMsgAsync<SlackApprovalResponse>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("call-1", routed.CallId);
        Assert.Equal(ApprovalOptionKeys.ApproveOnce, routed.SelectedKey);
        Assert.Equal("U1", routed.SenderId);
    }

    [Fact]
    public async Task Conversation_requires_mention_to_start_and_allows_thread_followups()
    {
        var sink = CreateTestProbe("conversation-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(SlackConversationActor.CreateProps(new SlackChannelId("C1"), deps), "slack-conversation-test-1");

        conversation.Tell(CreateMessage(
            eventId: "C1:200",
            channelId: "C1",
            eventTs: "200.1",
            text: "no mention",
            threadTs: null));

        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        conversation.Tell(CreateAppMention(
            eventId: "C1:201",
            channelId: "C1",
            eventTs: "201.1",
            text: "<@UBOT> start"));

        var first = await sink.ExpectMsgAsync<SlackThreadInbound>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("C1/201.1", first.SessionId.Value);
        Assert.Equal("start", first.Text);

        conversation.Tell(CreateMessage(
            eventId: "C1:202",
            channelId: "C1",
            eventTs: "202.1",
            text: "follow up",
            threadTs: "201.1"));

        var second = await sink.ExpectMsgAsync<SlackThreadInbound>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("C1/201.1", second.SessionId.Value);
        Assert.Equal("follow up", second.Text);
    }

    [Fact]
    public async Task Conversation_allows_dm_without_mention_when_enabled()
    {
        var sink = CreateTestProbe("dm-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(SlackConversationActor.CreateProps(new SlackChannelId("D1"), deps), "slack-conversation-test-2");

        conversation.Tell(CreateMessage(
            eventId: "D1:300",
            channelId: "D1",
            eventTs: "300.1",
            text: "hello from dm",
            isDirectMessage: true,
            threadTs: null));

        var inbound = await sink.ExpectMsgAsync<SlackThreadInbound>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("D1/300.1", inbound.SessionId.Value);
        Assert.Equal("hello from dm", inbound.Text);
    }

    [Fact]
    public async Task Conversation_forwards_files_from_app_mention()
    {
        var sink = CreateTestProbe("files-mention-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(SlackConversationActor.CreateProps(new SlackChannelId("C1"), deps), "slack-conversation-test-files-1");

        var files = new List<SlackFileReference>
        {
            new("F1", "image.png", "image/png", 2048, "https://files.slack.com/F1/image.png")
        };

        conversation.Tell(CreateAppMention(
            eventId: "C1:500",
            channelId: "C1",
            eventTs: "500.1",
            text: "<@UBOT> check this",
            files: files));

        var inbound = await sink.ExpectMsgAsync<SlackThreadInbound>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("check this", inbound.Text);
        Assert.NotNull(inbound.Files);
        Assert.Single(inbound.Files);
        Assert.Equal("F1", inbound.Files[0].Id);
    }

    [Fact]
    public async Task Conversation_forwards_files_when_normalized_text_empty()
    {
        var sink = CreateTestProbe("files-empty-text-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(SlackConversationActor.CreateProps(new SlackChannelId("C1"), deps), "slack-conversation-test-files-2");

        var files = new List<SlackFileReference>
        {
            new("F2", "photo.jpg", "image/jpeg", 4096, "https://files.slack.com/F2/photo.jpg")
        };

        // AppMention with only the bot mention — normalized text will be empty but files exist
        conversation.Tell(CreateAppMention(
            eventId: "C1:600",
            channelId: "C1",
            eventTs: "600.1",
            text: "<@UBOT>",
            files: files));

        var inbound = await sink.ExpectMsgAsync<SlackThreadInbound>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, inbound.Text);
        Assert.NotNull(inbound.Files);
        Assert.Single(inbound.Files);
        Assert.Equal("F2", inbound.Files[0].Id);
    }

    [Fact]
    public async Task Conversation_ignores_bot_messages_to_prevent_feedback_loop()
    {
        var sink = CreateTestProbe("bot-loop-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(SlackConversationActor.CreateProps(new SlackChannelId("C1"), deps), "slack-conversation-test-3");

        conversation.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("C1:400"),
            ChannelId: new SlackChannelId("C1"),
            ThreadTs: new SlackThreadTs("400.1"),
            EventTs: new SlackEventTs("400.2"),
            UserId: new SlackUserId("UBOT"),
            BotId: new SlackBotId("B1"),
            Text: "bot output",
            Subtype: "bot_message",
            Hidden: false,
            IsDirectMessage: false));

        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
    }

    private static SlackGatewayDependencies CreateDependencies(
        Func<SlackChannelId, SlackGatewayDependencies, Props>? conversationPropsFactory = null,
        Func<SessionId, SlackChannelId, SlackThreadTs, SlackGatewayDependencies, Props>? threadPropsFactory = null)
    {
        return new SlackGatewayDependencies(
            Pipeline: null!,
            IngressGate: null,
            ActorSystem: null!,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                MentionOnly = true,
                AllowDirectMessages = true,
                AllowedChannelIds = ["C1"]
            },
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: null,
            ReplyClient: new NoopReplyClient(),
            ContentScanner: new NullContentScanner(),
            ThreadHistoryFetcher: EmptyThreadHistoryFetcher.Instance,
            AudienceProfiles: TestSlackGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestSlackGatewayDeps.DefaultVisionCapableModel,
            Paths: TestSlackGatewayDeps.NewTestPaths(),
            ConversationPropsFactory: conversationPropsFactory,
            ThreadPropsFactory: threadPropsFactory);
    }

    private static SlackInboundMessage CreateMessage(
        string eventId,
        string channelId,
        string eventTs,
        string text = "hello",
        string? threadTs = null,
        bool isDirectMessage = false,
        IReadOnlyList<SlackFileReference>? files = null)
    {
        return new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId(eventId),
            ChannelId: new SlackChannelId(channelId),
            ThreadTs: threadTs is not null ? new SlackThreadTs(threadTs) : null,
            EventTs: new SlackEventTs(eventTs),
            UserId: new SlackUserId("U1"),
            BotId: null,
            Text: text,
            Subtype: null,
            Hidden: false,
            IsDirectMessage: isDirectMessage,
            Files: files);
    }

    private static SlackInboundMessage CreateAppMention(
        string eventId,
        string channelId,
        string eventTs,
        string text,
        IReadOnlyList<SlackFileReference>? files = null)
    {
        return new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: new SlackEventId(eventId),
            ChannelId: new SlackChannelId(channelId),
            ThreadTs: null,
            EventTs: new SlackEventTs(eventTs),
            UserId: new SlackUserId("U1"),
            BotId: null,
            Text: text,
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false,
            Files: files);
    }

    private sealed class ForwardActor : ReceiveActor
    {
        public ForwardActor(IActorRef target)
        {
            ReceiveAny(msg => target.Tell(msg));
        }
    }

}
