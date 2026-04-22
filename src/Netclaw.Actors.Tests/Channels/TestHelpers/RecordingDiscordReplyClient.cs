using Netclaw.Channels.Discord;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class RecordingDiscordReplyClient : IDiscordReplyClient
{
    public List<DiscordPostMessage> Posts { get; } = [];
    public Exception? ThrowOnPost { get; set; }

    public Task PostReplyAsync(DiscordPostMessage message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPost is { } ex)
            throw ex;

        Posts.Add(message);
        return Task.CompletedTask;
    }
}
