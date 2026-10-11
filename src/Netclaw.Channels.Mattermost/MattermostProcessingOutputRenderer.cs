// -----------------------------------------------------------------------
// <copyright file="MattermostProcessingOutputRenderer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Mattermost;

public sealed class MattermostProcessingOutputRenderer(IMattermostReplyClient replyClient) : IChannelOutputRenderer
{
    public ChannelDescriptorKey Key => ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);

    public async ValueTask RenderAsync(
        ChannelOutputRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Output is not ProcessingStateOutput { IsProcessing: true })
            return;

        // Mattermost typing is a transient pulse; the client clears it after a
        // short window. A call to SendTypingAsync sends one pulse and returns.
        // The Mattermost session binding actor repeats the pulse on a timer
        // while the session still reports processing, because the session emits
        // ProcessingStateOutput(true) once on entering a processing phase.
        // https://docs.mattermost.com/api/reference/mattermost-api#websocket-api
        await replyClient.SendTypingAsync(
            new MattermostChannelId(request.Target.Destination.StableId),
            request.Target.ThreadOrRootId,
            cancellationToken);
    }
}
