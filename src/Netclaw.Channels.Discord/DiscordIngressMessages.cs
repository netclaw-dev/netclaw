// -----------------------------------------------------------------------
// <copyright file="DiscordIngressMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

internal sealed class PendingApprovalRequest(ToolInteractionRequest request)
{
    public ToolInteractionRequest Request { get; } = request;
    public ToolCallId CallId => Request.CallId;

    public DiscordUserId? RequesterSenderId { get; } =
        request.RequesterSenderId is not null ? new DiscordUserId(request.RequesterSenderId) : null;

    public PrincipalClassification? RequesterPrincipal => Request.RequesterPrincipal;
    public DiscordMessageId? PromptMessageId { get; set; }
}

