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

    private static ulong ParseSnowflake(string value, string label)
    {
        if (!ulong.TryParse(value, out var id))
            throw new InvalidOperationException($"Discord {label} '{value}' is not a valid snowflake.");
        return id;
    }
}
