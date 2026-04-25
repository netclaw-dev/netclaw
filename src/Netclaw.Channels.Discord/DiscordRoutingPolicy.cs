namespace Netclaw.Channels.Discord;

internal static class DiscordRoutingPolicy
{
    public static DiscordRoutingDecision Evaluate(
        DiscordGatewayMessage message,
        bool mentionOnly,
        bool allowDirectMessages,
        bool mentionRequiredInDm,
        bool threadExists,
        bool containsBotMention)
    {
        var hasAttachments = message.Attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(message.Text) && !hasAttachments)
            return DiscordRoutingDecision.Ignore(DiscordRoutingIgnoreReason.NoContent);

        if (message.IsDirectMessage)
        {
            if (!allowDirectMessages)
                return DiscordRoutingDecision.Ignore(DiscordRoutingIgnoreReason.DmNotAllowed);
            if (mentionRequiredInDm && !containsBotMention)
                return DiscordRoutingDecision.Ignore(DiscordRoutingIgnoreReason.DmMentionRequired);
            return DiscordRoutingDecision.StartOrContinue;
        }

        if (threadExists)
            return DiscordRoutingDecision.ContinueOnly;

        if (!mentionOnly)
            return DiscordRoutingDecision.StartOrContinue;

        return containsBotMention
            ? DiscordRoutingDecision.StartOrContinue
            : DiscordRoutingDecision.Ignore(DiscordRoutingIgnoreReason.ChannelMentionRequired);
    }
}

internal enum DiscordRoutingDecisionKind
{
    Ignore,
    ContinueOnly,
    StartOrContinue
}

internal enum DiscordRoutingIgnoreReason
{
    NoContent,
    DmNotAllowed,
    DmMentionRequired,
    ChannelMentionRequired
}

internal sealed record DiscordRoutingDecision(
    DiscordRoutingDecisionKind Kind,
    DiscordRoutingIgnoreReason? IgnoreReason)
{
    public static readonly DiscordRoutingDecision StartOrContinue =
        new(DiscordRoutingDecisionKind.StartOrContinue, null);

    public static readonly DiscordRoutingDecision ContinueOnly =
        new(DiscordRoutingDecisionKind.ContinueOnly, null);

    public static DiscordRoutingDecision Ignore(DiscordRoutingIgnoreReason reason) =>
        new(DiscordRoutingDecisionKind.Ignore, reason);

    /// <summary>
    /// Pre-computed telemetry labels keyed by <see cref="DiscordRoutingIgnoreReason"/>
    /// so the drop path does not allocate a new string per dropped event.
    /// </summary>
    public static string TelemetryLabelFor(DiscordRoutingIgnoreReason reason) =>
        reason switch
        {
            DiscordRoutingIgnoreReason.NoContent => "routing_policy_ignore:NoContent",
            DiscordRoutingIgnoreReason.DmNotAllowed => "routing_policy_ignore:DmNotAllowed",
            DiscordRoutingIgnoreReason.DmMentionRequired => "routing_policy_ignore:DmMentionRequired",
            DiscordRoutingIgnoreReason.ChannelMentionRequired => "routing_policy_ignore:ChannelMentionRequired",
            _ => "routing_policy_ignore",
        };
}
