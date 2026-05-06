// -----------------------------------------------------------------------
// <copyright file="MattermostNetReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Mattermost;

namespace Netclaw.Channels.Mattermost.Transport;

internal sealed class MattermostNetReplyClient : IMattermostReplyClient
{
    private readonly MattermostClient _client;

    public MattermostNetReplyClient(MattermostClient client)
    {
        _client = client;
    }

    public async Task<MattermostPostResult> PostReplyAsync(MattermostPostMessage message, CancellationToken cancellationToken = default)
    {
        var post = await _client.CreatePostAsync(
            channelId: message.ChannelId.Value,
            message: message.Text,
            replyToPostId: message.RootPostId?.Value ?? string.Empty,
            files: message.FileIds);

        return new MattermostPostResult(
            PostId: new MattermostPostId(post.Id));
    }

    public async Task UpdatePostAsync(
        MattermostPostId postId,
        string text,
        CancellationToken cancellationToken = default)
    {
        await _client.UpdatePostAsync(postId.Value, text);
    }
}
