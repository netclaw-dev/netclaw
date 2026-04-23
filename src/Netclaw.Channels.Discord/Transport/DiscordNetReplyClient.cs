using Discord;
using Discord.WebSocket;

namespace Netclaw.Channels.Discord.Transport;

internal sealed class DiscordNetReplyClient : IDiscordReplyClient
{
    private readonly DiscordSocketClient _client;

    public DiscordNetReplyClient(DiscordSocketClient client)
    {
        _client = client;
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
            var thread = await textChannel.CreateThreadAsync(
                threadName,
                ThreadType.PublicThread,
                ThreadArchiveDuration.OneDay,
                message: await textChannel.GetMessageAsync(anchorId));
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
