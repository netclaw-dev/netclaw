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

internal sealed class PendingApprovalRequest
{
    public PendingApprovalRequest(ToolInteractionRequest request)
    {
        Request = request;
        CallId = request.CallId;
        RequesterSenderId = request.RequesterSenderId is { } requesterSenderId
            ? requesterSenderId.Value
            : null;
        RequesterPrincipal = request.RequesterPrincipal;
        Options = request.Options;
        OptionKeys = request.Options.Select(option => option.Key.Value).ToArray();
    }

    public PendingApprovalRequest(
        ToolCallId callId,
        string? requesterSenderId,
        PrincipalClassification? requesterPrincipal,
        IReadOnlyList<string> optionKeys,
        MattermostPostId? promptPostId)
    {
        Request = null;
        CallId = callId;
        RequesterSenderId = requesterSenderId;
        RequesterPrincipal = requesterPrincipal;
        OptionKeys = [.. optionKeys];
        Options = OptionKeys
            .Select(key => new ToolInteractionOption(new ApprovalOptionKey(key), ApprovalOptionKeys.LabelFor(key)))
            .ToArray();
        PromptPostId = promptPostId;
    }

    public ToolInteractionRequest? Request { get; }
    public ToolCallId CallId { get; }

    public string? RequesterSenderId { get; }

    public PrincipalClassification? RequesterPrincipal { get; }
    public IReadOnlyList<ToolInteractionOption> Options { get; }
    public IReadOnlyList<string> OptionKeys { get; }
    public MattermostPostId? PromptPostId { get; set; }
}
