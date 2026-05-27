// -----------------------------------------------------------------------
// <copyright file="MattermostIngressMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Channels.Mattermost;

public sealed record MattermostFileReference(
    string Name,
    string MimeType,
    long Size,
    string Url);

public sealed record MattermostThreadInbound(
    SessionId SessionId,
    MattermostChannelId ChannelId,
    MattermostPostId PostId,
    MattermostRootPostId RootPostId,
    MattermostEventId EventId,
    MattermostUserId SenderId,
    TrustAudience Audience,
    PrincipalClassification Principal,
    SourceProvenance Provenance,
    string Text,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<MattermostFileReference>? Attachments = null);

public sealed record MattermostApprovalResponse(
    MattermostChannelId ChannelId,
    MattermostRootPostId RootPostId,
    ToolCallId CallId,
    string SelectedKey,
    MattermostUserId SenderId,
    MattermostUserId? RequesterSenderId = null,
    MattermostPostId? PromptPostId = null);

public sealed record StartMattermostProactiveThread(
    MattermostChannelId ChannelId,
    MattermostRootPostId RootPostId,
    SessionId SessionId);

public sealed record MattermostProactiveThreadAck(SessionId SessionId);

internal sealed class PendingApprovalRequest(ToolInteractionRequest request)
{
    public ToolInteractionRequest Request { get; } = request;
    public ToolCallId CallId => Request.CallId;

    public MattermostUserId? RequesterSenderId { get; } =
        request.RequesterSenderId is not null ? new MattermostUserId(request.RequesterSenderId.Value.Value) : null;

    public PrincipalClassification? RequesterPrincipal => Request.RequesterPrincipal;
    public MattermostPostId? PromptPostId { get; set; }
}
