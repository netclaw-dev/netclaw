// -----------------------------------------------------------------------
// <copyright file="ISlackReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using SlackNet.Blocks;

namespace Netclaw.Channels.Slack;

public interface ISlackReplyClient
{
    Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default);

    Task<string> PostThreadReplyWithTsAsync(SlackPostMessage message, CancellationToken cancellationToken = default);

    Task UpdateThreadMessageAsync(
        SlackChannelId channelId,
        SlackEventTs messageTs,
        string text,
        IReadOnlyList<Block>? blocks = null,
        CancellationToken cancellationToken = default);

    Task SetThreadStatusAsync(
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        string status,
        CancellationToken cancellationToken = default);

    Task UploadFileToThreadAsync(
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        string filePath,
        string? filename = null,
        CancellationToken cancellationToken = default);
}
