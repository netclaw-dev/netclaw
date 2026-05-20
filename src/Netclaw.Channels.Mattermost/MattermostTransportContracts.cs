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
/// Normalized Mattermost interactive action response — what the daemon
/// receives when a user clicks an approval button in a Mattermost post and
/// the Mattermost server forwards the click to <c>/api/mattermost/actions</c>.
/// </summary>
/// <param name="ChannelId">Mattermost channel where the click happened.</param>
/// <param name="RootPostId">Thread root the approval prompt was posted in;
///   together with <paramref name="ChannelId"/> this addresses the owning
///   session binding.</param>
/// <param name="CallId">Identifier of the <c>ToolInteractionRequest</c> this
///   click is answering — propagated end-to-end from the session protocol so
///   the session actor can match the response to its pending request.</param>
/// <param name="SelectedKey">Which approval option the user chose, expressed
///   as one of the stable <see cref="ApprovalOptionKeys"/> wire values
///   (e.g. <c>approve_once</c>, <c>approve_session</c>, <c>deny</c>).</param>
/// <param name="SenderId">Mattermost user who actually clicked the
///   button.</param>
/// <param name="RequesterSenderId">Mattermost user the prompt was originally
///   issued for, captured at prompt-mint time. Used by the session actor's
///   <c>approval_wrong_requester</c> check.</param>
/// <param name="ReceivedAt">Timestamp the daemon observed the click.</param>
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
    // Interactive button callbacks arrive via the channel-owned HTTP endpoint
    // (MattermostActionEndpointExtensions) which dispatches directly to the
    // MattermostGatewayActor via the ActorRegistry — there is no
    // InteractionReceived event on the client because the transport client is
    // not on the callback path. (Discord differs: its callbacks arrive through
    // the WebSocket, so its client does expose an InteractionReceived event.)
    event Func<MattermostGatewayMessage, Task>? MessageReceived;

    bool IsConnected { get; }

    MattermostUserId? BotUserId { get; }

    string? BotUsername { get; }

    Task ConnectAsync(string serverUrl, string botToken, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IMattermostReplyClient
{
    Task<MattermostPostResult> PostReplyAsync(MattermostPostMessage message, CancellationToken cancellationToken = default);

    Task UpdatePostAsync(
        MattermostPostId postId,
        string text,
        IReadOnlyList<MattermostAttachment>? attachments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outbound post payload sent through <see cref="IMattermostReplyClient"/>.
/// <para>
/// Mattermost's API treats file uploads and rich-content attachments as
/// independent concepts:
/// </para>
/// <list type="bullet">
/// <item><see cref="FileIds"/> is a list of opaque IDs returned by Mattermost's
///   <c>/api/v4/files</c> upload endpoint. Attaching them to a post causes
///   Mattermost to render the uploaded files (images, documents) inline below
///   the post text — separate from the message body.</item>
/// <item><see cref="Attachments"/> is the Slack-style "message attachments"
///   array — structured rich-content blocks that can carry coloured side bars,
///   markdown blocks, and interactive buttons. Netclaw uses this for approval
///   prompts (see <c>MattermostApprovalPromptBuilder</c>).</item>
/// </list>
/// </summary>
public sealed record MattermostPostMessage(
    MattermostChannelId ChannelId,
    string Text,
    MattermostPostId? RootPostId = null,
    IReadOnlyList<string>? FileIds = null,
    IReadOnlyList<MattermostAttachment>? Attachments = null);

/// <summary>
/// A single Mattermost "message attachment" — a structured block rendered
/// beneath the post text. Attachments can carry interactive actions
/// (buttons) and follow the same rough shape as Slack's legacy attachment
/// model.
/// </summary>
/// <param name="Fallback">Plain-text rendering of the attachment used by
///   surfaces that can't display rich content — push notifications, screen
///   readers, the Mattermost mobile preview text, search indexers. If unset
///   the attachment will be invisible in those surfaces.</param>
/// <param name="Color">Hex colour bar shown along the attachment's left
///   edge. Conventional values: <c>#36a64f</c> (green/info),
///   <c>#ff0000</c> (red/danger).</param>
/// <param name="Text">Markdown body of the attachment.</param>
/// <param name="Actions">Interactive buttons rendered below the
///   attachment text.</param>
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

    public bool IsConnected => false;

    public MattermostUserId? BotUserId => null;

    public string? BotUsername => null;

    public Task ConnectAsync(string serverUrl, string botToken, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Mattermost channel is enabled, but no Mattermost gateway client is configured.");

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// Placeholder reply client that fails loud until Mattermost outbound delivery is wired.
/// </summary>
public sealed class UnconfiguredMattermostReplyClient : IMattermostReplyClient
{
    public Task<MattermostPostResult> PostReplyAsync(MattermostPostMessage message, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Mattermost channel attempted outbound delivery, but no Mattermost reply client is configured.");

    public Task UpdatePostAsync(MattermostPostId postId, string text, IReadOnlyList<MattermostAttachment>? attachments, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Mattermost channel attempted to update a post, but no Mattermost reply client is configured.");
}
