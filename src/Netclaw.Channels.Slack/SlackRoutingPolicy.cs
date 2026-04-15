namespace Netclaw.Channels.Slack;

public static class SlackRoutingPolicy
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
            return SlackRoutingDecision.StartOrContinue();

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
            return SlackRoutingDecision.StartOrContinue();
        }

        if (threadExists)
            return SlackRoutingDecision.ContinueOnly();

        // Thread reply where the actor was lost (e.g. daemon restart):
        // the message has a ThreadTs different from its EventTs, meaning
        // Slack knows this is a reply in an existing thread. Re-create
        // the thread actor and continue the persisted session.
        var isThreadReply = message.ThreadTs is { } threadTs
            && !string.Equals(threadTs.Value, message.EventTs.Value, StringComparison.Ordinal);
        if (isThreadReply)
            return SlackRoutingDecision.StartOrContinue();

        if (!mentionOnly)
            return SlackRoutingDecision.StartOrContinue();

        return containsBotMention
            ? SlackRoutingDecision.StartOrContinue()
            : SlackRoutingDecision.Ignore(SlackRoutingIgnoreReason.ChannelMentionRequired);
    }
}

public enum SlackRoutingDecisionKind
{
    Ignore,
    ContinueOnly,
    StartOrContinue
}

public enum SlackRoutingIgnoreReason
{
    NoContent,
    WrongKind,
    HiddenMessage,
    UnsupportedSubtype,
    DmNotAllowed,
    DmMentionRequired,
    ChannelMentionRequired
}

public readonly record struct SlackRoutingDecision(
    SlackRoutingDecisionKind Kind,
    SlackRoutingIgnoreReason? IgnoreReason)
{
    public static SlackRoutingDecision Ignore(SlackRoutingIgnoreReason reason) =>
        new(SlackRoutingDecisionKind.Ignore, reason);

    public static SlackRoutingDecision ContinueOnly() =>
        new(SlackRoutingDecisionKind.ContinueOnly, null);

    public static SlackRoutingDecision StartOrContinue() =>
        new(SlackRoutingDecisionKind.StartOrContinue, null);
}
