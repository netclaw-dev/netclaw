// -----------------------------------------------------------------------
// <copyright file="IDiscordOutboundClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Discord;

/// <summary>
/// Result of a proactive Discord post that created a new conversation thread.
/// The posted message is the thread root; <see cref="ThreadOrMessageId"/> and
/// <see cref="ReplyChannelId"/> are the created thread's id.
/// </summary>
public readonly record struct DiscordNewThread(
    DiscordChannelId ChannelId,
    DiscordReplyChannelId ReplyChannelId,
    DiscordThreadOrMessageId ThreadOrMessageId);

/// <summary>
/// Thin abstraction over the Discord API for proactive outbound operations:
/// posting a new message and creating a conversation thread off it. Distinct
/// from <see cref="IDiscordReplyClient"/>, which serves the inbound reply path
/// and creates threads only off pre-existing anchor messages.
/// </summary>
public interface IDiscordOutboundClient
{
    /// <summary>
    /// Posts a new message to a text channel and creates a public thread off
    /// that message, so the posted message becomes the thread root.
    /// </summary>
    Task<DiscordNewThread> PostNewThreadAsync(
        DiscordChannelId channelId,
        string text,
        string threadName,
        CancellationToken ct = default);
}
