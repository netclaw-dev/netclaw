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
using static Netclaw.Actors.Sessions.SessionProtocol;

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
    DiscordUserId? RequesterSenderId = null,
    DiscordMessageId? PromptMessageId = null);

/// <summary>
/// Sent to the gateway to wire up the actor hierarchy for a proactively-created
/// Discord session. Channel posts use a Discord thread; DMs use the root DM
/// message as the stable session anchor.
/// </summary>
public sealed record StartProactiveThread(
    DiscordChannelId ChannelId,
    DiscordReplyChannelId ReplyChannelId,
    DiscordThreadOrMessageId ThreadOrMessageId,
    SessionId SessionId,
    DiscordUserId? DirectMessageUserId = null,
    DiscordMessageId? RootMessageId = null) : INoSerializationVerificationNeeded;

/// <summary>
/// Acknowledgement that a proactive thread's session pipeline was initialized.
/// Returned by <see cref="DiscordSessionBindingActor"/> in response to
/// <see cref="StartProactiveThread"/>.
/// </summary>
public sealed record ProactiveThreadAck(SessionId SessionId) : INoSerializationVerificationNeeded;

internal sealed class PendingApprovalRequest : Netclaw.Channels.PendingApprovalRequest<DiscordMessageId>
{
    public PendingApprovalRequest(ToolInteractionRequest request) : base(request)
    {
    }

    public PendingApprovalRequest(
        ToolCallId callId,
        string? requesterSenderId,
        PrincipalClassification? requesterPrincipal,
        IReadOnlyList<string> optionKeys,
        DiscordMessageId? promptMessageId,
        string? toolName = null,
        string? displayText = null)
        : base(callId, requesterSenderId, requesterPrincipal, optionKeys, promptMessageId, toolName, displayText)
    {
    }
}
