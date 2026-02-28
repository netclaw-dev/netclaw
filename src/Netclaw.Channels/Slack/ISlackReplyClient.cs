namespace Netclaw.Channels.Slack;

public interface ISlackReplyClient
{
    Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default);

    Task UploadFileToThreadAsync(
        string channelId,
        string threadTs,
        string filePath,
        string? filename = null,
        CancellationToken cancellationToken = default);
}
