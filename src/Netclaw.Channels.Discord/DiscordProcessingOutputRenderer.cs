// -----------------------------------------------------------------------
// <copyright file="DiscordProcessingOutputRenderer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Discord;

public sealed class DiscordProcessingOutputRenderer(IDiscordReplyClient replyClient) : IChannelOutputRenderer
{
    public ChannelDescriptorKey Key => ChannelDescriptorKey.FromChannelType(ChannelType.Discord);

    public async ValueTask RenderAsync(
        ChannelOutputRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Output is not ProcessingStateOutput { IsProcessing: true })
            return;

        await replyClient.TriggerTypingAsync(
            new DiscordReplyChannelId(request.Target.Destination.StableId),
            cancellationToken);
    }
}
