// -----------------------------------------------------------------------
// <copyright file="MattermostIngressMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

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

/// <summary>
/// Sent to the gateway to wire up the actor hierarchy for a proactively-created
/// Mattermost session. For DMs, <paramref name="DirectMessageUserId"/> carries
/// the target user so the conversation actor can validate the user ACL instead
/// of the channel ACL (DM channel ids are ephemeral and never allowlisted).
/// </summary>
public sealed record StartMattermostProactiveThread(
    MattermostChannelId ChannelId,
    MattermostRootPostId RootPostId,
    SessionId SessionId,
    MattermostUserId? DirectMessageUserId = null) : INoSerializationVerificationNeeded;

public sealed record MattermostProactiveThreadAck(SessionId SessionId) : INoSerializationVerificationNeeded;

internal sealed class PendingApprovalRequest : Netclaw.Channels.PendingApprovalRequest<MattermostPostId>
{
    public PendingApprovalRequest(ToolInteractionRequest request) : base(request)
    {
    }

    public PendingApprovalRequest(
        ToolCallId callId,
        string? requesterSenderId,
        PrincipalClassification? requesterPrincipal,
        IReadOnlyList<string> optionKeys,
        MattermostPostId? promptPostId,
        string? toolName = null,
        string? displayText = null)
        : base(callId, requesterSenderId, requesterPrincipal, optionKeys, promptPostId, toolName, displayText)
    {
    }

    public MattermostPostId? PromptPostId
    {
        get => PromptId;
        set => PromptId = value;
    }
}
