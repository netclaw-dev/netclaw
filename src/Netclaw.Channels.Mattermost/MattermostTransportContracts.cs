// -----------------------------------------------------------------------
// <copyright file="MattermostTransportContracts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Mattermost;

/// <summary>
/// Normalized inbound Mattermost message payload emitted by the transport client.
/// </summary>
public sealed record MattermostGatewayMessage(
    MattermostEventId EventId,
    MattermostChannelId ChannelId,
    MattermostPostId PostId,
    MattermostRootPostId RootPostId,
    MattermostUserId SenderId,
    bool IsBotMessage,
    bool IsDirectMessage,
    bool ContainsBotMention,
    string Text,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<MattermostFileReference>? Attachments = null);

/// <summary>
/// Normalized Mattermost interactive action response emitted by the transport client.
/// </summary>
public sealed record MattermostGatewayInteraction(
    MattermostChannelId ChannelId,
    MattermostRootPostId RootPostId,
    string CallId,
    string SelectedKey,
    MattermostUserId SenderId,
    MattermostUserId? RequesterSenderId,
    DateTimeOffset ReceivedAt);

public interface IMattermostGatewayClient
{
    event Func<MattermostGatewayMessage, Task>? MessageReceived;

    event Func<MattermostGatewayInteraction, Task>? InteractionReceived;

    bool IsConnected { get; }

    MattermostUserId? BotUserId { get; }

    string? BotUsername { get; }

    Task ConnectAsync(string serverUrl, string botToken, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task HandleActionCallbackAsync(MattermostGatewayInteraction interaction);
}

public interface IMattermostReplyClient
{
    Task<MattermostPostResult> PostReplyAsync(MattermostPostMessage message, CancellationToken cancellationToken = default);

    Task UpdatePostAsync(
        MattermostPostId postId,
        string text,
        CancellationToken cancellationToken = default);

    Task UpdatePostAsync(
        MattermostPostId postId,
        string text,
        IReadOnlyList<MattermostAttachment>? attachments,
        CancellationToken cancellationToken = default);
}

public sealed record MattermostPostMessage(
    MattermostChannelId ChannelId,
    string Text,
    MattermostPostId? RootPostId = null,
    IReadOnlyList<string>? FileIds = null,
    IReadOnlyList<MattermostAttachment>? Attachments = null);

public sealed record MattermostAttachment(
    string? Fallback = null,
    string? Color = null,
    string? Text = null,
    IReadOnlyList<MattermostAttachmentAction>? Actions = null);

public sealed record MattermostAttachmentAction(
    string Id,
    string Name,
    string IntegrationUrl,
    Dictionary<string, string> Context,
    string Style = "default");

public sealed record MattermostPostResult(
    MattermostPostId? PostId = null)
{
    public static readonly MattermostPostResult Default = new();
}

/// <summary>
/// Placeholder transport client that fails loud until the real Mattermost
/// gateway wiring is added.
/// </summary>
public sealed class UnconfiguredMattermostGatewayClient : IMattermostGatewayClient
{
    public event Func<MattermostGatewayMessage, Task>? MessageReceived
    {
        add { }
        remove { }
    }

    public event Func<MattermostGatewayInteraction, Task>? InteractionReceived
    {
        add { }
        remove { }
    }

    public bool IsConnected => false;

    public MattermostUserId? BotUserId => null;

    public string? BotUsername => null;

    public Task ConnectAsync(string serverUrl, string botToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Mattermost channel is enabled, but no Mattermost gateway client is configured.");

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task HandleActionCallbackAsync(MattermostGatewayInteraction interaction)
        => throw new InvalidOperationException(
            "Mattermost channel is enabled, but no Mattermost gateway client is configured.");
}

/// <summary>
/// Placeholder reply client that fails loud until Mattermost outbound delivery is wired.
/// </summary>
public sealed class UnconfiguredMattermostReplyClient : IMattermostReplyClient
{
    public Task<MattermostPostResult> PostReplyAsync(MattermostPostMessage message, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Mattermost channel attempted outbound delivery, but no Mattermost reply client is configured.");

    public Task UpdatePostAsync(MattermostPostId postId, string text, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Mattermost channel attempted to update a post, but no Mattermost reply client is configured.");

    public Task UpdatePostAsync(MattermostPostId postId, string text, IReadOnlyList<MattermostAttachment>? attachments, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Mattermost channel attempted to update a post, but no Mattermost reply client is configured.");
}
