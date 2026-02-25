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
