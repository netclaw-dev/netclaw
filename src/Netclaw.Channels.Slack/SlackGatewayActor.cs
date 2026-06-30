// -----------------------------------------------------------------------
// <copyright file="SlackGatewayActor.cs" company="Petabridge, LLC">
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

namespace Netclaw.Channels.Slack;

public sealed class SlackGatewayActor : ChannelGatewayActor<SlackChannelId>
{
    private readonly SlackGatewayDependencies _dependencies;

    public SlackGatewayActor(SlackGatewayDependencies dependencies)
        : base(ChannelType.Slack)
    {
        _dependencies = dependencies;

        Receive<SlackInboundMessage>(message =>
        {
            ChannelTelemetry.For(ChannelType.Slack).RecordEventReceived(message.Kind.ToString());

            if (!TryMarkEventProcessed(message.EventId.Value))
            {
                Log.Debug("Dropping duplicate Slack event {0}", message.EventId);
                ChannelTelemetry.For(ChannelType.Slack).RecordEventFiltered("duplicate_event");
                return;
            }

            var conversation = GetOrCreateConversation(message.ChannelId);

            Log.Debug("Routing Slack event {0} to conversation {1}", message.EventId, message.ChannelId);
            ChannelTelemetry.For(ChannelType.Slack).RecordEventRouted(message.Kind.ToString());
            conversation.Forward(message);
        });

        Receive<StartProactiveThread>(message =>
        {
            var conversation = GetOrCreateConversation(message.ChannelId);

            Log.Debug("Routing proactive thread to conversation {0}", message.ChannelId);
            conversation.Forward(message);
        });

        Receive<SlackApprovalResponse>(message =>
        {
            var conversation = GetOrCreateConversation(message.ChannelId);

            Log.Info("Routing Slack approval response for channel {0} thread={1} call={2}",
                message.ChannelId, message.ThreadTs, message.CallId);
            conversation.Forward(message);
        });
    }

    internal static bool TryParseSlackSessionId(
        SessionId sessionId,
        out SlackChannelId channelId,
        out SlackThreadTs threadTs)
    {
        channelId = default!;
        threadTs = default!;

        if (!SessionIdFormat.TrySplit(sessionId, out var channelPart, out var threadPart))
            return false;

        channelId = new SlackChannelId(channelPart);
        threadTs = new SlackThreadTs(threadPart);
        return true;
    }

    public static Props CreateProps(SlackGatewayDependencies dependencies) =>
        Props.Create(() => new SlackGatewayActor(dependencies));

    /// <summary>
    /// Some legitimate SlackNet event shapes omit the envelope event id, so an
    /// id-less Slack event is processed (without dedup protection) rather than
    /// rejected.
    /// </summary>
    protected override bool OnMissingEventId() => true;

    protected override string ChannelIdValue(SlackChannelId channelId) => channelId.Value;

    protected override Props CreateConversationProps(SlackChannelId channelId) =>
        _dependencies.ConversationPropsFactory?.Invoke(channelId, _dependencies)
            ?? SlackConversationActor.CreateProps(channelId, _dependencies);

    protected override bool TryParseSessionChannelId(SessionId sessionId, out SlackChannelId channelId) =>
        TryParseSlackSessionId(sessionId, out channelId, out _);
}

public sealed record SlackGatewayDependencies(
    ISessionPipeline Pipeline,
    SessionIngressGate? IngressGate,
    ActorSystem ActorSystem,
    TimeProvider TimeProvider,
    SlackChannelOptions Options,
    SlackUserId? BotUserId,
    SlackChannelId? DefaultChannelId,
    IChannelRegistry ChannelRegistry,
    ISlackReplyClient ReplyClient,
    IContentScanner ContentScanner,
    IThreadHistoryFetcher ThreadHistoryFetcher,
    ToolAudienceProfiles AudienceProfiles,
    Netclaw.Configuration.ModelCapabilities ModelCapabilities,
    NetclawPaths Paths,
    HttpClient? HttpClient = null,
    Func<SlackChannelId, SlackGatewayDependencies, Props>? ConversationPropsFactory = null,
    Func<SessionId, SlackChannelId, SlackThreadTs, SlackGatewayDependencies, Props>? ThreadPropsFactory = null,
    IPromptInjectionDetector? PromptInjectionDetector = null);
