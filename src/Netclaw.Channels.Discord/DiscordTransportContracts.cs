namespace Netclaw.Channels.Discord;

/// <summary>
/// Normalized inbound Discord message payload emitted by the transport client.
/// </summary>
public sealed record DiscordGatewayMessage(
    DiscordEventId EventId,
    DiscordChannelId ChannelId,
    DiscordReplyChannelId ReplyChannelId,
    DiscordMessageId MessageId,
    DiscordThreadOrMessageId ThreadOrMessageId,
    DiscordMessageId? RootMessageId,
    DiscordUserId SenderId,
    bool IsBotMessage,
    bool IsDirectMessage,
    bool ContainsBotMention,
    string Text,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<DiscordFileReference>? Attachments = null);

/// <summary>
/// Normalized Discord interaction response payload emitted by the transport client.
/// </summary>
public sealed record DiscordGatewayInteraction(
    DiscordChannelId ChannelId,
    DiscordThreadOrMessageId ThreadOrMessageId,
    string CallId,
    string SelectedKey,
    DiscordUserId SenderId,
    DiscordUserId? RequesterSenderId,
    DateTimeOffset ReceivedAt);

public interface IDiscordGatewayClient
{
    event Func<DiscordGatewayMessage, Task>? MessageReceived;

    event Func<DiscordGatewayInteraction, Task>? InteractionReceived;

    bool IsConnected { get; }

    DiscordUserId? BotUserId { get; }

    Task ConnectAsync(string botToken, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IDiscordReplyClient
{
    Task<DiscordPostResult> PostReplyAsync(DiscordPostMessage message, CancellationToken cancellationToken = default);

    Task SetThreadNameAsync(DiscordReplyChannelId threadChannelId, string name, CancellationToken cancellationToken = default);

    Task UpdateMessageAsync(
        DiscordReplyChannelId channelId,
        DiscordMessageId messageId,
        string text,
        bool removeComponents = false,
        CancellationToken cancellationToken = default);
}

public sealed record DiscordPostMessage(
    DiscordReplyChannelId ReplyChannelId,
    string Text,
    DiscordMessageId? RootMessageId = null,
    IReadOnlyList<DiscordButtonSpec>? Buttons = null,
    DiscordMessageId? CreateThreadOnMessage = null,
    string? ThreadName = null);

public sealed record DiscordPostResult(
    DiscordReplyChannelId? CreatedThreadId = null,
    DiscordMessageId? MessageId = null)
{
    public static readonly DiscordPostResult Default = new();
}

public sealed record DiscordButtonSpec(
    string CustomId,
    string Label,
    DiscordButtonStyle Style);

public enum DiscordButtonStyle
{
    Primary = 1,
    Secondary = 2,
    Success = 3,
    Danger = 4
}

/// <summary>
/// Placeholder transport client that fails loud until the real Discord gateway
/// wiring is added in follow-up implementation tasks.
/// </summary>
public sealed class UnconfiguredDiscordGatewayClient : IDiscordGatewayClient
{
    public event Func<DiscordGatewayMessage, Task>? MessageReceived
    {
        add { }
        remove { }
    }

    public event Func<DiscordGatewayInteraction, Task>? InteractionReceived
    {
        add { }
        remove { }
    }

    public bool IsConnected => false;

    public DiscordUserId? BotUserId => null;

    public Task ConnectAsync(string botToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Discord channel is enabled, but no Discord gateway client is configured.");

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// Placeholder reply client that fails loud until Discord outbound delivery is wired.
/// </summary>
public sealed class UnconfiguredDiscordReplyClient : IDiscordReplyClient
{
    public Task<DiscordPostResult> PostReplyAsync(DiscordPostMessage message, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Discord channel attempted outbound delivery, but no Discord reply client is configured.");

    public Task SetThreadNameAsync(DiscordReplyChannelId threadChannelId, string name, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Discord channel attempted to set thread name, but no Discord reply client is configured.");

    public Task UpdateMessageAsync(DiscordReplyChannelId channelId, DiscordMessageId messageId, string text,
        bool removeComponents = false, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Discord channel attempted to update a message, but no Discord reply client is configured.");
}
