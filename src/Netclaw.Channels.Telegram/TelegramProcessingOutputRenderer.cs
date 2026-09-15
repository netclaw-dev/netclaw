// -----------------------------------------------------------------------
// <copyright file="TelegramProcessingOutputRenderer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Telegram.Bot.Types.Enums;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Telegram;

public sealed class TelegramProcessingOutputRenderer(TelegramTransport transport) : IChannelOutputRenderer
{
    public ChannelDescriptorKey Key => ChannelDescriptorKey.FromChannelType(ChannelType.Telegram);

    public async ValueTask RenderAsync(
        ChannelOutputRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Output is not ProcessingStateOutput { IsProcessing: true })
            return;

        if (!long.TryParse(request.Target.Destination.StableId, out var chatId))
            return;

        await transport.SendChatActionAsync(chatId, ChatAction.Typing, cancellationToken);
    }
}
