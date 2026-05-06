// -----------------------------------------------------------------------
// <copyright file="IMattermostOutboundClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Mattermost;

public readonly record struct MattermostNewThread(MattermostChannelId ChannelId, MattermostRootPostId RootPostId);

/// <summary>
/// Thin abstraction over the Mattermost API for proactive outbound operations:
/// opening DM channels and posting new threads.
/// </summary>
public interface IMattermostOutboundClient
{
    /// <summary>Open or retrieve a DM channel with a user. Returns the DM channel ID.</summary>
    Task<MattermostChannelId> OpenDmChannelAsync(MattermostUserId userId, CancellationToken ct = default);

    /// <summary>Post a new top-level message to a channel. Returns the thread root identifiers.</summary>
    Task<MattermostNewThread> PostNewThreadAsync(MattermostChannelId channelId, string text, CancellationToken ct = default);
}
