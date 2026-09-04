// -----------------------------------------------------------------------
// <copyright file="SlackGatewayContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class SlackGatewayContractTests(ITestOutputHelper output)
    : GatewayRoutingContractTests(output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    protected override IActorRef CreateGateway(ChannelOptionsBuilder options)
    {
        var slackOptions = new SlackChannelOptions
        {
            Enabled = true,
            MentionOnly = false,
            AllowDirectMessages = options.AllowDirectMessages,
            AllowedChannelIds = options.AllowedChannelIds,
            AllowedUserIds = options.AllowedUserIds,
            ChannelAudiences = options.ChannelAudiences,
            BotToken = new SensitiveString("xoxb-fake")
        };

        var defaultChannelId = options.DefaultChannelId is not null
            ? new SlackChannelId(options.DefaultChannelId)
            : (SlackChannelId?)null;

        // Wire the real SlackConversationActor (which performs ACL) with a
        // ThreadPropsFactory that routes accepted messages to the test probe.
        var deps = new SlackGatewayDependencies(
            Pipeline: new FailingSessionPipeline(new InvalidOperationException("not used")),
            IngressGate: null,
            ActorSystem: Sys,
            TimeProvider: TimeProvider.System,
            Options: slackOptions,
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: defaultChannelId,
            ChannelRegistry: TestSlackGatewayDeps.DefaultChannelRegistry,
            ReplyClient: new RecordingSlackReplyClient(),
            ContentScanner: new NullContentScanner(),
            ThreadHistoryFetcher: EmptyThreadHistoryFetcher.Instance,
            AudienceProfiles: TestSlackGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestSlackGatewayDeps.DefaultVisionCapableModel,
                        StorageResolver: Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance,
            PromptInjectionDetector: SafePromptInjectionDetector.Instance,
            ThreadPropsFactory: (sid, chId, threadTs, d) =>
                Props.Create(() => new ForwardActor(TestActor)));

        return Sys.ActorOf(SlackGatewayActor.CreateProps(deps));
    }

    protected override object CreateAllowedMessage(
        string channelId, string threadId, string userId, string text, string eventId)
        => new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId(eventId),
            ChannelId: new SlackChannelId(channelId),
            ThreadTs: new SlackThreadTs(threadId),
            EventTs: new SlackEventTs("1000.1"),
            UserId: new SlackUserId(userId),
            BotId: null,
            Text: text,
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false);

    protected override object CreateDeniedMessage(
        string channelId, string userId, string eventId)
        => new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId(eventId),
            ChannelId: new SlackChannelId(channelId),
            ThreadTs: new SlackThreadTs("thread-1"),
            EventTs: new SlackEventTs("1000.1"),
            UserId: new SlackUserId(userId),
            BotId: null,
            Text: "denied",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false);
}
