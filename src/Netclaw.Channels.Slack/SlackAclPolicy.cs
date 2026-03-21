namespace Netclaw.Channels.Slack;

/// <summary>
/// Shared ACL checks for Slack channel and user authorization.
/// Used by both <see cref="SlackConversationActor"/> (inbound) and
/// <see cref="Tools.SendSlackMessageTool"/> (outbound/proactive).
/// </summary>
public static class SlackAclPolicy
{
    /// <summary>
    /// Returns true if <paramref name="channelId"/> is the default channel
    /// or appears in <see cref="SlackChannelOptions.AllowedChannelIds"/>.
    /// </summary>
    public static bool IsAllowedChannel(
        SlackChannelId channelId,
        SlackChannelOptions options,
        SlackChannelId? defaultChannelId)
    {
        if (defaultChannelId is not null && channelId == defaultChannelId.Value)
            return true;

        return options.AllowedChannelIds.Contains(channelId.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns true if the user is permitted. An empty allow-list means all users are allowed.
    /// </summary>
    public static bool IsAllowedUser(SlackUserId userId, SlackChannelOptions options)
    {
        if (options.AllowedUserIds.Length == 0)
            return true;

        return options.AllowedUserIds.Contains(userId.Value, StringComparer.Ordinal);
    }
}
