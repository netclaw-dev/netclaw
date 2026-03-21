namespace Netclaw.Channels.Slack;

public interface ISlackReplyClient
{
    Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default);

    Task UploadFileToThreadAsync(
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        string filePath,
        string? filename = null,
        CancellationToken cancellationToken = default);
}
