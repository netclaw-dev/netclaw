// -----------------------------------------------------------------------
// <copyright file="TelegramRoutingPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Telegram;

internal static class TelegramRoutingPolicy
{
    public static TelegramRoutingDecision Evaluate(
        TelegramInboundMessage message,
        bool mentionOnly)
    {
        var hasContent = !string.IsNullOrWhiteSpace(message.Text)
                         || message.Files is { Count: > 0 };
        if (!hasContent)
            return TelegramRoutingDecision.Ignore(TelegramRoutingIgnoreReason.NoContent);

        if (message.IsDirectMessage
            || !mentionOnly
            || message.ContainsBotMention
            || message.IsReplyToBot)
            return TelegramRoutingDecision.StartOrContinue;

        return TelegramRoutingDecision.Ignore(TelegramRoutingIgnoreReason.GroupMentionRequired);
    }
}

internal enum TelegramRoutingDecisionKind
{
    Ignore,
    StartOrContinue
}

internal enum TelegramRoutingIgnoreReason
{
    NoContent,
    GroupMentionRequired
}

internal sealed record TelegramRoutingDecision(
    TelegramRoutingDecisionKind Kind,
    TelegramRoutingIgnoreReason? IgnoreReason)
{
    public static readonly TelegramRoutingDecision StartOrContinue =
        new(TelegramRoutingDecisionKind.StartOrContinue, null);

    public static TelegramRoutingDecision Ignore(TelegramRoutingIgnoreReason reason) =>
        new(TelegramRoutingDecisionKind.Ignore, reason);

    public static string TelemetryLabelFor(TelegramRoutingIgnoreReason reason) =>
        reason switch
        {
            TelegramRoutingIgnoreReason.NoContent => "routing_policy_ignore:NoContent",
            TelegramRoutingIgnoreReason.GroupMentionRequired => "routing_policy_ignore:GroupMentionRequired",
            _ => "routing_policy_ignore"
        };
}
