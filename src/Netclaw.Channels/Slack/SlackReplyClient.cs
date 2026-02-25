using SlackNet;
using SlackNet.WebApi;

namespace Netclaw.Channels.Slack;

public sealed class SlackReplyClient(ISlackApiClient slackApiClient) : ISlackReplyClient
{
    public Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
    {
        return slackApiClient.Chat.PostMessage(new Message
        {
            Channel = message.ChannelId,
            ThreadTs = message.ThreadTs,
            Text = message.Text
        });
    }
}
