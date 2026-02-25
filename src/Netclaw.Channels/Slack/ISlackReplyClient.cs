namespace Netclaw.Channels.Slack;

public interface ISlackReplyClient
{
    Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default);
}
