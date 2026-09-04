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
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

#region DiscordProactiveOutboundClient Tests

/// <summary>
/// Discord-specific proactive send behavior (default-channel bypass,
/// thread-creation partial success, default thread name). The cross-channel
/// canonical outcomes (ACL denials, DM gate, gateway availability,
/// success/nack strings) live in
/// <see cref="Contracts.DiscordProactiveOutboundClientContractTests"/>.
/// </summary>
public sealed class DiscordProactiveOutboundClientTests
{
    private static readonly DiscordChannelOptions DefaultOptions = new()
    {
        Enabled = true,
        AllowDirectMessages = true,
        AllowedUserIds = ["u-1", "u-2"],
        AllowedChannelIds = ["ch-1", "ch-2"]
    };

    [Fact]
    public async Task Allows_default_channel_not_in_allow_list()
    {
        var options = new DiscordChannelOptions
        {
            Enabled = true,
            DefaultChannelId = "ch-default"
        };
        var fake = new FakeDiscordOutboundClient();
        var client = CreateClient(outbound: fake, options: options);

        var result = await SendAsync(client, "hello", channelId: "ch-default");

        Assert.Contains("Message sent to channel ch-default", result);
        Assert.Single(fake.Posts);
        Assert.Equal("ch-default", fake.Posts[0].ChannelId.Value);
    }

    [Fact]
    public async Task Returns_error_on_post_failure()
    {
        var fake = new FakeDiscordOutboundClient { ShouldThrow = true };
        var client = CreateClient(outbound: fake);
        var result = await SendAsync(client, "hello", channelId: "ch-1");
        Assert.Contains("Failed to post message to Discord", result);
    }

    [Fact]
    public async Task Returns_partial_success_when_thread_creation_fails_after_message_post()
    {
        var fake = new FakeDiscordOutboundClient { ThrowThreadCreationFailure = true };
        var client = CreateClient(outbound: fake);

        var result = await SendAsync(client, "hello", channelId: "ch-1");

        Assert.DoesNotContain("Error:", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Message sent to channel ch-1", result);
        Assert.Contains("could not create a follow-up thread", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("root-ch-1", result);
    }

    [Fact]
    public async Task Successful_channel_message_posts_and_wires_session()
    {
        var fake = new FakeDiscordOutboundClient();
        var client = CreateClient(outbound: fake);

        var result = await SendAsync(client, "hello world", channelId: "ch-1");

        Assert.Equal("discord", client.Key.Value);
        Assert.Contains("Message sent to channel ch-1", result);
        Assert.Contains("ch-1/", result);
        Assert.Single(fake.Posts);
        Assert.Equal("ch-1", fake.Posts[0].ChannelId.Value);
        Assert.Equal("hello world", fake.Posts[0].Text);
    }

    [Fact]
    public async Task Successful_dm_uses_allowed_user_id()
    {
        var fake = new FakeDiscordOutboundClient();
        var client = CreateClient(outbound: fake);

        var result = await SendAsync(client, "hello user", userId: "u-1");

        Assert.Contains("Message sent to user u-1", result);
        Assert.Single(fake.DirectMessages);
        Assert.Equal("u-1", fake.DirectMessages[0].UserId.Value);
        Assert.Equal("hello user", fake.DirectMessages[0].Text);
    }

    [Fact]
    public async Task Channel_posts_create_thread_with_default_name()
    {
        var fake = new FakeDiscordOutboundClient();
        var client = CreateClient(outbound: fake);

        await SendAsync(client, "hello", channelId: "ch-1");

        Assert.Single(fake.Posts);
        Assert.Equal("Conversation", fake.Posts[0].ThreadName);
    }

    private static Task<string> SendAsync(
        DiscordProactiveOutboundClient client, string text,
        string? channelId = null, string? userId = null)
    {
        var request = userId is not null
            ? new ChannelSendRequest(ChannelAddressKind.DirectMessage, userId, text)
            : new ChannelSendRequest(ChannelAddressKind.Destination, channelId!, text);
        return client.SendMessageAsync(request, CancellationToken.None);
    }

    private static DiscordProactiveOutboundClient CreateClient(
        FakeDiscordOutboundClient? outbound = null,
        DiscordChannelOptions? options = null,
        Func<IActorRef?>? gatewayAccessor = null)
    {
        return new DiscordProactiveOutboundClient(
            outbound ?? new FakeDiscordOutboundClient(),
            options ?? DefaultOptions,
            gatewayAccessor ?? (() => AckGateway()));
    }

    private static FakeProactiveGateway AckGateway() =>
        new(msg => msg is StartProactiveThread spt ? new ProactiveThreadAck(spt.SessionId) : null);
}

#endregion

#region DiscordAddressResolver Tests

public sealed class DiscordAddressResolverTests
{
    private const string AllowedUserId = "123456789012345678";
    private const string OtherUserId = "234567890123456789";
    private const string AllowedChannelId = "345678901234567890";
    private const string OtherChannelId = "456789012345678901";

    [Fact]
    public async Task User_resolver_resolves_exact_user_id_without_directory_lookup()
    {
        var resolver = CreateResolver(new DiscordChannelOptions
        {
            AllowDirectMessages = true,
            AllowedUserIds = [AllowedUserId]
        });

        var result = await resolver.ResolveAsync(new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Discord),
            ChannelAddressKind.User,
            AllowedUserId), TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Resolved, result.Status);
        Assert.Equal(AllowedUserId, result.RequireSingle().StableId);
    }

    [Fact]
    public async Task User_resolver_filters_exact_user_id_through_allowed_users()
    {
        var resolver = CreateResolver(new DiscordChannelOptions
        {
            AllowDirectMessages = true,
            AllowedUserIds = [AllowedUserId]
        });

        var result = await resolver.ResolveAsync(new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Discord),
            ChannelAddressKind.User,
            OtherUserId), TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.NotFound, result.Status);
        Assert.Contains("allowed users", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Direct_message_resolution_requires_direct_messages_enabled()
    {
        var resolver = CreateResolver(new DiscordChannelOptions
        {
            AllowDirectMessages = false,
            AllowedUserIds = [AllowedUserId]
        });

        var result = await resolver.ResolveAsync(new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Discord),
            ChannelAddressKind.DirectMessage,
            AllowedUserId), TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Unsupported, result.Status);
        Assert.Contains("disabled", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Destination_resolver_resolves_channel_mention()
    {
        var resolver = CreateResolver(new DiscordChannelOptions
        {
            AllowedChannelIds = [AllowedChannelId]
        });

        var result = await resolver.ResolveAsync(new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Discord),
            ChannelAddressKind.Destination,
            $"<#{AllowedChannelId}>"), TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Resolved, result.Status);
        Assert.Equal(AllowedChannelId, result.RequireSingle().StableId);
    }

    [Fact]
    public async Task Destination_resolver_filters_exact_channel_id_through_allowed_channels()
    {
        var resolver = CreateResolver(new DiscordChannelOptions
        {
            AllowedChannelIds = [AllowedChannelId]
        });

        var result = await resolver.ResolveAsync(new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Discord),
            ChannelAddressKind.Destination,
            OtherChannelId), TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.NotFound, result.Status);
        Assert.Contains("allowed channels", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task User_resolver_resolves_cached_name_matches()
    {
        var lookup = new FakeDiscordAddressLookupClient
        {
            Users =
            [
                new DiscordLookupUser(new DiscordUserId(AllowedUserId), "alice", "Alice", "Alice A.", IsBot: false)
            ]
        };
        var resolver = CreateResolver(new DiscordChannelOptions
        {
            AllowDirectMessages = true,
            AllowedUserIds = [AllowedUserId]
        }, lookup);

        var result = await resolver.ResolveAsync(new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Discord),
            ChannelAddressKind.DirectMessage,
            "@alice"), TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Resolved, result.Status);
        Assert.Equal(AllowedUserId, result.RequireSingle().StableId);
        Assert.Equal(ChannelAddressKind.DirectMessage, result.RequireSingle().AddressKind);
    }

    [Fact]
    public async Task List_destinations_applies_channel_acl()
    {
        var lookup = new FakeDiscordAddressLookupClient
        {
            Destinations =
            [
                new DiscordLookupDestination(new DiscordChannelId("100000000000000001"), "general"),
                new DiscordLookupDestination(new DiscordChannelId("100000000000000002"), "secret-ops"),
                new DiscordLookupDestination(new DiscordChannelId("100000000000000003"), "announcements")
            ]
        };
        var resolver = CreateResolver(new DiscordChannelOptions
        {
            AllowedChannelIds = ["100000000000000001", "100000000000000003"]
        }, lookup);

        var result = await resolver.ListDestinationsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Listed, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, c => c.StableId == "100000000000000001" && c.DisplayName == "#general");
        Assert.Contains(result.Candidates, c => c.StableId == "100000000000000003" && c.DisplayName == "#announcements");
        Assert.DoesNotContain(result.Candidates, c => c.StableId == "100000000000000002");
    }

    private static DiscordAddressResolver CreateResolver(
        DiscordChannelOptions options,
        FakeDiscordAddressLookupClient? lookup = null)
    {
        return new DiscordAddressResolver(
            lookup ?? new FakeDiscordAddressLookupClient(),
            options,
            () => null);
    }

    private sealed class FakeDiscordAddressLookupClient : IDiscordAddressLookupClient
    {
        public IReadOnlyList<DiscordLookupUser> Users { get; init; } = [];
        public IReadOnlyList<DiscordLookupDestination> Destinations { get; init; } = [];

        public ValueTask<IReadOnlyList<DiscordLookupUser>> FindUsersAsync(
            string query,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Users);

        public ValueTask<IReadOnlyList<DiscordLookupDestination>> FindDestinationsAsync(
            string query,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Destinations);

        public ValueTask<IReadOnlyList<DiscordLookupDestination>> ListDestinationsAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Destinations);
    }
}

#endregion

#region DiscordProactiveThreadActorTests (TestKit)

public sealed class DiscordProactiveThreadActorTests(ITestOutputHelper output) : TestKit(output: output)
{
    // akka.test.single-expect-default raises the parameterless
    // ExpectMsgAsync<ProactiveThreadAck> wait below. The stock 3s value
    // measures scheduler load on a starved CI runner, not correctness.
    // Production allows 30s for the same ack — see
    // ProactiveSendFormatting.ProactiveThreadAckTimeout.
    protected override Config? Config =>
        ConfigurationFactory.ParseString("""
            akka.test.default-timeout = 5s
            akka.test.single-expect-default = 15s
            """);

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
    public async Task StartProactiveThread_allows_dm_channel_when_user_is_allowed()
    {
        var sink = CreateTestProbe("proactive-dm-sink");
        var deps = CreateDependencies(
            options: new DiscordChannelOptions
            {
                Enabled = true,
                AllowDirectMessages = true,
                AllowedChannelIds = ["ch-1"],
                AllowedUserIds = ["u-1"]
            },
            sessionPropsFactory: (_, _, _, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(
            DiscordConversationActor.CreateProps(new DiscordChannelId("dm-1"), deps),
            "discord-proactive-dm");

        conversation.Tell(new StartProactiveThread(
            new DiscordChannelId("dm-1"),
            new DiscordReplyChannelId("dm-1"),
            new DiscordThreadOrMessageId("msg-1"),
            new SessionId("dm-1/msg-1"),
            DirectMessageUserId: new DiscordUserId("u-1"),
            RootMessageId: new DiscordMessageId("msg-1")));

        var routed = await sink.ExpectMsgAsync<StartProactiveThread>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("dm-1/msg-1", routed.SessionId.Value);
        Assert.Equal("msg-1", routed.RootMessageId?.Value);
    }

    [Fact]
    public async Task StartProactiveThread_rejects_dm_when_user_is_disallowed()
    {
        var sink = CreateTestProbe("proactive-dm-disallowed-sink");
        var deps = CreateDependencies(
            options: new DiscordChannelOptions
            {
                Enabled = true,
                AllowDirectMessages = true,
                AllowedUserIds = ["u-1"]
            },
            sessionPropsFactory: (_, _, _, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(
            DiscordConversationActor.CreateProps(new DiscordChannelId("dm-2"), deps),
            "discord-proactive-dm-disallowed");

        conversation.Tell(new StartProactiveThread(
            new DiscordChannelId("dm-2"),
            new DiscordReplyChannelId("dm-2"),
            new DiscordThreadOrMessageId("msg-2"),
            new SessionId("dm-2/msg-2"),
            DirectMessageUserId: new DiscordUserId("u-bad"),
            RootMessageId: new DiscordMessageId("msg-2")));

        var failure = await ExpectMsgAsync<Status.Failure>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("allowed users", failure.Cause.Message, StringComparison.OrdinalIgnoreCase);

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
        DiscordChannelOptions? options = null,
        Func<SessionId, DiscordChannelId, DiscordReplyChannelId, DiscordThreadOrMessageId, DiscordMessageId?, DiscordGatewayDependencies, Props>? sessionPropsFactory = null)
    {
        var replyClient = new UnconfiguredDiscordReplyClient();

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
            ChannelRegistry: TestChannelRegistries.DiscordWithProcessingRenderer(replyClient),
            ReplyClient: replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
                        StorageResolver: Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance,
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
