// -----------------------------------------------------------------------
// <copyright file="NoopReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Slack;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class NoopReplyClient : ISlackReplyClient
{
    public Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<string> PostThreadReplyWithTsAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
        => Task.FromResult("0");

    public Task UpdateThreadMessageAsync(
        SlackChannelId channelId,
        SlackEventTs messageTs,
        string text,
        IReadOnlyList<SlackNet.Blocks.Block>? blocks = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SetThreadStatusAsync(
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        string status,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task UploadFileToThreadAsync(SlackChannelId channelId, SlackThreadTs threadTs, string filePath, string? filename = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
