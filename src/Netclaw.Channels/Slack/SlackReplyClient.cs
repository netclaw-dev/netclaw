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

    public async Task UploadFileToThreadAsync(
        string channelId,
        string threadTs,
        string filePath,
        string? filename = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedFilename = filename ?? Path.GetFileName(filePath);
        await using var stream = System.IO.File.OpenRead(filePath);
        var upload = new FileUpload(resolvedFilename, stream);

        await slackApiClient.Files.Upload(
            upload,
            channelId,
            threadTs,
            initialComment: null,
            cancellationToken);
    }
}
