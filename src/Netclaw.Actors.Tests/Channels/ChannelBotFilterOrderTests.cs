// -----------------------------------------------------------------------
// <copyright file="ChannelBotFilterOrderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Slack;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security;
using System.Diagnostics.Metrics;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// The bot filter runs before the ACL gate. A bot message from a sender the ACL denies
/// counts as filtered. A person the ACL denies still counts as dropped.
/// </summary>
/// <remarks>
/// <see cref="ChannelTelemetry"/> is static for the process. This collection does not run
/// in parallel with other test collections, so the counters have one writer.
/// </remarks>
[Collection(Name)]
public sealed class ChannelBotFilterOrderTests(ITestOutputHelper output) : TestKit(output: output), IAsyncDisposable
{
    public const string Name = "Channel telemetry counters";

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Slack_bot_message_from_ACL_denied_sender_is_filtered_not_dropped()
    {
        ChannelTelemetry.ResetForTests();
        StartListener();
        var conversation = CreateSlackConversation();

        conversation.Tell(CreateSlackMessage(botId: new SlackBotId("B1")));

        await AssertFilteredNotDropped(ChannelType.Slack);
    }

    [Fact]
    public async Task Slack_person_denied_by_ACL_is_dropped()
    {
        ChannelTelemetry.ResetForTests();
        StartListener();
        var conversation = CreateSlackConversation();

        conversation.Tell(CreateSlackMessage(botId: null));

        await AssertDroppedNotFiltered(ChannelType.Slack);
    }

    [Fact]
    public async Task Discord_bot_message_from_ACL_denied_sender_is_filtered_not_dropped()
    {
        ChannelTelemetry.ResetForTests();
        StartListener();
        var conversation = CreateDiscordConversation();

        conversation.Tell(CreateDiscordMessage(isBotMessage: true));

        await AssertFilteredNotDropped(ChannelType.Discord);
    }

    [Fact]
    public async Task Discord_person_denied_by_ACL_is_dropped()
    {
        ChannelTelemetry.ResetForTests();
        StartListener();
        var conversation = CreateDiscordConversation();

        conversation.Tell(CreateDiscordMessage(isBotMessage: false));

        await AssertDroppedNotFiltered(ChannelType.Discord);
    }

    [Fact]
    public async Task Mattermost_bot_message_from_ACL_denied_sender_is_filtered_not_dropped()
    {
        ChannelTelemetry.ResetForTests();
        StartListener();
        var conversation = CreateMattermostConversation();

        conversation.Tell(CreateMattermostMessage(isBotMessage: true));

        await AssertFilteredNotDropped(ChannelType.Mattermost);
    }

    [Fact]
    public async Task Mattermost_person_denied_by_ACL_is_dropped()
    {
        ChannelTelemetry.ResetForTests();
        StartListener();
        var conversation = CreateMattermostConversation();

        conversation.Tell(CreateMattermostMessage(isBotMessage: false));

        await AssertDroppedNotFiltered(ChannelType.Mattermost);
    }

    // RecordEventDropped and RecordEventFiltered bump the snapshot total first. The meter listener then runs and
    // records the reason. So a wait on the total does not prove that the reason exists. Each helper waits on the
    // reason itself, and only then checks the negative side.
    private async Task AssertFilteredNotDropped(ChannelType channelType)
    {
        var metrics = ChannelTelemetry.For(channelType);
        await AwaitAssertAsync(
            () =>
            {
                Assert.Equal(1, metrics.GetSnapshot().EventsFiltered);
                Assert.Equal(["bot_message"], Reasons("events.filtered"));
            },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, metrics.GetSnapshot().EventsDropped);
        Assert.Empty(Reasons("events.dropped"));
    }

    private async Task AssertDroppedNotFiltered(ChannelType channelType)
    {
        var metrics = ChannelTelemetry.For(channelType);
        await AwaitAssertAsync(
            () =>
            {
                Assert.Equal(1, metrics.GetSnapshot().EventsDropped);
                Assert.Equal([AclDenyReasons.UserNotAllowed], Reasons("events.dropped"));
            },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, metrics.GetSnapshot().EventsFiltered);
        Assert.Empty(Reasons("events.filtered"));
    }

    private readonly List<(string Instrument, string Reason)> _measurements = [];
    private MeterListener? _listener;

    private string[] Reasons(string counterSuffix)
    {
        lock (_measurements)
            return _measurements
                .Where(m => m.Instrument.EndsWith(counterSuffix, StringComparison.Ordinal))
                .Select(m => m.Reason)
                .ToArray();
    }

    // Observe the reason tag of the dropped and filtered counters.
    private void StartListener()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ChannelTelemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason")
                {
                    lock (_measurements)
                        _measurements.Add((instrument.Name, tag.Value?.ToString() ?? ""));
                }
            }
        });
        _listener.Start();
    }

    // Dispose after the actor system stops, as TestKitTeardownGuardTests requires.
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _listener?.Dispose();
        }
    }

    // ── Slack ──

    private IActorRef CreateSlackConversation()
    {
        var sink = CreateTestProbe();
        var deps = new SlackGatewayDependencies(
            Pipeline: null!,
            IngressGate: null,
            ActorSystem: null!,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                MentionOnly = false,
                AllowDirectMessages = true,
                AllowedChannelIds = ["C1"],
                AllowedUserIds = ["UALLOWED"]
            },
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: null,
            ChannelRegistry: TestSlackGatewayDeps.DefaultChannelRegistry,
            ReplyClient: new NoopReplyClient(),
            ContentScanner: new NullContentScanner(),
            ThreadHistoryFetcher: EmptyThreadHistoryFetcher.Instance,
            AudienceProfiles: TestSlackGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestSlackGatewayDeps.DefaultVisionCapableModel,
            StorageResolver: TestSessionStorageResolver.Instance,
            ThreadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)),
            PromptInjectionDetector: SafePromptInjectionDetector.Instance);

        return Sys.ActorOf(
            SlackConversationActor.CreateProps(new SlackChannelId("C1"), deps),
            $"slack-order-{Guid.NewGuid():N}");
    }

    private static SlackInboundMessage CreateSlackMessage(SlackBotId? botId) => new(
        Kind: SlackInboundKind.Message,
        EventId: new SlackEventId("C1:1"),
        ChannelId: new SlackChannelId("C1"),
        ThreadTs: null,
        EventTs: new SlackEventTs("1.1"),
        UserId: new SlackUserId("UDENIED"),
        BotId: botId,
        Text: "hello",
        Subtype: null,
        Hidden: false,
        IsDirectMessage: false);

    // ── Discord ──

    private IActorRef CreateDiscordConversation()
    {
        var sink = CreateTestProbe();
        var replyClient = new UnconfiguredDiscordReplyClient();
        var deps = new DiscordGatewayDependencies(
            Pipeline: null!,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: new DiscordChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowedChannelIds = ["ch-1"],
                AllowedUserIds = ["u-allowed"]
            },
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.DiscordWithProcessingRenderer(replyClient),
            ReplyClient: replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            StorageResolver: TestSessionStorageResolver.Instance,
            BotUserId: null,
            PromptInjectionDetector: SafePromptInjectionDetector.Instance,
            SessionPropsFactory: (_, _, _, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        return Sys.ActorOf(
            DiscordConversationActor.CreateProps(new DiscordChannelId("ch-1"), deps),
            $"discord-order-{Guid.NewGuid():N}");
    }

    private static DiscordGatewayMessage CreateDiscordMessage(bool isBotMessage) => new(
        EventId: new DiscordEventId("ev-1"),
        ChannelId: new DiscordChannelId("ch-1"),
        ReplyChannelId: new DiscordReplyChannelId("ch-1"),
        MessageId: new DiscordMessageId("m-1"),
        ThreadOrMessageId: new DiscordThreadOrMessageId("m-1"),
        RootMessageId: null,
        SenderId: new DiscordUserId("u-denied"),
        IsBotMessage: isBotMessage,
        IsDirectMessage: false,
        ContainsBotMention: false,
        Text: "hello",
        ReceivedAt: TimeProvider.System.GetUtcNow());

    // ── Mattermost ──

    private IActorRef CreateMattermostConversation()
    {
        var sink = CreateTestProbe();
        var replyClient = new UnconfiguredMattermostReplyClient();
        var deps = new MattermostGatewayDependencies(
            Pipeline: null!,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: new MattermostChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowDirectMessages = true,
                AllowedChannelIds = ["ch-1"],
                AllowedUserIds = ["u-allowed"]
            },
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.MattermostWithProcessingRenderer(replyClient),
            ReplyClient: replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
            StorageResolver: TestSessionStorageResolver.Instance,
            BotUsername: null,
            SessionPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        return Sys.ActorOf(
            MattermostConversationActor.CreateProps(new MattermostChannelId("ch-1"), deps),
            $"mm-order-{Guid.NewGuid():N}");
    }

    private static MattermostGatewayMessage CreateMattermostMessage(bool isBotMessage) => new(
        EventId: new MattermostEventId("ev-1"),
        ChannelId: new MattermostChannelId("ch-1"),
        PostId: new MattermostPostId("p-1"),
        RootPostId: new MattermostRootPostId("root-1"),
        SenderId: new MattermostUserId("u-denied"),
        IsBotMessage: isBotMessage,
        IsDirectMessage: false,
        ContainsBotMention: false,
        Text: "hello",
        ReceivedAt: TimeProvider.System.GetUtcNow());
}

[CollectionDefinition(ChannelBotFilterOrderTests.Name, DisableParallelization = true)]
public sealed class ChannelTelemetryCountersCollection;
