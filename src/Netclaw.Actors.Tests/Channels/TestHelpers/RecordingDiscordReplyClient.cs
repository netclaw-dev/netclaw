using Netclaw.Channels.Discord;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class RecordingDiscordReplyClient : IDiscordReplyClient
{
    public List<DiscordPostMessage> Posts { get; } = [];
    public List<(DiscordReplyChannelId ThreadId, string Name)> ThreadRenames { get; } = [];
    public Exception? ThrowOnPost { get; set; }

    public Task<DiscordPostResult> PostReplyAsync(DiscordPostMessage message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnPost is { } ex)
            throw ex;

        Posts.Add(message);

        DiscordPostResult result = DiscordPostResult.Default;
        if (message.CreateThreadOnMessage is not null)
        {
            var threadId = new DiscordReplyChannelId($"thread-{message.CreateThreadOnMessage.Value.Value}");
            result = new DiscordPostResult(CreatedThreadId: threadId);
        }

        return Task.FromResult(result);
    }

    public Task SetThreadNameAsync(DiscordReplyChannelId threadChannelId, string name, CancellationToken cancellationToken = default)
    {
        ThreadRenames.Add((threadChannelId, name));
        return Task.CompletedTask;
    }
}
