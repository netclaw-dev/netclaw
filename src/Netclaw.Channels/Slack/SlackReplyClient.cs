using SlackNet;
using SlackNet.WebApi;

namespace Netclaw.Channels.Slack;

public sealed class SlackReplyClient(ISlackApiClient slackApiClient) : ISlackReplyClient
{
    public Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
    {
        var blocks = SlackBlockConverter.Convert(message.Text);

        return slackApiClient.Chat.PostMessage(new Message
        {
            Channel = message.ChannelId,
            ThreadTs = message.ThreadTs,
            Text = message.Text, // fallback for notifications
            Blocks = blocks
        });
    }
}
