using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace Netclaw.Channels.Discord.Transport;

internal sealed class DiscordNetReplyClient : IDiscordReplyClient
{
    private readonly DiscordSocketClient _client;

    public DiscordNetReplyClient(DiscordSocketClient client)
    {
        _client = client;
    }

    private static async Task<IThreadChannel?> FindExistingThreadAsync(ITextChannel textChannel, ulong anchorMessageId)
    {
        var activeThreads = await textChannel.GetActiveThreadsAsync();
        foreach (var t in activeThreads)
        {
            if (t.Id == anchorMessageId || (t is SocketThreadChannel st && st.ParentChannel.Id == textChannel.Id))
            {
                // Discord creates thread IDs that can match the anchor message ID in some cases,
                // but more reliably we need to check active threads. Return the first thread
                // that was created from the anchor message.
                var msg = await textChannel.GetMessageAsync(anchorMessageId);
                if (msg?.Thread is { } thread)
                    return thread;
            }
        }

        // Fallback: try via the message reference directly
        var anchorMsg = await textChannel.GetMessageAsync(anchorMessageId);
        return anchorMsg?.Thread;
    }

    public async Task<DiscordPostResult> PostReplyAsync(DiscordPostMessage message, CancellationToken cancellationToken = default)
    {
        var channelId = ulong.Parse(message.ReplyChannelId.Value);
        var channel = _client.GetChannel(channelId)
            ?? throw new InvalidOperationException(
                $"Discord channel {message.ReplyChannelId.Value} not found in cache.");

        if (channel is not IMessageChannel messageChannel)
            throw new InvalidOperationException(
                $"Discord channel {message.ReplyChannelId.Value} is not a message channel.");

        IMessageChannel targetChannel = messageChannel;
        DiscordReplyChannelId? createdThreadId = null;

        if (message.CreateThreadOnMessage is { } threadAnchor
            && channel is ITextChannel textChannel)
        {
            var anchorId = ulong.Parse(threadAnchor.Value);
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

        var rootRef = message.CreateThreadOnMessage is null && message.RootMessageId is { } rootId
            ? new MessageReference(ulong.Parse(rootId.Value))
            : null;

        await targetChannel.SendMessageAsync(
            text: message.Text,
            messageReference: rootRef,
            components: components);

        return new DiscordPostResult(CreatedThreadId: createdThreadId);
    }
}
