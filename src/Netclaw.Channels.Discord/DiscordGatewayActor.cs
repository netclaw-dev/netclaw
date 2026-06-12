// -----------------------------------------------------------------------
// <copyright file="DiscordGatewayActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Channels.Discord;

public sealed class DiscordGatewayActor : ChannelGatewayActor<DiscordChannelId>
{
    private readonly DiscordGatewayDependencies _dependencies;

    public DiscordGatewayActor(DiscordGatewayDependencies dependencies)
        : base(ChannelType.Discord)
    {
        _dependencies = dependencies;

        Receive<DiscordGatewayMessage>(message =>
        {
            ChannelTelemetry.For(ChannelType.Discord).RecordEventReceived("message");

            if (!TryMarkEventProcessed(message.EventId.Value))
            {
                Log.Debug("Dropping duplicate Discord event {0}", message.EventId.Value);
                ChannelTelemetry.For(ChannelType.Discord).RecordEventFiltered("duplicate_event");
                return;
            }

            var conversation = GetOrCreateConversation(message.ChannelId);

            Log.Debug("Routing Discord event {0} to conversation {1}", message.EventId.Value, message.ChannelId);
            conversation.Forward(message);
        });

        Receive<DiscordGatewayInteraction>(interaction =>
        {
            ChannelTelemetry.For(ChannelType.Discord).RecordEventReceived("interaction");

            var conversation = GetOrCreateConversation(interaction.ChannelId);

            Log.Debug("Routing Discord interaction to conversation {0}", interaction.ChannelId);
            conversation.Forward(interaction);
        });

        // Proactively-posted thread: the message is already in Discord; route
        // the wiring request to the per-channel conversation actor. Forward so
        // the ProactiveThreadAck reply flows back to the original asker.
        Receive<StartProactiveThread>(message =>
        {
            var conversation = GetOrCreateConversation(message.ChannelId);

            Log.Debug("Routing proactive thread to conversation {0}", message.ChannelId.Value);
            conversation.Forward(message);
        });
    }

    public static Props CreateProps(DiscordGatewayDependencies dependencies) =>
        Props.Create(() => new DiscordGatewayActor(dependencies));

    internal static bool TryParseDiscordSessionId(
        SessionId sessionId,
        out DiscordChannelId channelId,
        out DiscordThreadOrMessageId threadOrMessageId)
    {
        channelId = default;
        threadOrMessageId = default;

        if (!SessionIdFormat.TrySplit(sessionId, out var channelPart, out var threadPart))
            return false;

        channelId = new DiscordChannelId(channelPart);
        threadOrMessageId = new DiscordThreadOrMessageId(threadPart);
        return true;
    }

    protected override string ChannelIdValue(DiscordChannelId channelId) => channelId.Value;

    protected override Props CreateConversationProps(DiscordChannelId channelId) =>
        _dependencies.ConversationPropsFactory?.Invoke(channelId, _dependencies)
            ?? DiscordConversationActor.CreateProps(channelId, _dependencies);

    protected override bool TryParseSessionChannelId(SessionId sessionId, out DiscordChannelId channelId) =>
        TryParseDiscordSessionId(sessionId, out channelId, out _);
}

public sealed record DiscordGatewayDependencies(
    ISessionPipeline Pipeline,
    SessionIngressGate? IngressGate,
    TimeProvider TimeProvider,
    DiscordChannelOptions Options,
    DiscordChannelId? DefaultChannelId,
    IChannelRegistry ChannelRegistry,
    IDiscordReplyClient ReplyClient,
    IContentScanner ContentScanner,
    ToolAudienceProfiles AudienceProfiles,
    ModelCapabilities ModelCapabilities,
    NetclawPaths Paths,
    DiscordUserId? BotUserId = null,
    IPromptInjectionDetector? PromptInjectionDetector = null,
    IThreadHistoryFetcher? ThreadHistoryFetcher = null,
    HttpClient? HttpClient = null,
    Func<DiscordChannelId, DiscordGatewayDependencies, Props>? ConversationPropsFactory = null,
    Func<SessionId, DiscordChannelId, DiscordReplyChannelId, DiscordThreadOrMessageId, DiscordMessageId?, DiscordGatewayDependencies, Props>? SessionPropsFactory = null);
