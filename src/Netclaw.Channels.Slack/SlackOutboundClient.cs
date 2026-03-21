using SlackNet;
using SlackNet.WebApi;

namespace Netclaw.Channels.Slack;

public sealed class SlackOutboundClient(ISlackApiClient slackApiClient) : ISlackOutboundClient
{
    public async Task<SlackChannelId> OpenDmChannelAsync(SlackUserId userId, CancellationToken ct = default)
    {
        var channelId = await slackApiClient.Conversations.Open(new[] { userId.Value }, ct);
        return new SlackChannelId(channelId);
    }

    public async Task<SlackNewThread> PostNewThreadAsync(SlackChannelId channelId, string text, CancellationToken ct = default)
    {
        var blocks = SlackBlockConverter.Convert(text);
        var response = await slackApiClient.Chat.PostMessage(new Message
        {
            Channel = channelId.Value,
            Text = text,
            Blocks = blocks
        }, ct);

        var threadTs = new SlackThreadTs(response.Ts);
        return new SlackNewThread(channelId, threadTs);
    }
}
