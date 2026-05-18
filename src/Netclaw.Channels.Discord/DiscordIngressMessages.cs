// -----------------------------------------------------------------------
// <copyright file="DiscordIngressMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Channels.Discord;

public sealed record DiscordFileReference(
    string Name,
    string MimeType,
    long Size,
    string Url);

public sealed record DiscordThreadInbound(
    SessionId SessionId,
    DiscordChannelId ChannelId,
    DiscordReplyChannelId ReplyChannelId,
    DiscordThreadOrMessageId ThreadOrMessageId,
    DiscordMessageId? RootMessageId,
    DiscordEventId EventId,
    DiscordUserId SenderId,
    TrustAudience Audience,
    PrincipalClassification Principal,
    SourceProvenance Provenance,
    string Text,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<DiscordFileReference>? Attachments = null);

public sealed record DiscordApprovalResponse(
    DiscordChannelId ChannelId,
    DiscordThreadOrMessageId ThreadOrMessageId,
    ToolCallId CallId,
    string SelectedKey,
    DiscordUserId SenderId,
    DiscordUserId? RequesterSenderId = null);

/// <summary>
/// Sent to the gateway to wire up the actor hierarchy for a proactively-created
/// Discord thread. The message has already been posted and the thread created;
/// this initializes the session pipeline so user replies route to a live
/// session.
/// </summary>
public sealed record StartProactiveThread(
    DiscordChannelId ChannelId,
    DiscordReplyChannelId ReplyChannelId,
    DiscordThreadOrMessageId ThreadOrMessageId,
    SessionId SessionId) : INoSerializationVerificationNeeded;

/// <summary>
/// Acknowledgement that a proactive thread's session pipeline was initialized.
/// Returned by <see cref="DiscordSessionBindingActor"/> in response to
/// <see cref="StartProactiveThread"/>.
/// </summary>
public sealed record ProactiveThreadAck(SessionId SessionId) : INoSerializationVerificationNeeded;

internal sealed class PendingApprovalRequest(ToolInteractionRequest request)
{
    public ToolInteractionRequest Request { get; } = request;
    public ToolCallId CallId => Request.CallId;

    public DiscordUserId? RequesterSenderId { get; } =
        request.RequesterSenderId is not null ? new DiscordUserId(request.RequesterSenderId.Value.Value) : null;

    public PrincipalClassification? RequesterPrincipal => Request.RequesterPrincipal;
    public DiscordMessageId? PromptMessageId { get; set; }
}

