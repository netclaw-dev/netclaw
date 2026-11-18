// -----------------------------------------------------------------------
// <copyright file="RecordingDiscordReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Discord;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class RecordingDiscordReplyClient : IDiscordReplyClient
{
    private readonly object _lock = new();
    private readonly List<DiscordPostMessage> _posts = [];
    private readonly List<(DiscordReplyChannelId ThreadId, string Name)> _threadRenames = [];
    private readonly List<(DiscordReplyChannelId ChannelId, DiscordMessageId MessageId, string Text, bool RemoveComponents)> _updates = [];
    private readonly List<DiscordReplyChannelId> _typingTriggers = [];
    private readonly List<DiscordFileUpload> _uploads = [];

    public IReadOnlyList<DiscordPostMessage> Posts
    {
        get { lock (_lock) return _posts.ToArray(); }
    }

    public void ClearPosts()
    {
        lock (_lock) _posts.Clear();
    }

    public IReadOnlyList<(DiscordReplyChannelId ThreadId, string Name)> ThreadRenames
    {
        get { lock (_lock) return _threadRenames.ToArray(); }
    }

    public IReadOnlyList<(DiscordReplyChannelId ChannelId, DiscordMessageId MessageId, string Text, bool RemoveComponents)> Updates
    {
        get { lock (_lock) return _updates.ToArray(); }
    }

    public IReadOnlyList<DiscordReplyChannelId> TypingTriggers
    {
        get { lock (_lock) return _typingTriggers.ToArray(); }
    }

    public IReadOnlyList<DiscordFileUpload> Uploads
    {
        get { lock (_lock) return _uploads.ToArray(); }
    }

    public Exception? ThrowOnPost { get; set; }
    public Exception? ThrowOnUpload { get; set; }
    // Throws on the next post only, then auto-clears. Lets a test fail a content
    // post while letting a follow-up (e.g. fallback) succeed and be recorded.
    public Exception? ThrowOnceOnPost { get; set; }

    /// <summary>
    /// Failures for the next posts, one per post, in order. A test uses it to give two failed posts of one turn
    /// two different error texts. Posts after the last entry succeed.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<Exception> ThrowInOrderOnPosts { get; } = new();

    private int _messageCounter;

    public Task<DiscordPostResult> PostReplyAsync(DiscordPostMessage message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPost is { } ex)
            throw ex;

        if (ThrowOnceOnPost is { } onceEx)
        {
            ThrowOnceOnPost = null;
            throw onceEx;
        }

        if (ThrowInOrderOnPosts.TryDequeue(out var orderedEx))
            throw orderedEx;

        lock (_lock) _posts.Add(message);

        var messageId = new DiscordMessageId($"msg-{Interlocked.Increment(ref _messageCounter)}");
        DiscordPostResult result;
        if (message.CreateThreadOnMessage is not null)
        {
            var threadId = new DiscordReplyChannelId($"thread-{message.CreateThreadOnMessage.Value.Value}");
            result = new DiscordPostResult(CreatedThreadId: threadId, MessageId: messageId);
        }
        else
        {
            result = new DiscordPostResult(MessageId: messageId);
        }

        return Task.FromResult(result);
    }

    public Task SetThreadNameAsync(DiscordReplyChannelId threadChannelId, string name, CancellationToken cancellationToken = default)
    {
        lock (_lock) _threadRenames.Add((threadChannelId, name));
        return Task.CompletedTask;
    }

    public Task UpdateMessageAsync(DiscordReplyChannelId channelId, DiscordMessageId messageId, string text,
        bool removeComponents = false, CancellationToken cancellationToken = default)
    {
        lock (_lock) _updates.Add((channelId, messageId, text, removeComponents));
        return Task.CompletedTask;
    }

    public Task TriggerTypingAsync(DiscordReplyChannelId channelId, CancellationToken cancellationToken = default)
    {
        lock (_lock) _typingTriggers.Add(channelId);
        return Task.CompletedTask;
    }

    public Task<DiscordMessageId?> UploadFileAsync(DiscordFileUpload upload, CancellationToken cancellationToken = default)
    {
        if (ThrowOnUpload is { } ex)
            throw ex;

        lock (_lock) _uploads.Add(upload);
        var messageId = new DiscordMessageId($"file-{Interlocked.Increment(ref _messageCounter)}");
        return Task.FromResult<DiscordMessageId?>(messageId);
    }
}
