// -----------------------------------------------------------------------
// <copyright file="MattermostGatewayContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Mattermost;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class MattermostGatewayContractTests(ITestOutputHelper output)
    : GatewayRoutingContractTests(output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    protected override IActorRef CreateGateway(ChannelOptionsBuilder options)
    {
        var mattermostOptions = new MattermostChannelOptions
        {
            AllowedChannelIds = options.AllowedChannelIds,
            AllowedUserIds = options.AllowedUserIds,
            AllowDirectMessages = options.AllowDirectMessages,
            ChannelAudiences = options.ChannelAudiences
        };

        var defaultChannelId = options.DefaultChannelId is not null
            ? new MattermostChannelId(options.DefaultChannelId)
            : (MattermostChannelId?)null;

        var deps = new MattermostGatewayDependencies(
            Pipeline: new FailingSessionPipeline(new InvalidOperationException("not used")),
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: mattermostOptions,
            DefaultChannelId: defaultChannelId,
            ReplyClient: new RecordingMattermostReplyClient(),
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestMattermostGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestMattermostGatewayDeps.DefaultVisionCapableModel,
                        StorageResolver: Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance,
            SessionPropsFactory: (sid, chId, rootPostId, d) =>
                Props.Create(() => new ForwardActor(TestActor)));

        return Sys.ActorOf(MattermostGatewayActor.CreateProps(deps));
    }

    protected override object CreateAllowedMessage(
        string channelId, string threadId, string userId, string text, string eventId)
        => new MattermostGatewayMessage(
            EventId: new MattermostEventId(eventId),
            ChannelId: new MattermostChannelId(channelId),
            PostId: new MattermostPostId("post-1"),
            RootPostId: new MattermostRootPostId(threadId),
            SenderId: new MattermostUserId(userId),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: true,
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());

    protected override object CreateDeniedMessage(
        string channelId, string userId, string eventId)
        => new MattermostGatewayMessage(
            EventId: new MattermostEventId(eventId),
            ChannelId: new MattermostChannelId(channelId),
            PostId: new MattermostPostId("post-1"),
            RootPostId: new MattermostRootPostId("thread-1"),
            SenderId: new MattermostUserId(userId),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: true,
            Text: "denied",
            ReceivedAt: TimeProvider.System.GetUtcNow());
}
