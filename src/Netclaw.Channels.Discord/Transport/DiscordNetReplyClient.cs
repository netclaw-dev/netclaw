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

    public async Task PostReplyAsync(DiscordPostMessage message, CancellationToken cancellationToken = default)
    {
        var channelId = ulong.Parse(message.ReplyChannelId.Value);
        var channel = _client.GetChannel(channelId)
            ?? throw new InvalidOperationException(
                $"Discord channel {message.ReplyChannelId.Value} not found in cache.");

        if (channel is not IMessageChannel messageChannel)
            throw new InvalidOperationException(
                $"Discord channel {message.ReplyChannelId.Value} is not a message channel.");

        var rootRef = message.RootMessageId is { } rootId
            ? new MessageReference(ulong.Parse(rootId.Value))
            : null;

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

        await messageChannel.SendMessageAsync(
            text: message.Text,
            messageReference: rootRef,
            components: components);
    }
}
