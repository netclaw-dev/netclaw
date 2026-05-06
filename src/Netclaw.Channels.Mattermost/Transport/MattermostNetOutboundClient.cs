// -----------------------------------------------------------------------
// <copyright file="MattermostNetOutboundClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Mattermost;

namespace Netclaw.Channels.Mattermost.Transport;

internal sealed class MattermostNetOutboundClient : IMattermostOutboundClient
{
    private readonly MattermostClient _client;

    public MattermostNetOutboundClient(MattermostClient client)
    {
        _client = client;
    }

    public async Task<MattermostChannelId> OpenDmChannelAsync(MattermostUserId userId, CancellationToken ct = default)
    {
        var channel = await _client.CreateDirectChannelAsync(userId.Value);
        return new MattermostChannelId(channel.Id);
    }

    public async Task<MattermostNewThread> PostNewThreadAsync(MattermostChannelId channelId, string text, CancellationToken ct = default)
    {
        var post = await _client.CreatePostAsync(
            channelId: channelId.Value,
            message: text);

        if (string.IsNullOrEmpty(post.Id))
            throw new InvalidOperationException(
                "Mattermost returned no post ID — the message was not delivered");

        return new MattermostNewThread(channelId, new MattermostRootPostId(post.Id));
    }
}
