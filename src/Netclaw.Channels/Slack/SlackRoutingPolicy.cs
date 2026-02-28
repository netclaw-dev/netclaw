namespace Netclaw.Channels.Slack;

public static class SlackRoutingPolicy
{
    public static SlackRoutingDecision Evaluate(
        SlackInboundMessage message,
        bool mentionOnly,
        bool allowDirectMessages,
        bool threadExists,
        bool containsBotMention)
    {
        if (string.IsNullOrWhiteSpace(message.Text))
            return SlackRoutingDecision.Ignore;

        if (message.Kind is SlackInboundKind.AppMention)
            return SlackRoutingDecision.StartOrContinue;

        if (message.Kind is not SlackInboundKind.Message)
            return SlackRoutingDecision.Ignore;

        if (message.Hidden || !string.IsNullOrWhiteSpace(message.Subtype))
            return SlackRoutingDecision.Ignore;

        if (message.IsDirectMessage)
            return allowDirectMessages ? SlackRoutingDecision.StartOrContinue : SlackRoutingDecision.Ignore;

        if (threadExists)
            return SlackRoutingDecision.ContinueOnly;

        // Thread reply where the actor was lost (e.g. daemon restart):
        // the message has a ThreadTs different from its EventTs, meaning
        // Slack knows this is a reply in an existing thread. Re-create
        // the thread actor and continue the persisted session.
        var isThreadReply = !string.IsNullOrWhiteSpace(message.ThreadTs)
            && !string.Equals(message.ThreadTs, message.EventTs, StringComparison.Ordinal);
        if (isThreadReply)
            return SlackRoutingDecision.StartOrContinue;

        if (!mentionOnly)
            return SlackRoutingDecision.StartOrContinue;

        return containsBotMention ? SlackRoutingDecision.StartOrContinue : SlackRoutingDecision.Ignore;
    }
}

public enum SlackRoutingDecision
{
    Ignore,
    ContinueOnly,
    StartOrContinue
}
