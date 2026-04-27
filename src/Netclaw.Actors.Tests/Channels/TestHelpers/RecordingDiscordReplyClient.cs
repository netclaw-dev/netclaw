using Netclaw.Channels.Discord;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class RecordingDiscordReplyClient : IDiscordReplyClient
{
    public List<DiscordPostMessage> Posts { get; } = [];
    public List<(DiscordReplyChannelId ThreadId, string Name)> ThreadRenames { get; } = [];
    public List<(DiscordReplyChannelId ChannelId, DiscordMessageId MessageId, string Text, bool RemoveComponents)> Updates { get; } = [];
    public Exception? ThrowOnPost { get; set; }

    private int _messageCounter;

    public Task<DiscordPostResult> PostReplyAsync(DiscordPostMessage message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPost is { } ex)
            throw ex;

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
}
