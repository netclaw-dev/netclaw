// -----------------------------------------------------------------------
// <copyright file="DiscordGatewayContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Discord;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class DiscordGatewayContractTests(ITestOutputHelper output)
    : GatewayRoutingContractTests(output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    protected override IActorRef CreateGateway(ChannelOptionsBuilder options)
    {
        var discordOptions = new DiscordChannelOptions
        {
            AllowedChannelIds = options.AllowedChannelIds,
            AllowedUserIds = options.AllowedUserIds,
            AllowDirectMessages = options.AllowDirectMessages,
            ChannelAudiences = options.ChannelAudiences
        };

        var defaultChannelId = options.DefaultChannelId is not null
            ? new DiscordChannelId(options.DefaultChannelId)
            : (DiscordChannelId?)null;

        // Wire a real DiscordConversationActor (which performs ACL) with a
        // SessionPropsFactory that routes accepted messages to the test probe.
        var deps = new DiscordGatewayDependencies(
            Pipeline: new FailingSessionPipeline(new InvalidOperationException("not used")),
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: discordOptions,
            DefaultChannelId: defaultChannelId,
            ReplyClient: new RecordingDiscordReplyClient(),
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            Paths: TestDiscordGatewayDeps.NewTestPaths(),
            PromptInjectionDetector: SafePromptInjectionDetector.Instance,
            SessionPropsFactory: (sid, chId, replyId, threadId, rootId, d) =>
                Props.Create(() => new ForwardActor(TestActor)));

        return Sys.ActorOf(DiscordGatewayActor.CreateProps(deps));
    }

    protected override object CreateAllowedMessage(
        string channelId, string threadId, string userId, string text, string eventId)
        => new DiscordGatewayMessage(
            EventId: new DiscordEventId(eventId),
            ChannelId: new DiscordChannelId(channelId),
            ReplyChannelId: new DiscordReplyChannelId(threadId),
            MessageId: new DiscordMessageId("msg-1"),
            ThreadOrMessageId: new DiscordThreadOrMessageId(threadId),
            RootMessageId: null,
            SenderId: new DiscordUserId(userId),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: true,
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());

    protected override object CreateDeniedMessage(
        string channelId, string userId, string eventId)
        => new DiscordGatewayMessage(
            EventId: new DiscordEventId(eventId),
            ChannelId: new DiscordChannelId(channelId),
            ReplyChannelId: new DiscordReplyChannelId("reply-1"),
            MessageId: new DiscordMessageId("msg-1"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-1"),
            RootMessageId: null,
            SenderId: new DiscordUserId(userId),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: true,
            Text: "denied",
            ReceivedAt: TimeProvider.System.GetUtcNow());
}
