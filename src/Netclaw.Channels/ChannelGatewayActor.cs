// -----------------------------------------------------------------------
// <copyright file="ChannelGatewayActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels;

/// <summary>
/// Single owner of the channel session-id grammar: <c>{channelId}/{threadKey}</c>.
/// Every channel builds session ids through <see cref="Build"/> and parses them
/// through <see cref="TrySplit"/> — the per-channel <c>TryParse*SessionId</c>
/// helpers are typed wrappers over this class. Session ids are persisted (they
/// key session state and reminders), so changing this format is a breaking
/// migration, never a refactor.
/// </summary>
public static class SessionIdFormat
{
    /// <summary>Builds the deterministic <c>{channelId}/{threadKey}</c> session id.</summary>
    public static SessionId Build(string channelId, string threadKey) =>
        new($"{channelId}/{threadKey}");

    /// <summary>
    /// Splits a session id into its channel and thread parts. Rejects empty
    /// values, a missing separator, an empty channel part (separator first),
    /// and an empty thread part (separator last).
    /// </summary>
    public static bool TrySplit(SessionId sessionId, out string channelId, out string threadKey)
    {
        channelId = string.Empty;
        threadKey = string.Empty;

        var value = sessionId.Value;
        if (string.IsNullOrEmpty(value))
            return false;

        var slashIdx = value.IndexOf('/', StringComparison.Ordinal);
        if (slashIdx <= 0 || slashIdx == value.Length - 1)
            return false;

        channelId = value[..slashIdx];
        threadKey = value[(slashIdx + 1)..];
        return true;
    }
}

/// <summary>
/// Shared per-channel gateway routing plumbing (SPEC-015 §1.3): bounded
/// duplicate event-id tracking, get-or-create of per-conversation child
/// actors keyed by escaped channel id, and the channel-agnostic
/// <see cref="DeliverTrustedSessionTurn"/> receive (parse the session id,
/// nack unparseable ids, route to the owning conversation). Subclasses
/// register their channel-specific inbound receives in their own
/// constructors — the per-channel telemetry labels and log lines stay with
/// the channel — and supply the id projections via the abstract hooks.
/// </summary>
/// <typeparam name="TChannelId">The channel's conversation routing key (e.g. <c>SlackChannelId</c>).</typeparam>
public abstract class ChannelGatewayActor<TChannelId> : ReceiveActor
    where TChannelId : struct
{
    private const int MaxProcessedEventIds = 4096;

    private readonly Dictionary<string, byte> _processedEventIds = [];
    private readonly Queue<string> _processedEventOrder = new();
    private readonly string _channelDisplayName;

    protected ChannelGatewayActor(ChannelType channelType)
    {
        _channelDisplayName = channelType.ToString();
        Log = Context.GetLogger().WithContext("Adapter", channelType.ToWireValue());

        // No ACL call — audience was validated at reminder mint time by
        // the reminder-audience-authorization capability.
        Receive<DeliverTrustedSessionTurn>(message =>
        {
            if (!TryParseSessionChannelId(message.SessionId, out var channelId))
            {
                Log.Warning(
                    "Dropping DeliverTrustedSessionTurn with unparseable " + _channelDisplayName + " SessionId {SessionId}",
                    message.SessionId.Value);
                Sender.Tell(CommandNack.For(message.SessionId, "Invalid " + _channelDisplayName + " SessionId format"));
                return;
            }

            var conversation = GetOrCreateConversation(channelId);

            Log.Debug(
                "Routing DeliverTrustedSessionTurn session={Session} channel={Channel}",
                message.SessionId.Value, ChannelIdValue(channelId));
            conversation.Forward(message);
        });
    }

    /// <summary>Gateway log adapter tagged with the channel's <c>Adapter</c> context.</summary>
    protected ILoggingAdapter Log { get; }

    /// <summary>
    /// Returns the conversation child for <paramref name="channelId"/>, creating
    /// it on first use. Children are named by the escaped channel id value —
    /// actor paths are operator-visible, so the naming scheme is part of the
    /// channel's observable surface.
    /// </summary>
    protected IActorRef GetOrCreateConversation(TChannelId channelId)
    {
        var actorName = Uri.EscapeDataString(ChannelIdValue(channelId));
        var existing = Context.Child(actorName);
        if (!existing.IsNobody())
            return existing;

        return Context.ActorOf(CreateConversationProps(channelId), actorName);
    }

    /// <summary>
    /// Marks an event id as processed, returning <c>false</c> for duplicates.
    /// The tracking set is bounded at <see cref="MaxProcessedEventIds"/> entries
    /// (oldest-first eviction). Missing/blank ids are delegated to
    /// <see cref="OnMissingEventId"/> because channels disagree on whether an
    /// id-less event is processable.
    /// </summary>
    protected bool TryMarkEventProcessed(string? eventIdValue)
    {
        if (string.IsNullOrWhiteSpace(eventIdValue))
            return OnMissingEventId();

        if (!_processedEventIds.TryAdd(eventIdValue, 0))
            return false;

        _processedEventOrder.Enqueue(eventIdValue);

        while (_processedEventIds.Count > MaxProcessedEventIds
               && _processedEventOrder.TryDequeue(out var oldestEventId))
            _processedEventIds.Remove(oldestEventId);

        return true;
    }

    /// <summary>
    /// Policy for events that arrive without an event id. The default rejects
    /// them (returns <c>false</c>) because they cannot be deduplicated; Slack
    /// overrides to accept them because some legitimate SlackNet event shapes
    /// omit the envelope id.
    /// </summary>
    protected virtual bool OnMissingEventId()
    {
        Log.Warning("Rejecting " + _channelDisplayName + " event with empty EventId — cannot deduplicate");
        return false;
    }

    /// <summary>Projects the raw string value of the routing key (used for child naming and logs).</summary>
    protected abstract string ChannelIdValue(TChannelId channelId);

    /// <summary>Builds the conversation actor Props for a routing key (honoring any test props factory).</summary>
    protected abstract Props CreateConversationProps(TChannelId channelId);

    /// <summary>Extracts the channel part of a <c>{channelId}/{threadId}</c> session id.</summary>
    protected abstract bool TryParseSessionChannelId(SessionId sessionId, out TChannelId channelId);
}
