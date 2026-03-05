namespace Netclaw.Channels.Slack;

/// <summary>
/// Result of posting a new top-level message to a Slack channel.
/// </summary>
public readonly record struct SlackNewThread(SlackChannelId ChannelId, SlackThreadTs ThreadTs);

/// <summary>
/// Thin abstraction over the Slack API for proactive outbound operations:
/// opening DM channels and posting new threads.
/// </summary>
public interface ISlackOutboundClient
{
    /// <summary>Open a DM channel with a user. Returns the DM channel ID.</summary>
    Task<SlackChannelId> OpenDmChannelAsync(SlackUserId userId, CancellationToken ct = default);

    /// <summary>Post a new top-level message to a channel. Returns the thread root ts.</summary>
    Task<SlackNewThread> PostNewThreadAsync(SlackChannelId channelId, string text, CancellationToken ct = default);
}
