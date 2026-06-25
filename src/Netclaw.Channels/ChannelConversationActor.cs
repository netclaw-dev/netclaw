// -----------------------------------------------------------------------
// <copyright file="ChannelConversationActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels;

/// <summary>How a channel routing policy disposed of an inbound message.</summary>
public enum ChannelRoutingVerdictKind
{
    /// <summary>Drop the message.</summary>
    Ignore,

    /// <summary>Deliver only to an already-running session binding.</summary>
    ContinueOnly,

    /// <summary>Start a new session binding or continue an existing one.</summary>
    StartOrContinue
}

/// <summary>
/// Channel-neutral projection of a routing policy decision. The per-channel
/// policies keep their own decision enums; conversation actors map them into
/// this shape so the shared pipeline can log and meter ignores uniformly.
/// <see cref="IgnoreReason"/> is the channel enum's member name (rendered into
/// the <c>routing_policy_ignore</c> log line) and <see cref="IgnoreTelemetryLabel"/>
/// is the channel's <c>TelemetryLabelFor</c> output — they are distinct axes
/// and must both come from the channel policy.
/// </summary>
public sealed record ChannelRoutingVerdict(
    ChannelRoutingVerdictKind Kind,
    string? IgnoreReason,
    string? IgnoreTelemetryLabel)
{
    public static readonly ChannelRoutingVerdict StartOrContinue =
        new(ChannelRoutingVerdictKind.StartOrContinue, null, null);

    public static readonly ChannelRoutingVerdict ContinueOnly =
        new(ChannelRoutingVerdictKind.ContinueOnly, null, null);

    public static ChannelRoutingVerdict Ignore(string reason, string telemetryLabel) =>
        new(ChannelRoutingVerdictKind.Ignore, reason, telemetryLabel);
}

/// <summary>
/// Shared per-conversation security boundary for remote chat channels
/// (SPEC-015 §1.3). Owns the inbound message pipeline — ACL evaluation, bot
/// self-loop filtering, ingress gating (restart drain), routing policy,
/// text normalization/truncation, empty-content filtering, and session
/// binding get-or-create — in a fixed order so every channel applies the
/// same security gates the same way. Also owns idle passivation (2 hours),
/// stop-on-failure supervision of session bindings, and <c>Terminated</c>
/// bookkeeping.
///
/// Subclasses register their channel-specific receives (interactions,
/// proactive threads, trusted session turns) in their own constructors and
/// implement the abstract projections over <typeparamref name="TMessage"/>.
///
/// Slack's conversation actor intentionally does NOT use this base: its
/// pipeline order differs observably (routing policy before the ingress
/// gate, thread creation before the empty-text filter) and it has no
/// truncation, watch, or supervision override — see SPEC-015 §1.3 risk note.
/// </summary>
/// <typeparam name="TMessage">The channel's normalized inbound gateway message.</typeparam>
public abstract class ChannelConversationActor<TMessage> : ReceiveActor
    where TMessage : class
{
    private const int MaxInboundTextLength = 4000;
    private static readonly TimeSpan PassivationTimeout = TimeSpan.FromHours(2);

    private readonly string _channelDisplayName;

    protected ChannelConversationActor(
        ChannelType channelType,
        string channelIdValue,
        SessionIngressGate? ingressGate)
    {
        _channelDisplayName = channelType.ToString();
        ChannelIdValue = channelIdValue;
        IngressGate = ingressGate;
        Telemetry = ChannelTelemetry.For(channelType);
        Log = Context.GetLogger()
            .WithContext("Adapter", channelType.ToWireValue())
            .WithContext(_channelDisplayName + "ChannelId", channelIdValue);

        Context.SetReceiveTimeout(PassivationTimeout);

        Receive<ReceiveTimeout>(_ =>
        {
            Log.Info("Conversation idle for 2 hours, passivating");
            Context.Stop(Self);
        });

        Receive<TMessage>(HandleGatewayMessage);
        Receive<Terminated>(msg => Log.Debug("Session binding stopped: {0}", msg.ActorRef.Path.Name));
    }

    /// <summary>Conversation log adapter tagged with the channel's <c>Adapter</c> and channel-id contexts.</summary>
    protected ILoggingAdapter Log { get; }

    /// <summary>Metrics sink for this channel type.</summary>
    protected ChannelMetrics Telemetry { get; }

    /// <summary>The raw channel id string this conversation actor is bound to.</summary>
    protected string ChannelIdValue { get; }

    /// <summary>The restart-drain ingress gate, when the channel wires one.</summary>
    protected SessionIngressGate? IngressGate { get; }

    protected override SupervisorStrategy SupervisorStrategy()
        => new OneForOneStrategy(ex =>
        {
            Log.Error(ex, "Session binding child failed; stopping to allow re-creation");
            return Directive.Stop;
        });

    private void HandleGatewayMessage(TMessage message)
    {
        var eventId = EventIdOf(message);

        // --- ACL gate ---
        var aclDecision = EvaluateAcl(message);
        if (!aclDecision.IsAllowed)
        {
            var reason = aclDecision.DenyReason ?? "acl_denied";
            Log.Info("event_dropped event={0} reason={1}", eventId, reason);
            Telemetry.RecordEventDropped(reason);
            return;
        }

        // --- Bot self-loop filter ---
        if (IsBotMessage(message))
        {
            Log.Info("event_filtered event={0} reason=bot_message", eventId);
            Telemetry.RecordEventFiltered("bot_message");
            return;
        }

        // --- Ingress gate ---
        if (IngressGate?.ClosedReason is { } closedReason)
        {
            Log.Info("event_filtered event={0} reason=restart_drain_active", eventId);
            Telemetry.RecordEventFiltered("restart_drain_active");
            // Safe fire-and-forget: implementations wrap everything in try/catch,
            // and no synchronous code precedes the first await, so exceptions cannot escape.
            _ = PostIngressClosedReplyAsync(message, closedReason);
            return;
        }

        // --- Routing policy ---
        var threadKey = ThreadKeyOf(message);
        var actorName = BindingActorName(ChannelIdValue, threadKey);
        var existingBinding = Context.Child(actorName);
        var threadExists = !existingBinding.IsNobody();

        var verdict = EvaluateRouting(message, threadExists);

        if (verdict.Kind is ChannelRoutingVerdictKind.Ignore)
        {
            Log.Info(
                "event_filtered event={0} reason=routing_policy_ignore ignoreReason={1}",
                eventId,
                verdict.IgnoreReason);
            Telemetry.RecordEventFiltered(verdict.IgnoreTelemetryLabel!);
            return;
        }

        if (verdict.Kind is ChannelRoutingVerdictKind.ContinueOnly && !threadExists)
        {
            Log.Info("event_dropped event={0} reason=thread_not_initialized", eventId);
            Telemetry.RecordEventDropped("thread_not_initialized");
            return;
        }

        // --- Empty text filter ---
        var normalizedText = NormalizeInboundText(TextOf(message));
        if (normalizedText.Length > MaxInboundTextLength)
        {
            Log.Warning("inbound_text_truncated original={OriginalLength} clamped={MaxLength}",
                normalizedText.Length, MaxInboundTextLength);
            normalizedText = normalizedText[..MaxInboundTextLength];
        }

        if (string.IsNullOrWhiteSpace(normalizedText) && !HasAttachments(message))
        {
            Log.Info("event_filtered event={0} reason=empty_text", eventId);
            Telemetry.RecordEventFiltered("empty_text");
            return;
        }

        // --- Build session and forward ---
        var sessionId = BuildSessionId(threadKey);
        var sessionBinding = threadExists
            ? existingBinding
            : GetOrCreateSessionBinding(ChannelIdValue, threadKey, () => CreateSessionBindingProps(sessionId, message));

        var turnId = string.IsNullOrWhiteSpace(eventId)
            ? IdGen.ShortId()
            : eventId;

        var log = Log
            .WithContext(ThreadLogContextKey, threadKey)
            .WithContext("SessionId", sessionId.Value)
            .WithContext("TurnId", turnId)
            .WithContext(EventLogContextKey, eventId);

        log.Info("turn_routed event={EventId} textChars={TextLength}",
            eventId,
            normalizedText.Length);

        Telemetry.RecordEventRouted("message");
        sessionBinding.Forward(CreateThreadInbound(sessionId, message, aclDecision, normalizedText));
    }

    /// <summary>Builds the deterministic session id (<c>{channelId}/{threadKey}</c>) for a thread key.</summary>
    protected SessionId BuildSessionId(string threadKey) =>
        SessionIdFormat.Build(ChannelIdValue, threadKey);

    /// <summary>
    /// Returns the session binding child for <paramref name="threadKey"/>,
    /// creating (and death-watching) it on first use. The channel value is
    /// explicit because proactive-thread paths name the binding by the channel
    /// id carried on the command, not the conversation's own id.
    /// </summary>
    protected IActorRef GetOrCreateSessionBinding(
        string channelValue,
        string threadKey,
        Func<Props> propsFactory)
    {
        var actorName = BindingActorName(channelValue, threadKey);
        var existing = Context.Child(actorName);
        if (!existing.IsNobody())
            return existing;

        var child = Context.ActorOf(propsFactory(), actorName);
        Context.Watch(child);
        return child;
    }

    /// <summary>
    /// Warns and nacks a <see cref="DeliverTrustedSessionTurn"/> whose session id
    /// does not parse as <c>{channelId}/{threadKey}</c>.
    /// </summary>
    protected void NackUnparseableSessionId(DeliverTrustedSessionTurn message)
    {
        Log.Warning(
            "Dropping DeliverTrustedSessionTurn with unparseable " + _channelDisplayName + " SessionId {SessionId}",
            message.SessionId.Value);
        Sender.Tell(CommandNack.For(message.SessionId, "Invalid " + _channelDisplayName + " SessionId format"));
    }

    /// <summary>
    /// Warns and nacks a <see cref="DeliverTrustedSessionTurn"/> whose parsed
    /// channel id does not match this conversation actor.
    /// </summary>
    protected void NackConversationMismatch(DeliverTrustedSessionTurn message)
    {
        Log.Warning(
            "Dropping DeliverTrustedSessionTurn for wrong conversation session={Session} expected_channel={Channel}",
            message.SessionId.Value, ChannelIdValue);
        Sender.Tell(CommandNack.For(message.SessionId, "Conversation mismatch"));
    }

    private static string BindingActorName(string channelValue, string threadKey) =>
        Uri.EscapeDataString($"{channelValue}:{threadKey}");

    /// <summary>Log context key for the thread identifier (e.g. <c>DiscordThreadOrMessageId</c>).</summary>
    protected abstract string ThreadLogContextKey { get; }

    /// <summary>Log context key for the event identifier (e.g. <c>MattermostEventId</c>).</summary>
    protected abstract string EventLogContextKey { get; }

    /// <summary>Evaluates the channel ACL for an inbound message.</summary>
    protected abstract ChannelAclDecision EvaluateAcl(TMessage message);

    /// <summary>True when the message originates from the bot itself (self-loop guard).</summary>
    protected abstract bool IsBotMessage(TMessage message);

    /// <summary>Projects the raw event id string (may be empty for synthetic events).</summary>
    protected abstract string EventIdOf(TMessage message);

    /// <summary>
    /// Projects the thread key that addresses the session binding — the root
    /// post/thread identifier, falling back to the message's own id when the
    /// message starts a new thread.
    /// </summary>
    protected abstract string ThreadKeyOf(TMessage message);

    /// <summary>Projects the raw message text before normalization.</summary>
    protected abstract string TextOf(TMessage message);

    /// <summary>True when the message carries attachments (empty-text messages with attachments still route).</summary>
    protected abstract bool HasAttachments(TMessage message);

    /// <summary>Strips the channel's bot mention tag and trims whitespace.</summary>
    protected abstract string NormalizeInboundText(string text);

    /// <summary>Maps the channel routing policy decision into the shared verdict shape.</summary>
    protected abstract ChannelRoutingVerdict EvaluateRouting(TMessage message, bool threadExists);

    /// <summary>Builds the session binding Props for an inbound message (honoring any test props factory).</summary>
    protected abstract Props CreateSessionBindingProps(SessionId sessionId, TMessage message);

    /// <summary>Builds the normalized thread-inbound record forwarded to the session binding.</summary>
    protected abstract object CreateThreadInbound(
        SessionId sessionId,
        TMessage message,
        ChannelAclDecision aclDecision,
        string normalizedText);

    /// <summary>
    /// Posts the restart-drain notice back to the message's thread.
    /// Implementations MUST wrap the entire body in try/catch with no
    /// synchronous code before the first await — the pipeline invokes this
    /// fire-and-forget.
    /// </summary>
    protected abstract Task PostIngressClosedReplyAsync(TMessage message, string closedReason);
}
