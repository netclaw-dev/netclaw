// -----------------------------------------------------------------------
// <copyright file="MattermostGatewayActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Channels.Mattermost;

public sealed class MattermostGatewayActor : ChannelGatewayActor<MattermostChannelId>
{
    private readonly MattermostGatewayDependencies _dependencies;

    public MattermostGatewayActor(MattermostGatewayDependencies dependencies)
        : base(ChannelType.Mattermost)
    {
        _dependencies = dependencies;

        Receive<MattermostGatewayMessage>(message =>
        {
            ChannelTelemetry.For(ChannelType.Mattermost).RecordEventReceived("message");

            if (!TryMarkEventProcessed(message.EventId.Value))
            {
                Log.Debug("Dropping duplicate Mattermost event {0}", message.EventId.Value);
                ChannelTelemetry.For(ChannelType.Mattermost).RecordEventFiltered("duplicate_event");
                return;
            }

            var conversation = GetOrCreateConversation(message.ChannelId);

            Log.Debug("Routing Mattermost event {0} to conversation {1}", message.EventId.Value, message.ChannelId);
            conversation.Forward(message);
        });

        Receive<MattermostGatewayInteraction>(interaction =>
        {
            ChannelTelemetry.For(ChannelType.Mattermost).RecordEventReceived("interaction");

            var conversation = GetOrCreateConversation(interaction.ChannelId);

            Log.Debug("Routing Mattermost interaction to conversation {0}", interaction.ChannelId);
            conversation.Forward(interaction);
        });

        Receive<StartMattermostProactiveThread>(message =>
        {
            var conversation = GetOrCreateConversation(message.ChannelId);

            Log.Debug(
                "Routing StartMattermostProactiveThread session={Session} channel={Channel} rootPost={RootPost}",
                message.SessionId.Value, message.ChannelId.Value, message.RootPostId.Value);
            conversation.Forward(message);
        });
    }

    public static Props CreateProps(MattermostGatewayDependencies dependencies) =>
        Props.Create(() => new MattermostGatewayActor(dependencies));

    internal static bool TryParseMattermostSessionId(
        SessionId sessionId,
        out MattermostChannelId channelId,
        out MattermostRootPostId rootPostId)
    {
        channelId = default;
        rootPostId = default;

        if (!SessionIdFormat.TrySplit(sessionId, out var channelPart, out var threadPart))
            return false;

        channelId = new MattermostChannelId(channelPart);
        rootPostId = new MattermostRootPostId(threadPart);
        return true;
    }

    protected override string ChannelIdValue(MattermostChannelId channelId) => channelId.Value;

    protected override Props CreateConversationProps(MattermostChannelId channelId) =>
        _dependencies.ConversationPropsFactory?.Invoke(channelId, _dependencies)
            ?? MattermostConversationActor.CreateProps(channelId, _dependencies);

    protected override bool TryParseSessionChannelId(SessionId sessionId, out MattermostChannelId channelId) =>
        TryParseMattermostSessionId(sessionId, out channelId, out _);
}

public sealed record MattermostGatewayDependencies(
    ISessionPipeline Pipeline,
    SessionIngressGate? IngressGate,
    TimeProvider TimeProvider,
    MattermostChannelOptions Options,
    MattermostChannelId? DefaultChannelId,
    IMattermostReplyClient ReplyClient,
    IContentScanner ContentScanner,
    ToolAudienceProfiles AudienceProfiles,
    ModelCapabilities ModelCapabilities,
    NetclawPaths Paths,
    string? ServerUrl = null,
    string? CallbackUrl = null,
    MattermostUserId? BotUserId = null,
    string? BotUsername = null,
    IPromptInjectionDetector? PromptInjectionDetector = null,
    IThreadHistoryFetcher? ThreadHistoryFetcher = null,
    MattermostCallbackActionStore? CallbackActionStore = null,
    HttpClient? HttpClient = null,
    Func<MattermostChannelId, MattermostGatewayDependencies, Props>? ConversationPropsFactory = null,
    Func<SessionId, MattermostChannelId, MattermostRootPostId, MattermostGatewayDependencies, Props>? SessionPropsFactory = null);
