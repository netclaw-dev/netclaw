// -----------------------------------------------------------------------
// <copyright file="DiscordTransportContracts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    IReadOnlyList<DiscordFileReference>? Attachments = null,
    bool IsInThread = false);

/// <summary>
/// Normalized Discord interaction response payload emitted by the transport client.
/// </summary>
/// <remarks>
/// <paramref name="PromptMessageId"/> is the ID of the message that contained
/// the clicked button. Discord component interactions always carry the source
/// message, so the binding actor can redraw the prompt to its resolved state
/// even after passivation has dropped its in-memory pending-approval entry.
/// Null for text-reply responses (no component payload). See issue #939.
///
/// <paramref name="ReplyChannelId"/> is the actual Discord channel ID where
/// the prompt lives (the channel that the cold-spawned binding should target
/// for <c>chat.update</c>). It is distinct from <paramref name="ThreadOrMessageId"/>,
/// which for top-level guild prompts is the prompt's *message* ID, not its
/// channel ID. Older code derived the binding's reply channel from
/// <paramref name="ThreadOrMessageId"/>, which silently broke the cold-spawn
/// redraw for top-level guild prompts. Null in legacy paths that haven't
/// adopted the explicit field; consumers must fall back to <paramref name="ChannelId"/>
/// when null. See issue #939.
/// </remarks>
public sealed record DiscordGatewayInteraction(
    DiscordChannelId ChannelId,
    DiscordThreadOrMessageId ThreadOrMessageId,
    string CallId,
    string SelectedKey,
    DiscordUserId SenderId,
    DiscordUserId? RequesterSenderId,
    DateTimeOffset ReceivedAt,
    DiscordMessageId? PromptMessageId = null,
    DiscordReplyChannelId? ReplyChannelId = null);

public sealed record DiscordGatewaySnapshot(
    bool IsConnected,
    bool IsReady,
    string? HealthDetail,
    DiscordUserId? BotUserId) : IGatewaySnapshot;

public interface IDiscordGatewayClient
{
    event Func<DiscordGatewayMessage, Task>? MessageReceived;

    event Func<DiscordGatewayInteraction, Task>? InteractionReceived;

    /// <summary>
    /// Raised when the current Discord socket/session must be discarded and
    /// replaced with a fresh login/start cycle.
    /// </summary>
    event Func<string, Task>? CleanReconnectRequired;

    /// <summary>
    /// Raised when the lifecycle actor successfully reconnects after a transient
    /// failure. The snapshot contains the restored bot identity and health state.
    /// </summary>
    event Func<DiscordGatewaySnapshot, Task>? ConnectionRestored;

    Task<DiscordGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<DiscordGatewaySnapshot> ConnectAsync(string botToken, CancellationToken cancellationToken = default);

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

    Task TriggerTypingAsync(DiscordReplyChannelId channelId, CancellationToken cancellationToken = default);

    Task<DiscordMessageId?> UploadFileAsync(DiscordFileUpload upload, CancellationToken cancellationToken = default);
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

public sealed record DiscordFileUpload(
    DiscordReplyChannelId ReplyChannelId,
    string FilePath,
    string FileName,
    string Text,
    DiscordMessageId? RootMessageId = null);

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

    public event Func<string, Task>? CleanReconnectRequired
    {
        add { }
        remove { }
    }

    public event Func<DiscordGatewaySnapshot, Task>? ConnectionRestored
    {
        add { }
        remove { }
    }

    public Task<DiscordGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DiscordGatewaySnapshot(
            IsConnected: false,
            IsReady: false,
            HealthDetail: "Discord gateway client is not configured.",
            BotUserId: null));

    public Task<DiscordGatewaySnapshot> ConnectAsync(string botToken, CancellationToken cancellationToken = default)
        => Task.FromException<DiscordGatewaySnapshot>(new InvalidOperationException(
            "Discord channel is enabled, but no Discord gateway client is configured."));

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

    public Task TriggerTypingAsync(DiscordReplyChannelId channelId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Discord channel attempted to trigger typing, but no Discord reply client is configured.");

    public Task<DiscordMessageId?> UploadFileAsync(DiscordFileUpload upload, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Discord channel attempted to upload a file, but no Discord reply client is configured.");
}
