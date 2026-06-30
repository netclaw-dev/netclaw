// -----------------------------------------------------------------------
// <copyright file="SlackProcessingOutputRenderer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Slack;

public sealed class SlackProcessingOutputRenderer(ISlackReplyClient replyClient) : IChannelOutputRenderer
{
    private const string ThinkingStatus = "is thinking...";

    public ChannelDescriptorKey Key => ChannelDescriptorKey.FromChannelType(ChannelType.Slack);

    public async ValueTask RenderAsync(
        ChannelOutputRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.EffectKind != ChannelOutputEffectKind.ProcessingIndicator)
            throw new InvalidOperationException("Slack processing renderer only supports processing indicator effects.");

        if (request.Output is not ProcessingStateOutput processing)
            throw new InvalidOperationException("Slack processing renderer requires a processing state output.");

        if (string.IsNullOrWhiteSpace(request.Target.ThreadOrRootId))
            throw new InvalidOperationException("Slack processing indicators require a thread timestamp.");

        await replyClient.SetThreadStatusAsync(
            new SlackChannelId(request.Target.Destination.StableId),
            new SlackThreadTs(request.Target.ThreadOrRootId),
            processing.IsProcessing ? ThinkingStatus : string.Empty,
            cancellationToken);
    }
}
