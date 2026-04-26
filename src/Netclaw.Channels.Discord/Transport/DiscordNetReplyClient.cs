using System.Collections.Concurrent;
using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace Netclaw.Channels.Discord.Transport;

internal sealed class DiscordNetReplyClient : IDiscordReplyClient
{
    private const int MaxRestChannelCacheSize = 1000;

    private readonly DiscordSocketClient _client;
    private readonly ConcurrentDictionary<ulong, IMessageChannel> _restChannelCache = new();

    public DiscordNetReplyClient(DiscordSocketClient client)
    {
        _client = client;
    }

    private static async Task<IThreadChannel?> FindExistingThreadAsync(
        ITextChannel textChannel, ulong anchorMessageId)
    {
        var anchorMsg = await textChannel.GetMessageAsync(anchorMessageId);
        if (anchorMsg?.Thread is { } thread)
            return thread;

        var activeThreads = await textChannel.GetActiveThreadsAsync();
        return activeThreads.FirstOrDefault(t => t.Id == anchorMessageId);
    }

    public async Task<DiscordPostResult> PostReplyAsync(DiscordPostMessage message, CancellationToken cancellationToken = default)
    {
        var channelId = ParseSnowflake(message.ReplyChannelId.Value, "reply channel ID");

        // Socket cache misses for DM channels — fall back to REST API.
        IMessageChannel? messageChannel = _client.GetChannel(channelId) as IMessageChannel;
        if (messageChannel is null && !_restChannelCache.TryGetValue(channelId, out messageChannel))
        {
            var restChannel = await _client.Rest.GetChannelAsync(channelId);
            messageChannel = restChannel as IMessageChannel;
            if (messageChannel is not null)
            {
                // Safety valve — evict stale entries if the cache grows too large.
                // Full clear is acceptable here because the cache repopulates lazily
                // and only DM channels (socket cache misses) land here.
                if (_restChannelCache.Count >= MaxRestChannelCacheSize)
                    _restChannelCache.Clear();
                _restChannelCache[channelId] = messageChannel;
            }
        }

        if (messageChannel is null)
            throw new InvalidOperationException(
                $"Discord channel {message.ReplyChannelId.Value} not found or is not a message channel.");

        IMessageChannel targetChannel = messageChannel;
        DiscordReplyChannelId? createdThreadId = null;

        if (message.CreateThreadOnMessage is { } threadAnchor
            && messageChannel is ITextChannel textChannel)
        {
            var anchorId = ParseSnowflake(threadAnchor.Value, "anchor message ID");
            var threadName = message.ThreadName ?? "Conversation";
            var anchorMessage = await textChannel.GetMessageAsync(anchorId)
                ?? throw new InvalidOperationException(
                    $"Discord message {threadAnchor.Value} not found — cannot create thread.");
            IThreadChannel thread;
            try
            {
                thread = await textChannel.CreateThreadAsync(
                    threadName,
                    ThreadType.PublicThread,
                    ThreadArchiveDuration.OneDay,
                    message: anchorMessage);
            }
            catch (HttpException httpEx) when (httpEx.HttpCode == HttpStatusCode.BadRequest)
            {
                // Thread already exists on this message (race between two simultaneous
                // messages). Try to find and use the existing thread.
                var existingThread = await FindExistingThreadAsync(textChannel, anchorId);
                if (existingThread is null)
                    throw new InvalidOperationException(
                        $"Failed to create thread on message {threadAnchor.Value} and could not find existing thread.",
                        httpEx);
                thread = existingThread;
            }
            targetChannel = thread;
            createdThreadId = new DiscordReplyChannelId(thread.Id.ToString());
        }

        MessageComponent? components = null;
        if (message.Buttons is { Count: > 0 })
        {
            var builder = new ComponentBuilder();
            foreach (var button in message.Buttons)
            {
                builder.WithButton(
                    label: button.Label,
                    customId: button.CustomId,
                    style: (ButtonStyle)(int)button.Style);
            }

            components = builder.Build();
        }

        MessageReference? rootRef = null;
        if (message.CreateThreadOnMessage is null && message.RootMessageId is { } rootId)
            rootRef = new MessageReference(ParseSnowflake(rootId.Value, "root message ID"));

        await targetChannel.SendMessageAsync(
            text: message.Text,
            messageReference: rootRef,
            components: components);

        return new DiscordPostResult(CreatedThreadId: createdThreadId);
    }

    public async Task SetThreadNameAsync(DiscordReplyChannelId threadChannelId, string name, CancellationToken cancellationToken = default)
    {
        var channelId = ParseSnowflake(threadChannelId.Value, "thread channel ID");
        var channel = _client.GetChannel(channelId);
        if (channel is not IThreadChannel thread)
            return;

        await thread.ModifyAsync(props =>
        {
            props.Name = name.Length > 100 ? name[..100] : name;
        }, new RequestOptions { CancelToken = cancellationToken });
    }

    private static ulong ParseSnowflake(string value, string label)
    {
        if (!ulong.TryParse(value, out var id))
            throw new InvalidOperationException($"Discord {label} '{value}' is not a valid snowflake.");
        return id;
    }
}
