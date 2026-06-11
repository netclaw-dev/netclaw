// -----------------------------------------------------------------------
// <copyright file="RecordingMattermostReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Mattermost;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class RecordingMattermostReplyClient : IMattermostReplyClient
{
    private readonly object _lock = new();
    private readonly List<MattermostPostMessage> _posts = [];
    private readonly List<(MattermostPostId PostId, string Text, IReadOnlyList<MattermostAttachment>? Attachments)> _updates = [];
    private readonly List<(MattermostChannelId ChannelId, string FilePath, string? FileName)> _uploads = [];

    public IReadOnlyList<MattermostPostMessage> Posts
    {
        get { lock (_lock) return _posts.ToList(); }
    }

    public IReadOnlyList<(MattermostPostId PostId, string Text, IReadOnlyList<MattermostAttachment>? Attachments)> Updates
    {
        get { lock (_lock) return _updates.ToList(); }
    }

    public IReadOnlyList<(MattermostChannelId ChannelId, string FilePath, string? FileName)> Uploads
    {
        get { lock (_lock) return _uploads.ToList(); }
    }

    public Exception? ThrowOnPost { get; set; }

    public Exception? ThrowOnUpload { get; set; }

    // Throws on the next post only, then auto-clears. Lets a test fail a content
    // post while letting a follow-up (e.g. fallback) succeed and be recorded.
    public Exception? ThrowOnceOnPost { get; set; }

    private int _messageCounter;

    public void Clear()
    {
        lock (_lock)
        {
            _posts.Clear();
            _updates.Clear();
            _uploads.Clear();
        }
    }

    public Task<MattermostPostResult> PostReplyAsync(MattermostPostMessage message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPost is { } ex)
            throw ex;

        if (ThrowOnceOnPost is { } onceEx)
        {
            ThrowOnceOnPost = null;
            throw onceEx;
        }

        lock (_lock) _posts.Add(message);
        var postId = new MattermostPostId($"post-{Interlocked.Increment(ref _messageCounter)}");
        return Task.FromResult(new MattermostPostResult(PostId: postId));
    }

    public Task UpdatePostAsync(MattermostPostId postId, string text, IReadOnlyList<MattermostAttachment>? attachments, CancellationToken cancellationToken = default)
    {
        lock (_lock) _updates.Add((postId, text, attachments));
        return Task.CompletedTask;
    }

    public Task<string> UploadFileAsync(
        MattermostChannelId channelId,
        string filePath,
        string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnUpload is { } ex)
            throw ex;

        var fileId = $"file-{Interlocked.Increment(ref _messageCounter)}";
        lock (_lock) _uploads.Add((channelId, filePath, fileName));
        return Task.FromResult(fileId);
    }
}
