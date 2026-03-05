using Netclaw.Channels.Slack;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class NoopReplyClient : ISlackReplyClient
{
    public Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task UploadFileToThreadAsync(SlackChannelId channelId, SlackThreadTs threadTs, string filePath, string? filename = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
