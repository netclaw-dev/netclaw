// -----------------------------------------------------------------------
// <copyright file="RecordingDiscordReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Discord;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class RecordingDiscordReplyClient : IDiscordReplyClient
{
    public List<DiscordPostMessage> Posts { get; } = [];
    public List<(DiscordReplyChannelId ThreadId, string Name)> ThreadRenames { get; } = [];
    public List<(DiscordReplyChannelId ChannelId, DiscordMessageId MessageId, string Text, bool RemoveComponents)> Updates { get; } = [];
    public List<DiscordReplyChannelId> TypingTriggers { get; } = [];
    public List<DiscordFileUpload> Uploads { get; } = [];
    public Exception? ThrowOnPost { get; set; }
    public Exception? ThrowOnUpload { get; set; }
    // Throws on the next post only, then auto-clears. Lets a test fail a content
    // post while letting a follow-up (e.g. fallback) succeed and be recorded.
    public Exception? ThrowOnceOnPost { get; set; }

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

        Posts.Add(message);

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
        ThreadRenames.Add((threadChannelId, name));
        return Task.CompletedTask;
    }

    public Task UpdateMessageAsync(DiscordReplyChannelId channelId, DiscordMessageId messageId, string text,
        bool removeComponents = false, CancellationToken cancellationToken = default)
    {
        Updates.Add((channelId, messageId, text, removeComponents));
        return Task.CompletedTask;
    }

    public Task TriggerTypingAsync(DiscordReplyChannelId channelId, CancellationToken cancellationToken = default)
    {
        TypingTriggers.Add(channelId);
        return Task.CompletedTask;
    }

    public Task<DiscordMessageId?> UploadFileAsync(DiscordFileUpload upload, CancellationToken cancellationToken = default)
    {
        if (ThrowOnUpload is { } ex)
            throw ex;

        Uploads.Add(upload);
        var messageId = new DiscordMessageId($"file-{Interlocked.Increment(ref _messageCounter)}");
        return Task.FromResult<DiscordMessageId?>(messageId);
    }
}
