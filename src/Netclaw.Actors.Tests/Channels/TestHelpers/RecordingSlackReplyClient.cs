// -----------------------------------------------------------------------
// <copyright file="RecordingSlackReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Slack;
using SlackNet.Blocks;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

public sealed class RecordingSlackReplyClient : ISlackReplyClient
{
    private readonly object _lock = new();
    private readonly List<SlackPostMessage> _posts = [];
    private readonly List<UpdateRecord> _updates = [];

    public IReadOnlyList<SlackPostMessage> Posts
    {
        get { lock (_lock) return _posts.ToList(); }
    }

    public IReadOnlyList<UpdateRecord> Updates
    {
        get { lock (_lock) return _updates.ToList(); }
    }

    public Exception? ThrowOnPost { get; set; }

    public void Clear()
    {
        lock (_lock)
        {
            _posts.Clear();
            _updates.Clear();
        }
    }

    public Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPost is { } ex)
            throw ex;
        lock (_lock) _posts.Add(message);
        return Task.CompletedTask;
    }

    public Task<string> PostThreadReplyWithTsAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPost is { } ex)
            throw ex;
        lock (_lock) _posts.Add(message);
        return Task.FromResult("fake.ts");
    }

    public Task UpdateThreadMessageAsync(
        SlackChannelId channelId,
        SlackEventTs messageTs,
        string text,
        IReadOnlyList<Block>? blocks = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock) _updates.Add(new UpdateRecord(channelId, messageTs, text, blocks));
        return Task.CompletedTask;
    }

    public Task UploadFileToThreadAsync(
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        string filePath,
        string? filename = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public sealed record UpdateRecord(
        SlackChannelId ChannelId,
        SlackEventTs MessageTs,
        string Text,
        IReadOnlyList<Block>? Blocks);
}
