// -----------------------------------------------------------------------
// <copyright file="ProactiveSendFormatting.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels;

/// <summary>
/// Canonical LLM-visible result strings (and the gateway ask timeout) shared by
/// the per-channel <see cref="IChannelOutboundClient"/> implementations. These
/// strings are returned to the model as <c>send_channel_message</c> tool
/// results, so they are part of the agent-visible contract — keep them
/// byte-stable and identical across channels (only the interpolated values may
/// differ). Channel-unique outcomes (e.g. Discord's thread-creation partial
/// failure) stay local to the owning client.
/// </summary>
public static class ProactiveSendFormatting
{
    /// <summary>
    /// How long a proactive send waits for the gateway to ack the session
    /// wiring before reporting the sent-but-pipeline-failed fallback.
    /// </summary>
    public static readonly TimeSpan ProactiveThreadAckTimeout = TimeSpan.FromSeconds(30);

    public static string GatewayNotConnected(string channel) =>
        $"Error: {channel} gateway is not connected.";

    public static string DirectMessagesDisabled(string channel) =>
        $"Error: Direct messages are disabled. Enable AllowDirectMessages in {channel} configuration to send DMs.";

    public static string UserNotAllowed(string userId) =>
        $"Error: User {userId} is not in the allowed users list.";

    public static string ChannelNotAllowed(string channelId) =>
        $"Error: Channel {channelId} is not in the allowed channels list.";

    public static string UnsupportedAddressKind(string channel, ChannelAddressKind addressKind) =>
        $"Error: {channel} outbound send does not support address kind '{addressKind}'.";

    public static string OpenDmChannelFailed(string message) =>
        $"Error: Failed to open DM channel: {message}";

    public static string PostFailed(string channel, string message) =>
        $"Error: Failed to post message to {channel}: {message}";

    /// <summary>Renders the send target as it appears in the success/fallback strings.</summary>
    public static string DescribeTarget(bool isDirectMessage, string targetId) =>
        isDirectMessage ? $"user {targetId}" : $"channel {targetId}";

    public static string Sent(string target, string thread) =>
        $"Message sent to {target}. Thread: {thread}";

    /// <summary>
    /// The message reached the platform but the gateway never acked the session
    /// wiring — replies to the thread may not route back to a live session.
    /// </summary>
    public static string SentButPipelineFailed(string target, string thread) =>
        $"Message sent to {target} but session pipeline failed to initialize. Thread: {thread}";
}
