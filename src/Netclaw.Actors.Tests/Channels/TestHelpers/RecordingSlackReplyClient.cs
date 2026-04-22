using Netclaw.Channels.Slack;
using SlackNet.Blocks;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

public sealed class RecordingSlackReplyClient : ISlackReplyClient
{
    private readonly object _lock = new();
    private readonly List<SlackPostMessage> _posts = [];

    public IReadOnlyList<SlackPostMessage> Posts
    {
        get { lock (_lock) return _posts.ToList(); }
    }

    public Exception? ThrowOnPost { get; set; }

    public void Clear()
    {
        lock (_lock) _posts.Clear();
    }

    public Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPost is { } ex)
            throw ex;
        lock (_lock) _posts.Add(message);
        return Task.CompletedTask;
    }

    public Task<string> PostThreadReplyWithTsAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPost is { } ex)
            throw ex;
        lock (_lock) _posts.Add(message);
        return Task.FromResult("fake.ts");
    }

    public Task UpdateThreadMessageAsync(
        SlackChannelId channelId,
        SlackEventTs messageTs,
        string text,
        IReadOnlyList<Block>? blocks = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UploadFileToThreadAsync(
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        string filePath,
        string? filename = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
