// -----------------------------------------------------------------------
// <copyright file="DiscordProactiveThreadTests.cs" company="Petabridge, LLC">
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
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Discord.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

#region SendDiscordMessageTool Tests

public sealed class SendDiscordMessageToolTests
{
    private static readonly DiscordChannelOptions DefaultOptions = new()
    {
        Enabled = true,
        AllowedChannelIds = ["ch-1", "ch-2"]
    };

    [Fact]
    public async Task Rejects_empty_message()
    {
        var tool = CreateTool();
        var result = await ExecuteAsync(tool, "   ", channelId: "ch-1");
        Assert.Contains("'message' parameter is required", result);
    }

    [Fact]
    public async Task Rejects_disallowed_channel()
    {
        var tool = CreateTool();
        var result = await ExecuteAsync(tool, "hello", channelId: "ch-bad");
        Assert.Contains("not in the allowed channels list", result);
    }

    [Fact]
    public async Task Allows_default_channel_when_channel_id_omitted()
    {
        var options = new DiscordChannelOptions
        {
            Enabled = true,
            DefaultChannelId = "ch-default"
        };
        var fake = new FakeDiscordOutboundClient();
        var tool = CreateTool(outbound: fake, options: options);

        var result = await ExecuteAsync(tool, "hello");

        Assert.Contains("Message sent to channel ch-default", result);
        Assert.Single(fake.Posts);
        Assert.Equal("ch-default", fake.Posts[0].ChannelId.Value);
    }

    [Fact]
    public async Task Rejects_when_no_channel_and_no_default()
    {
        var tool = CreateTool();
        var result = await ExecuteAsync(tool, "hello");
        Assert.Contains("no default Discord channel is configured", result);
    }

    [Fact]
    public async Task Returns_error_when_gateway_disconnected()
    {
        var tool = CreateTool(gatewayAccessor: () => null);
        var result = await ExecuteAsync(tool, "hello", channelId: "ch-1");
        Assert.Contains("gateway is not connected", result);
    }

    [Fact]
    public async Task Returns_error_on_post_failure()
    {
        var fake = new FakeDiscordOutboundClient { ShouldThrow = true };
        var tool = CreateTool(outbound: fake);
        var result = await ExecuteAsync(tool, "hello", channelId: "ch-1");
        Assert.Contains("Failed to post message to Discord", result);
    }

    [Fact]
    public async Task Returns_partial_success_when_thread_creation_fails_after_message_post()
    {
        var fake = new FakeDiscordOutboundClient { ThrowThreadCreationFailure = true };
        var tool = CreateTool(outbound: fake);

        var result = await ExecuteAsync(tool, "hello", channelId: "ch-1");

        Assert.DoesNotContain("Error:", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Message sent to channel ch-1", result);
        Assert.Contains("could not create a follow-up thread", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("root-ch-1", result);
    }

    [Fact]
    public async Task Successful_channel_message_posts_and_wires_session()
    {
        var fake = new FakeDiscordOutboundClient();
        var tool = CreateTool(outbound: fake);

        var result = await ExecuteAsync(tool, "hello world", channelId: "ch-1");

        Assert.Contains("Message sent to channel ch-1", result);
        Assert.Contains("ch-1/", result);
        Assert.Single(fake.Posts);
        Assert.Equal("ch-1", fake.Posts[0].ChannelId.Value);
        Assert.Equal("hello world", fake.Posts[0].Text);
    }

    [Fact]
    public async Task Uses_provided_thread_name()
    {
        var fake = new FakeDiscordOutboundClient();
        var tool = CreateTool(outbound: fake);

        await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["message"] = "hello",
            ["channel_id"] = "ch-1",
            ["thread_name"] = "Release notes"
        }, CancellationToken.None);

        Assert.Single(fake.Posts);
        Assert.Equal("Release notes", fake.Posts[0].ThreadName);
    }

    [Fact]
    public async Task Defaults_thread_name_when_omitted()
    {
        var fake = new FakeDiscordOutboundClient();
        var tool = CreateTool(outbound: fake);

        await ExecuteAsync(tool, "hello", channelId: "ch-1");

        Assert.Single(fake.Posts);
        Assert.Equal("Conversation", fake.Posts[0].ThreadName);
    }

    [Fact]
    public async Task Accepts_text_alias_and_snake_case_channel_id()
    {
        var fake = new FakeDiscordOutboundClient();
        var tool = CreateTool(outbound: fake);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["text"] = "hello from text alias",
            ["channel_id"] = "ch-2"
        }, CancellationToken.None);

        Assert.Contains("Message sent to channel ch-2", result);
        Assert.Single(fake.Posts);
        Assert.Equal("hello from text alias", fake.Posts[0].Text);
    }

    private static Task<string> ExecuteAsync(
        SendDiscordMessageTool tool, string message, string? channelId = null)
    {
        var args = new Dictionary<string, object?> { ["Message"] = message };
        if (channelId is not null) args["ChannelId"] = channelId;
        return tool.ExecuteAsync(args, CancellationToken.None);
    }

    private static SendDiscordMessageTool CreateTool(
        FakeDiscordOutboundClient? outbound = null,
        DiscordChannelOptions? options = null,
        Func<IActorRef?>? gatewayAccessor = null)
    {
        return new SendDiscordMessageTool(
            outbound ?? new FakeDiscordOutboundClient(),
            options ?? DefaultOptions,
            gatewayAccessor ?? (() => new FakeGatewayActor()));
    }

    private sealed class FakeDiscordOutboundClient : IDiscordOutboundClient
    {
        public bool ShouldThrow { get; init; }
        public bool ThrowThreadCreationFailure { get; init; }
        public List<(DiscordChannelId ChannelId, string Text, string ThreadName)> Posts { get; } = [];

        public Task<DiscordNewThread> PostNewThreadAsync(
            DiscordChannelId channelId, string text, string threadName, CancellationToken ct = default)
        {
            if (ShouldThrow) throw new InvalidOperationException("Discord API error");
            if (ThrowThreadCreationFailure)
            {
                throw new DiscordThreadCreationFailedException(
                    channelId,
                    new DiscordMessageId($"root-{channelId.Value}"),
                    "Root message posted but thread creation failed.",
                    new InvalidOperationException("Missing Create Public Threads permission"));
            }
            Posts.Add((channelId, text, threadName));
            // Discord convention: a thread created from a message shares its id.
            var threadId = $"thread-{channelId.Value}";
            return Task.FromResult(new DiscordNewThread(
                channelId,
                new DiscordReplyChannelId(threadId),
                new DiscordThreadOrMessageId(threadId)));
        }
    }

    /// <summary>
    /// Minimal fake that satisfies IActorRef for the Ask pattern without an
    /// actor system. Immediately responds with <see cref="ProactiveThreadAck"/>.
    /// </summary>
    private sealed class FakeGatewayActor : MinimalActorRef
    {
        public override ActorPath Path { get; } =
            new RootActorPath(Address.AllSystems) / "fake-discord-gateway";

        public override IActorRefProvider Provider =>
            throw new NotSupportedException("Not needed for tool tests");

        protected override void TellInternal(object message, IActorRef sender)
        {
            if (message is StartProactiveThread spt)
                sender.Tell(new ProactiveThreadAck(spt.SessionId));
        }
    }
}

#endregion

#region DiscordProactiveThreadActorTests (TestKit)

public sealed class DiscordProactiveThreadActorTests(ITestOutputHelper output) : TestKit(output: output)
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
    public async Task Conversation_routes_proactive_thread_to_session_binding()
    {
        var sink = CreateTestProbe("proactive-route-sink");
        var deps = CreateDependencies(
            sessionPropsFactory: (_, _, _, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(
            DiscordConversationActor.CreateProps(new DiscordChannelId("ch-1"), deps),
            "discord-proactive-route");

        conversation.Tell(new StartProactiveThread(
            new DiscordChannelId("ch-1"),
            new DiscordReplyChannelId("th-1"),
            new DiscordThreadOrMessageId("th-1"),
            new SessionId("ch-1/th-1")));

        var routed = await sink.ExpectMsgAsync<StartProactiveThread>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-1/th-1", routed.SessionId.Value);
    }

    [Fact]
    public async Task Proactive_thread_reuses_existing_session_binding()
    {
        var sink = CreateTestProbe("proactive-reuse-sink");
        var deps = CreateDependencies(
            sessionPropsFactory: (_, _, _, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(
            DiscordConversationActor.CreateProps(new DiscordChannelId("ch-1"), deps),
            "discord-proactive-reuse");

        // An inbound message first creates the session binding for th-1.
        conversation.Tell(CreateInbound(channelId: "ch-1", threadOrMessageId: "th-1", text: "first"));
        var inbound = await sink.ExpectMsgAsync<DiscordThreadInbound>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-1/th-1", inbound.SessionId.Value);

        // A proactive thread for the same thread id reuses that binding.
        conversation.Tell(new StartProactiveThread(
            new DiscordChannelId("ch-1"),
            new DiscordReplyChannelId("th-1"),
            new DiscordThreadOrMessageId("th-1"),
            new SessionId("ch-1/th-1")));

        var routed = await sink.ExpectMsgAsync<StartProactiveThread>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-1/th-1", routed.SessionId.Value);
    }

    [Fact]
    public async Task StartProactiveThread_rejected_for_disallowed_channel()
    {
        var sink = CreateTestProbe("proactive-disallowed-sink");
        var deps = CreateDependencies(
            sessionPropsFactory: (_, _, _, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(
            DiscordConversationActor.CreateProps(new DiscordChannelId("ch-99"), deps),
            "discord-proactive-disallowed");

        conversation.Tell(new StartProactiveThread(
            new DiscordChannelId("ch-99"),
            new DiscordReplyChannelId("th-1"),
            new DiscordThreadOrMessageId("th-1"),
            new SessionId("ch-99/th-1")));

        var failure = await ExpectMsgAsync<Status.Failure>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("allowed channels", failure.Cause.Message, StringComparison.OrdinalIgnoreCase);

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartProactiveThread_rejected_when_ingress_closed()
    {
        var sink = CreateTestProbe("proactive-ingress-sink");
        var gate = new SessionIngressGate();
        gate.TryClose("restart-drain");
        var deps = CreateDependencies(
            ingressGate: gate,
            sessionPropsFactory: (_, _, _, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(
            DiscordConversationActor.CreateProps(new DiscordChannelId("ch-1"), deps),
            "discord-proactive-ingress-closed");

        conversation.Tell(new StartProactiveThread(
            new DiscordChannelId("ch-1"),
            new DiscordReplyChannelId("th-1"),
            new DiscordThreadOrMessageId("th-1"),
            new SessionId("ch-1/th-1")));

        var failure = await ExpectMsgAsync<Status.Failure>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("restart-drain", failure.Cause.Message, StringComparison.OrdinalIgnoreCase);

        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProactiveThreadAck_flows_back_through_gateway()
    {
        var deps = CreateDependencies(
            sessionPropsFactory: (_, _, _, _, _, _) => Props.Create(() => new AckActor()));

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-ack-gateway");

        gateway.Tell(new StartProactiveThread(
            new DiscordChannelId("ch-1"),
            new DiscordReplyChannelId("th-1"),
            new DiscordThreadOrMessageId("th-1"),
            new SessionId("ch-1/th-1")));

        var ack = await ExpectMsgAsync<ProactiveThreadAck>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ch-1/th-1", ack.SessionId.Value);
    }

    private static DiscordGatewayDependencies CreateDependencies(
        SessionIngressGate? ingressGate = null,
        Func<SessionId, DiscordChannelId, DiscordReplyChannelId, DiscordThreadOrMessageId, DiscordMessageId?, DiscordGatewayDependencies, Props>? sessionPropsFactory = null)
    {
        return new DiscordGatewayDependencies(
            Pipeline: null!,
            IngressGate: ingressGate,
            TimeProvider: TimeProvider.System,
            Options: new DiscordChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowedChannelIds = ["ch-1"]
            },
            DefaultChannelId: null,
            ReplyClient: new UnconfiguredDiscordReplyClient(),
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            Paths: TestDiscordGatewayDeps.NewTestPaths(),
            PromptInjectionDetector: SafePromptInjectionDetector.Instance,
            SessionPropsFactory: sessionPropsFactory);
    }

    private static DiscordGatewayMessage CreateInbound(
        string channelId, string threadOrMessageId, string text)
    {
        return new DiscordGatewayMessage(
            EventId: new DiscordEventId($"ev-{threadOrMessageId}"),
            ChannelId: new DiscordChannelId(channelId),
            ReplyChannelId: new DiscordReplyChannelId(threadOrMessageId),
            MessageId: new DiscordMessageId(threadOrMessageId),
            ThreadOrMessageId: new DiscordThreadOrMessageId(threadOrMessageId),
            RootMessageId: null,
            SenderId: new DiscordUserId("u-1"),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: false,
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());
    }

    /// <summary>
    /// Actor that simulates <see cref="DiscordSessionBindingActor"/>'s proactive
    /// acknowledgement behavior.
    /// </summary>
    private sealed class AckActor : ReceiveActor
    {
        public AckActor()
        {
            Receive<StartProactiveThread>(msg =>
                Sender.Tell(new ProactiveThreadAck(msg.SessionId)));
        }
    }
}

#endregion
