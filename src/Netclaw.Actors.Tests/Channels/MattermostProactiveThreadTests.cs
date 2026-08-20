// -----------------------------------------------------------------------
// <copyright file="MattermostProactiveThreadTests.cs" company="Petabridge, LLC">
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
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Mattermost.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Mattermost-specific proactive send behavior. The cross-channel canonical
/// outcomes (ACL denials, DM gate, gateway availability, success/nack strings)
/// live in <see cref="Contracts.MattermostProactiveOutboundClientContractTests"/>.
/// </summary>
public sealed class MattermostProactiveOutboundClientTests
{
    private static readonly MattermostChannelOptions DefaultOptions = new()
    {
        AllowDirectMessages = true,
        AllowedUserIds = ["u-1", "u-2"],
        AllowedChannelIds = ["ch-1", "ch-2"]
    };

    [Fact]
    public async Task Successful_dm_uses_allowed_user_id()
    {
        var fake = new FakeMattermostOutboundClient();
        var client = CreateClient(outbound: fake);

        var result = await SendAsync(client, "hello user", userId: "u-1");

        Assert.Contains("Message sent to user u-1", result);
        Assert.Single(fake.OpenedDms);
        Assert.Equal("u-1", fake.OpenedDms[0].Value);
    }

    [Fact]
    public async Task Successful_channel_message_posts_and_wires_session()
    {
        var fake = new FakeMattermostOutboundClient();
        var client = CreateClient(outbound: fake);

        var result = await SendAsync(client, "hello channel", channelId: "ch-1");

        Assert.Equal("mattermost", client.Key.Value);
        Assert.Contains("Message sent to channel ch-1", result);
        Assert.Single(fake.PostedThreads);
        Assert.Equal("ch-1", fake.PostedThreads[0].ChannelId.Value);
        Assert.Equal("hello channel", fake.PostedThreads[0].Text);
    }

    private static Task<string> SendAsync(
        MattermostProactiveOutboundClient client,
        string text,
        string? channelId = null,
        string? userId = null)
    {
        var request = userId is not null
            ? new ChannelSendRequest(ChannelAddressKind.DirectMessage, userId, text)
            : new ChannelSendRequest(ChannelAddressKind.Destination, channelId!, text);
        return client.SendMessageAsync(request, CancellationToken.None);
    }

    private static MattermostProactiveOutboundClient CreateClient(
        FakeMattermostOutboundClient? outbound = null,
        MattermostChannelOptions? options = null,
        Func<MattermostChannelId?>? defaultChannelIdAccessor = null,
        Func<IActorRef?>? gatewayAccessor = null)
    {
        return new MattermostProactiveOutboundClient(
            outbound ?? new FakeMattermostOutboundClient(),
            options ?? DefaultOptions,
            defaultChannelIdAccessor ?? (() => null),
            gatewayAccessor ?? (() => AckGateway()));
    }

    private static FakeProactiveGateway AckGateway() =>
        new(msg => msg is StartMattermostProactiveThread spt
            ? new MattermostProactiveThreadAck(spt.SessionId)
            : null);
}

public sealed class MattermostAddressResolverTests
{
    private const string AllowedUserId = "abcdefghijklmnopqrstuvwxyz";
    private const string OtherUserId = "bcdefghijklmnopqrstuvwxyza";
    private const string AllowedChannelId = "12345678901234567890123456";
    private const string OtherChannelId = "23456789012345678901234567";

    [Fact]
    public async Task User_resolver_resolves_exact_user_id_without_directory_lookup()
    {
        var tool = CreateUserResolver(new MattermostChannelOptions
        {
            AllowedUserIds = [AllowedUserId]
        });
        var request = new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost),
            ChannelAddressKind.User,
            AllowedUserId);

        var result = await tool.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Resolved, result.Status);
        Assert.Equal(AllowedUserId, result.RequireSingle().StableId);
    }

    [Fact]
    public async Task User_resolver_filters_exact_user_id_through_allowed_users()
    {
        var tool = CreateUserResolver(new MattermostChannelOptions
        {
            AllowedUserIds = [AllowedUserId]
        });
        var request = new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost),
            ChannelAddressKind.User,
            OtherUserId);

        var result = await tool.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.NotFound, result.Status);
        Assert.Contains("not in the allowed users list", result.Error);
    }

    [Fact]
    public async Task Destination_resolver_resolves_exact_channel_id()
    {
        var resolver = new MattermostDestinationAddressResolver(
            new MattermostChannelOptions { AllowedChannelIds = [AllowedChannelId] },
            () => null);
        var request = new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost),
            ChannelAddressKind.Destination,
            $"channel:{AllowedChannelId}");

        var result = await resolver.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Resolved, result.Status);
        Assert.Equal(AllowedChannelId, result.RequireSingle().StableId);
    }

    [Fact]
    public async Task Destination_resolver_filters_exact_channel_id_through_allowed_channels()
    {
        var resolver = new MattermostDestinationAddressResolver(
            new MattermostChannelOptions { AllowedChannelIds = [AllowedChannelId] },
            () => null);
        var request = new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost),
            ChannelAddressKind.Destination,
            OtherChannelId);

        var result = await resolver.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.NotFound, result.Status);
        Assert.Contains("not in the allowed channels list", result.Error);
    }

    [Fact]
    public async Task List_destinations_enumerates_default_channel_and_allowlist()
    {
        var resolver = new MattermostDestinationAddressResolver(
            new MattermostChannelOptions { AllowedChannelIds = [AllowedChannelId] },
            () => new MattermostChannelId(OtherChannelId));

        var result = await resolver.ListDestinationsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Listed, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, c => c.StableId == AllowedChannelId);
        Assert.Contains(result.Candidates, c => c.StableId == OtherChannelId);
    }

    [Fact]
    public async Task List_destinations_is_empty_when_nothing_is_configured()
    {
        var resolver = new MattermostDestinationAddressResolver(
            new MattermostChannelOptions(),
            () => null);

        var result = await resolver.ListDestinationsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Listed, result.Status);
        Assert.Empty(result.Candidates);
    }

    private static LookupMattermostUserTool CreateUserResolver(MattermostChannelOptions options)
    {
        return new LookupMattermostUserTool(
            () => throw new InvalidOperationException("Directory lookup should not be used for exact IDs."),
            options);
    }
}

/// <summary>
/// Drives <see cref="StartMattermostProactiveThread"/> through the real
/// <see cref="MattermostConversationActor"/> ACL path. Regression coverage for
/// proactive DMs: DM channel ids are ephemeral and never allowlisted, so the
/// conversation actor must validate the user ACL for DMs (Discord parity)
/// instead of nacking every proactive DM via the channel ACL.
/// </summary>
public sealed class MattermostProactiveThreadActorTests(ITestOutputHelper output) : TestKit(output: output)
{
    // akka.test.single-expect-default raises the parameterless
    // ExpectMsgAsync<MattermostProactiveThreadAck> wait below. The stock 3s
    // value measures scheduler load on a starved CI runner, not correctness.
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
    public async Task StartProactiveThread_acks_dm_channel_when_user_is_allowed()
    {
        var conversation = CreateConversation("dm-u-1", new MattermostChannelOptions
        {
            Enabled = true,
            AllowDirectMessages = true,
            AllowedChannelIds = ["ch-1"],
            AllowedUserIds = ["u-1"]
        });

        conversation.Tell(new StartMattermostProactiveThread(
            new MattermostChannelId("dm-u-1"),
            new MattermostRootPostId("root-1"),
            new SessionId("dm-u-1/root-1"),
            DirectMessageUserId: new MattermostUserId("u-1")));

        var ack = await ExpectMsgAsync<MattermostProactiveThreadAck>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("dm-u-1/root-1", ack.SessionId.Value);
    }

    [Fact]
    public async Task StartProactiveThread_nacks_dm_when_user_is_disallowed()
    {
        var conversation = CreateConversation("dm-u-bad", new MattermostChannelOptions
        {
            Enabled = true,
            AllowDirectMessages = true,
            AllowedUserIds = ["u-1"]
        });

        conversation.Tell(new StartMattermostProactiveThread(
            new MattermostChannelId("dm-u-bad"),
            new MattermostRootPostId("root-1"),
            new SessionId("dm-u-bad/root-1"),
            DirectMessageUserId: new MattermostUserId("u-bad")));

        var nack = await ExpectMsgAsync<CommandNack>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("allowed users", nack.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartProactiveThread_nacks_dm_when_direct_messages_disabled()
    {
        var conversation = CreateConversation("dm-u-1", new MattermostChannelOptions
        {
            Enabled = true,
            AllowDirectMessages = false,
            AllowedUserIds = ["u-1"]
        });

        conversation.Tell(new StartMattermostProactiveThread(
            new MattermostChannelId("dm-u-1"),
            new MattermostRootPostId("root-1"),
            new SessionId("dm-u-1/root-1"),
            DirectMessageUserId: new MattermostUserId("u-1")));

        var nack = await ExpectMsgAsync<CommandNack>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("direct messages are disabled", nack.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartProactiveThread_nacks_disallowed_channel_without_dm_marker()
    {
        var conversation = CreateConversation("ch-99", new MattermostChannelOptions
        {
            Enabled = true,
            AllowDirectMessages = true,
            AllowedChannelIds = ["ch-1"],
            AllowedUserIds = ["u-1"]
        });

        conversation.Tell(new StartMattermostProactiveThread(
            new MattermostChannelId("ch-99"),
            new MattermostRootPostId("root-1"),
            new SessionId("ch-99/root-1")));

        var nack = await ExpectMsgAsync<CommandNack>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("allowed channels", nack.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private IActorRef CreateConversation(string channelId, MattermostChannelOptions options)
    {
        var deps = new MattermostGatewayDependencies(
            Pipeline: null!,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: options,
            DefaultChannelId: null,
            ReplyClient: new UnconfiguredMattermostReplyClient(),
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
            Paths: TestMattermostGatewayDeps.NewTestPaths(),
            SessionPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(TestActor)));

        return Sys.ActorOf(
            MattermostConversationActor.CreateProps(new MattermostChannelId(channelId), deps),
            $"mm-proactive-{channelId}-{Guid.NewGuid():N}");
    }
}
