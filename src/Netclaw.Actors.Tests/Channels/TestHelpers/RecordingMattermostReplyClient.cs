// -----------------------------------------------------------------------
// <copyright file="RecordingMattermostReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Mattermost;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class RecordingMattermostReplyClient : IMattermostReplyClient
{
    public List<MattermostPostMessage> Posts { get; } = [];
    public List<(MattermostPostId PostId, string Text, IReadOnlyList<MattermostAttachment>? Attachments)> Updates { get; } = [];
    public Exception? ThrowOnPost { get; set; }

    private int _messageCounter;

    public Task<MattermostPostResult> PostReplyAsync(MattermostPostMessage message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPost is { } ex)
            throw ex;

        Posts.Add(message);
        var postId = new MattermostPostId($"post-{Interlocked.Increment(ref _messageCounter)}");
        return Task.FromResult(new MattermostPostResult(PostId: postId));
    }

    public Task UpdatePostAsync(MattermostPostId postId, string text, IReadOnlyList<MattermostAttachment>? attachments, CancellationToken cancellationToken = default)
    {
        Updates.Add((postId, text, attachments));
        return Task.CompletedTask;
    }
}
