namespace Netclaw.Channels.Slack;

internal static class SlackRoutingPolicy
{
    public static SlackRoutingDecision Evaluate(
        SlackInboundMessage message,
        bool mentionOnly,
        bool allowDirectMessages,
        bool mentionRequiredInDm,
        bool threadExists,
        bool containsBotMention)
    {
        var hasContent = !string.IsNullOrWhiteSpace(message.Text)
                        || message.Files is { Count: > 0 };
        if (!hasContent)
            return SlackRoutingDecision.Ignore(SlackRoutingIgnoreReason.NoContent);

        if (message.Kind is SlackInboundKind.AppMention)
            return SlackRoutingDecision.StartOrContinue;

        // Defensive: SlackChannel only dispatches Message and AppMention to this
        // policy, so any other Kind is a routing bug upstream. Drop loudly.
        if (message.Kind is not SlackInboundKind.Message)
            return SlackRoutingDecision.Ignore(SlackRoutingIgnoreReason.WrongKind);

        if (message.Hidden)
            return SlackRoutingDecision.Ignore(SlackRoutingIgnoreReason.HiddenMessage);

        // Allow file_share subtype through when files are attached — Slack sends
        // user-uploaded files as messages with subtype "file_share". All other
        // subtypes (bot_message, message_changed, channel_join, etc.) are dropped.
        if (!string.IsNullOrWhiteSpace(message.Subtype))
        {
            var isFileShare = string.Equals(message.Subtype, "file_share", StringComparison.Ordinal)
                && message.Files is { Count: > 0 };
            if (!isFileShare)
                return SlackRoutingDecision.Ignore(SlackRoutingIgnoreReason.UnsupportedSubtype);
        }

        if (message.IsDirectMessage)
        {
            if (!allowDirectMessages)
                return SlackRoutingDecision.Ignore(SlackRoutingIgnoreReason.DmNotAllowed);
            if (mentionRequiredInDm && !containsBotMention)
                return SlackRoutingDecision.Ignore(SlackRoutingIgnoreReason.DmMentionRequired);
            return SlackRoutingDecision.StartOrContinue;
        }

        if (threadExists)
            return SlackRoutingDecision.ContinueOnly;

        // Thread reply where the actor was lost (e.g. daemon restart):
        // the message has a ThreadTs different from its EventTs, meaning
        // Slack knows this is a reply in an existing thread. Re-create
        // the thread actor and continue the persisted session.
        var isThreadReply = message.ThreadTs is { } threadTs
            && !string.Equals(threadTs.Value, message.EventTs.Value, StringComparison.Ordinal);
        if (isThreadReply)
            return SlackRoutingDecision.StartOrContinue;

        if (!mentionOnly)
            return SlackRoutingDecision.StartOrContinue;

        return containsBotMention
            ? SlackRoutingDecision.StartOrContinue
            : SlackRoutingDecision.Ignore(SlackRoutingIgnoreReason.ChannelMentionRequired);
    }
}

internal enum SlackRoutingDecisionKind
{
    Ignore,
    ContinueOnly,
    StartOrContinue
}

internal enum SlackRoutingIgnoreReason
{
    NoContent,
    WrongKind,
    HiddenMessage,
    UnsupportedSubtype,
    DmNotAllowed,
    DmMentionRequired,
    ChannelMentionRequired
}

internal sealed record SlackRoutingDecision(
    SlackRoutingDecisionKind Kind,
    SlackRoutingIgnoreReason? IgnoreReason)
{
    public static readonly SlackRoutingDecision StartOrContinue =
        new(SlackRoutingDecisionKind.StartOrContinue, null);

    public static readonly SlackRoutingDecision ContinueOnly =
        new(SlackRoutingDecisionKind.ContinueOnly, null);

    public static SlackRoutingDecision Ignore(SlackRoutingIgnoreReason reason) =>
        new(SlackRoutingDecisionKind.Ignore, reason);

    /// <summary>
    /// Pre-computed telemetry labels keyed by <see cref="SlackRoutingIgnoreReason"/>
    /// so the <see cref="SlackConversationActor"/> drop path does not allocate a
    /// new string per dropped event. Matches the shape produced by the other
    /// <c>ChannelTelemetry.For(Slack).RecordEventFiltered</c> callers (bucket-prefixed
    /// reason labels) so existing dashboards continue to work.
    /// </summary>
    public static string TelemetryLabelFor(SlackRoutingIgnoreReason reason) =>
        reason switch
        {
            SlackRoutingIgnoreReason.NoContent => "routing_policy_ignore:NoContent",
            SlackRoutingIgnoreReason.WrongKind => "routing_policy_ignore:WrongKind",
            SlackRoutingIgnoreReason.HiddenMessage => "routing_policy_ignore:HiddenMessage",
            SlackRoutingIgnoreReason.UnsupportedSubtype => "routing_policy_ignore:UnsupportedSubtype",
            SlackRoutingIgnoreReason.DmNotAllowed => "routing_policy_ignore:DmNotAllowed",
            SlackRoutingIgnoreReason.DmMentionRequired => "routing_policy_ignore:DmMentionRequired",
            SlackRoutingIgnoreReason.ChannelMentionRequired => "routing_policy_ignore:ChannelMentionRequired",
            _ => "routing_policy_ignore",
        };
}
