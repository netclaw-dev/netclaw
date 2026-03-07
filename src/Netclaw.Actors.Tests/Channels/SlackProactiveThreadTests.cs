using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Slack;
using Netclaw.Channels.Slack.Tools;
using Netclaw.Security;
using SlackNet;
using SlackNet.WebApi;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Channels;

#region SendSlackMessageTool Tests

public sealed class SendSlackMessageToolTests
{
    private static readonly SlackChannelOptions DefaultOptions = new()
    {
        AllowDirectMessages = true,
        AllowedUserIds = ["U1", "U2"],
        AllowedChannelIds = ["C1", "C2"]
    };

    [Fact]
    public async Task Rejects_when_both_channel_and_user_provided()
    {
        var tool = CreateTool();
        var result = await ExecuteAsync(tool, "hello", channelId: "C1", userId: "U1");
        Assert.Contains("exactly one", result);
    }

    [Fact]
    public async Task Rejects_when_neither_provided()
    {
        var tool = CreateTool();
        var result = await ExecuteAsync(tool, "hello");
        Assert.Contains("exactly one", result);
    }

    [Fact]
    public async Task Rejects_disallowed_user()
    {
        var tool = CreateTool();
        var result = await ExecuteAsync(tool, "hello", userId: "UBAD");
        Assert.Contains("not in the allowed users list", result);
    }

    [Fact]
    public async Task Rejects_disallowed_channel()
    {
        var tool = CreateTool();
        var result = await ExecuteAsync(tool, "hello", channelId: "CBAD");
        Assert.Contains("not in the allowed channels list", result);
    }

    [Fact]
    public async Task Allows_default_channel()
    {
        var fake = new FakeSlackOutboundClient();
        var gateway = new FakeGatewayActor();
        var tool = CreateTool(
            outbound: fake,
            gatewayAccessor: () => gateway,
            defaultChannelIdAccessor: () => new SlackChannelId("CDEFAULT"));

        var result = await ExecuteAsync(tool, "hello", channelId: "CDEFAULT");
        Assert.Contains("Message sent", result);
    }

    [Fact]
    public async Task Rejects_DM_when_AllowDirectMessages_false()
    {
        var options = new SlackChannelOptions
        {
            AllowDirectMessages = false,
            AllowedUserIds = ["U1"]
        };
        var tool = CreateTool(options: options);
        var result = await ExecuteAsync(tool, "hello", userId: "U1");
        Assert.Contains("Direct messages are disabled", result);
    }

    [Fact]
    public async Task Returns_error_when_gateway_disconnected()
    {
        var tool = CreateTool(gatewayAccessor: () => null);
        var result = await ExecuteAsync(tool, "hello", channelId: "C1");
        Assert.Contains("gateway is not connected", result);
    }

    [Fact]
    public async Task Returns_error_on_Slack_API_failure()
    {
        var fake = new FakeSlackOutboundClient { ShouldThrow = true };
        var tool = CreateTool(outbound: fake);
        var result = await ExecuteAsync(tool, "hello", channelId: "C1");
        Assert.Contains("Failed to post message to Slack", result);
    }

    [Fact]
    public async Task Returns_error_on_DM_open_failure()
    {
        var fake = new FakeSlackOutboundClient { ShouldThrow = true };
        var tool = CreateTool(outbound: fake);
        var result = await ExecuteAsync(tool, "hello", userId: "U1");
        Assert.Contains("Failed to open DM channel", result);
    }

    [Fact]
    public async Task Successful_channel_message()
    {
        var fake = new FakeSlackOutboundClient();
        var gateway = new FakeGatewayActor();
        var tool = CreateTool(outbound: fake, gatewayAccessor: () => gateway);

        var result = await ExecuteAsync(tool, "hello world", channelId: "C1");

        Assert.Contains("Message sent to channel C1", result);
        Assert.Contains("C1/", result);
        Assert.Single(fake.PostedThreads);
        Assert.Equal("C1", fake.PostedThreads[0].ChannelId.Value);
    }

    [Fact]
    public async Task Successful_DM()
    {
        var fake = new FakeSlackOutboundClient();
        var gateway = new FakeGatewayActor();
        var tool = CreateTool(outbound: fake, gatewayAccessor: () => gateway);

        var result = await ExecuteAsync(tool, "hello user", userId: "U1");

        Assert.Contains("Message sent to user U1", result);
        Assert.Single(fake.OpenedDms);
        Assert.Equal("U1", fake.OpenedDms[0].Value);
    }

    private static Task<string> ExecuteAsync(SendSlackMessageTool tool, string message,
        string? channelId = null, string? userId = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["Message"] = message
        };
        if (channelId is not null) args["ChannelId"] = channelId;
        if (userId is not null) args["UserId"] = userId;
        return tool.ExecuteAsync(args, CancellationToken.None);
    }

    private static SendSlackMessageTool CreateTool(
        FakeSlackOutboundClient? outbound = null,
        SlackChannelOptions? options = null,
        Func<SlackChannelId?>? defaultChannelIdAccessor = null,
        Func<IActorRef?>? gatewayAccessor = null)
    {
        return new SendSlackMessageTool(
            outbound ?? new FakeSlackOutboundClient(),
            options ?? DefaultOptions,
            defaultChannelIdAccessor ?? (() => null),
            gatewayAccessor ?? (() => new FakeGatewayActor()));
    }

    private sealed class FakeSlackOutboundClient : ISlackOutboundClient
    {
        public bool ShouldThrow { get; init; }
        public List<SlackUserId> OpenedDms { get; } = [];
        public List<(SlackChannelId ChannelId, string Text)> PostedThreads { get; } = [];

        public Task<SlackChannelId> OpenDmChannelAsync(SlackUserId userId, CancellationToken ct = default)
        {
            if (ShouldThrow) throw new InvalidOperationException("Slack API error");
            OpenedDms.Add(userId);
            return Task.FromResult(new SlackChannelId($"D{userId.Value}"));
        }

        public Task<SlackNewThread> PostNewThreadAsync(SlackChannelId channelId, string text, CancellationToken ct = default)
        {
            if (ShouldThrow) throw new InvalidOperationException("Slack API error");
            PostedThreads.Add((channelId, text));
            return Task.FromResult(new SlackNewThread(channelId, new SlackThreadTs("1234567890.000001")));
        }
    }

    /// <summary>
    /// Minimal fake that satisfies IActorRef for Ask pattern without an actor system.
    /// Immediately responds with ProactiveThreadAck.
    /// </summary>
    private sealed class FakeGatewayActor : MinimalActorRef
    {
        public override ActorPath Path { get; } =
            new RootActorPath(Address.AllSystems) / "fake-gateway";

        public override IActorRefProvider Provider =>
            throw new NotSupportedException("Not needed for tool tests");

        protected override void TellInternal(object message, IActorRef sender)
        {
            if (message is StartProactiveThread spt)
            {
                sender.Tell(new ProactiveThreadAck(spt.SessionId));
            }
        }
    }
}

#endregion

#region LookupSlackUserTool Tests

public sealed class LookupSlackUserToolTests
{
    [Fact]
    public async Task Matches_by_real_name()
    {
        var tool = CreateTool(CreateUsers());
        var result = await ExecuteAsync(tool, "Alice");
        Assert.Contains("U1", result);
        Assert.Contains("Alice Smith", result);
    }

    [Fact]
    public async Task Matches_by_display_name()
    {
        var tool = CreateTool(CreateUsers());
        var result = await ExecuteAsync(tool, "bobby");
        Assert.Contains("U2", result);
    }

    [Fact]
    public async Task Matches_by_email()
    {
        var tool = CreateTool(CreateUsers());
        var result = await ExecuteAsync(tool, "alice@example.com");
        Assert.Contains("U1", result);
    }

    [Fact]
    public async Task Returns_no_matches()
    {
        var tool = CreateTool(CreateUsers());
        var result = await ExecuteAsync(tool, "nonexistent");
        Assert.Contains("No matching users found", result);
    }

    [Fact]
    public async Task Filters_deleted_users()
    {
        var users = CreateUsers();
        users.Add(new User
        {
            Id = "UDEL", Name = "deleted", RealName = "Deleted Person", Deleted = true,
            Profile = new UserProfile { DisplayName = "deleted" }
        });

        var tool = CreateTool(users);
        var result = await ExecuteAsync(tool, "Deleted");
        Assert.Contains("No matching users found", result);
    }

    [Fact]
    public async Task Filters_to_allowed_users()
    {
        var options = new SlackChannelOptions { AllowedUserIds = ["U1"] };
        var tool = CreateTool(CreateUsers(), options);
        var result = await ExecuteAsync(tool, "Bob");
        Assert.Contains("No matching users found", result);
    }

    [Fact]
    public async Task Caches_on_second_call()
    {
        var fakeApi = new FakeSlackUsersApi(CreateUsers());
        var tool = new LookupSlackUserTool(fakeApi, new SlackChannelOptions(), TimeProvider.System);

        await ExecuteAsync(tool, "Alice");
        await ExecuteAsync(tool, "Bob");

        Assert.Equal(1, fakeApi.CallCount);
    }

    [Fact]
    public async Task Refreshes_after_TTL()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fakeApi = new FakeSlackUsersApi(CreateUsers());
        var tool = new LookupSlackUserTool(fakeApi, new SlackChannelOptions(), fakeTime);

        await ExecuteAsync(tool, "Alice");
        Assert.Equal(1, fakeApi.CallCount);

        // Advance past the 5-minute cache TTL
        fakeTime.Advance(TimeSpan.FromMinutes(6));

        await ExecuteAsync(tool, "Alice");
        Assert.Equal(2, fakeApi.CallCount);
    }

    private static Task<string> ExecuteAsync(LookupSlackUserTool tool, string query)
    {
        var args = new Dictionary<string, object?> { ["Query"] = query };
        return tool.ExecuteAsync(args, CancellationToken.None);
    }

    private static List<User> CreateUsers()
    {
        return
        [
            new User
            {
                Id = "U1", Name = "alice", RealName = "Alice Smith",
                Profile = new UserProfile { DisplayName = "alice_s", Email = "alice@example.com" }
            },
            new User
            {
                Id = "U2", Name = "bob", RealName = "Bob Jones",
                Profile = new UserProfile { DisplayName = "bobby", Email = "bob@example.com" }
            },
            // Bot user — should be filtered
            new User
            {
                Id = "UBOT", Name = "bot", RealName = "Bot User", IsBot = true,
                Profile = new UserProfile { DisplayName = "bot" }
            }
        ];
    }

    private static LookupSlackUserTool CreateTool(
        List<User> users,
        SlackChannelOptions? options = null)
    {
        var fakeApi = new FakeSlackUsersApi(users);
        return new LookupSlackUserTool(fakeApi, options ?? new SlackChannelOptions(), TimeProvider.System);
    }

    /// <summary>
    /// Minimal fake implementing only Users.List for the lookup tool.
    /// All other members throw.
    /// </summary>
    internal sealed class FakeSlackUsersApi : IUsersApi
    {
        private readonly List<User> _users;
        public int CallCount { get; private set; }

        public FakeSlackUsersApi(List<User> users) => _users = users;

        public Task<UserListResponse> List(string? cursor = null, bool includeLocale = false, int limit = 0,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new UserListResponse
            {
                Members = _users,
                ResponseMetadata = new ResponseMetadata { NextCursor = null }
            });
        }

        public Task<ConversationListResponse> Conversations(bool excludeArchived = false, int limit = 0,
            IEnumerable<ConversationType>? types = null, string? teamId = null, string? userId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeletePhoto(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Presence> GetPresence(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IdentityResponse> Identity(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<User> Info(string userId, bool includeLocale = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<User> LookupByEmail(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> LookupDiscoverableContact(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPhoto(byte[] image, string contentType, string? fileName = null, int? cropW = null, int? cropX = null, int? cropY = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPhoto(Stream image, string contentType, string? fileName = null, int? cropW = null, int? cropX = null, int? cropY = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPresence(Presence presence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPresence(RequestPresence presence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

#endregion

#region SlackProactiveThreadActorTests (TestKit)

public sealed class SlackProactiveThreadActorTests(ITestOutputHelper output) : TestKit(output: output)
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
    public async Task Routes_proactive_thread_to_thread_actor()
    {
        var sink = CreateTestProbe("proactive-thread-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(
            SlackConversationActor.CreateProps(new SlackChannelId("C1"), deps),
            "proactive-route-test");

        var sessionId = new SessionId("C1/100.1");
        conversation.Tell(new StartProactiveThread(
            new SlackChannelId("C1"),
            new SlackThreadTs("100.1"),
            sessionId));

        await sink.ExpectMsgAsync<StartProactiveThread>(msg =>
            Assert.Equal("C1/100.1", msg.SessionId.Value));
    }

    [Fact]
    public async Task Reuses_existing_conversation_thread_for_proactive()
    {
        var sink = CreateTestProbe("reuse-conversation-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(
            SlackConversationActor.CreateProps(new SlackChannelId("C1"), deps),
            "proactive-reuse-test");

        // First: mention starts a thread
        conversation.Tell(CreateAppMention("C1:200", "C1", "200.1", "<@UBOT> start"));
        var first = await sink.ExpectMsgAsync<SlackThreadInbound>();
        Assert.Equal("C1/200.1", first.SessionId.Value);

        // Second: proactive thread with the same thread ts reuses the existing actor
        conversation.Tell(new StartProactiveThread(
            new SlackChannelId("C1"),
            new SlackThreadTs("200.1"),
            new SessionId("C1/200.1")));

        await sink.ExpectMsgAsync<StartProactiveThread>(msg =>
            Assert.Equal("C1/200.1", msg.SessionId.Value));
    }

    [Fact]
    public async Task StartProactiveThread_rejected_for_disallowed_channel()
    {
        var sink = CreateTestProbe("disallowed-channel-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(
            SlackConversationActor.CreateProps(new SlackChannelId("CBAD"), deps),
            "proactive-disallowed-test");

        conversation.Tell(new StartProactiveThread(
            new SlackChannelId("CBAD"),
            new SlackThreadTs("400.1"),
            new SessionId("CBAD/400.1")));

        await ExpectMsgAsync<Status.Failure>(failure =>
            Assert.Contains("allowed channels", failure.Cause.Message, StringComparison.OrdinalIgnoreCase));

        // Message should be rejected before thread actor creation.
        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task StartProactiveThread_rejected_when_dm_disabled()
    {
        var sink = CreateTestProbe("dm-disabled-sink");
        var deps = CreateDependencies(
            allowDirectMessages: false,
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(
            SlackConversationActor.CreateProps(new SlackChannelId("DU1"), deps),
            "proactive-dm-disabled-test");

        conversation.Tell(new StartProactiveThread(
            new SlackChannelId("DU1"),
            new SlackThreadTs("510.1"),
            new SessionId("DU1/510.1")));

        await ExpectMsgAsync<Status.Failure>(failure =>
            Assert.Contains("Direct messages are disabled", failure.Cause.Message, StringComparison.OrdinalIgnoreCase));
        await sink.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task StartProactiveThread_allows_dm_channel_not_in_allow_list()
    {
        var sink = CreateTestProbe("dm-bypass-sink");
        // AllowedChannelIds = ["C1"] — "DU1" is NOT in the list
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(
            SlackConversationActor.CreateProps(new SlackChannelId("DU1"), deps),
            "proactive-dm-bypass-test");

        conversation.Tell(new StartProactiveThread(
            new SlackChannelId("DU1"),
            new SlackThreadTs("500.1"),
            new SessionId("DU1/500.1")));

        // DM channels bypass the channel ACL check — should be forwarded
        await sink.ExpectMsgAsync<StartProactiveThread>(msg =>
            Assert.Equal("DU1/500.1", msg.SessionId.Value));
    }

    [Fact]
    public async Task ProactiveThreadAck_flows_back_through_gateway()
    {
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) =>
                Props.Create(() => new AckActor()));

        var gateway = Sys.ActorOf(SlackGatewayActor.CreateProps(deps), "ack-flow-gateway");

        var sessionId = new SessionId("C1/300.1");
        gateway.Tell(new StartProactiveThread(
            new SlackChannelId("C1"),
            new SlackThreadTs("300.1"),
            sessionId));

        // The ack should flow back to us (the sender) through Forward chain
        await ExpectMsgAsync<ProactiveThreadAck>(ack =>
            Assert.Equal("C1/300.1", ack.SessionId.Value));
    }

    private static SlackGatewayDependencies CreateDependencies(
        bool allowDirectMessages = true,
        Func<SlackChannelId, SlackGatewayDependencies, Props>? conversationPropsFactory = null,
        Func<SessionId, SlackChannelId, SlackThreadTs, SlackGatewayDependencies, Props>? threadPropsFactory = null)
    {
        return new SlackGatewayDependencies(
            Pipeline: null!,
            ActorSystem: null!,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                MentionOnly = true,
                AllowDirectMessages = allowDirectMessages,
                AllowedChannelIds = ["C1"]
            },
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: null,
            ReplyClient: new NoopReplyClient(),
            ContentScanner: new NullContentScanner(),
            ConversationPropsFactory: conversationPropsFactory,
            ThreadPropsFactory: threadPropsFactory);
    }

    private static SlackInboundMessage CreateAppMention(
        string eventId,
        string channelId,
        string eventTs,
        string text)
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
            IsDirectMessage: false);
    }

    private sealed class ForwardActor : ReceiveActor
    {
        public ForwardActor(IActorRef target)
        {
            ReceiveAny(msg => target.Tell(msg));
        }
    }

    /// <summary>
    /// Actor that simulates SlackThreadBindingActor's proactive ack behavior.
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
