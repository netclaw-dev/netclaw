// -----------------------------------------------------------------------
// <copyright file="ParentSessionApprovalBridge.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Bridges a sub-agent's approval requests to the parent session's interactive channel.
/// Wraps the session's <see cref="IApprovalChannel"/> and request emitter into the
/// cross-layer <see cref="IParentApprovalBridge"/> contract.
/// </summary>
internal sealed class ParentSessionApprovalBridge : IParentApprovalBridge
{
    private readonly IApprovalChannel _channel;
    private readonly Action<ToolInteractionRequest> _emitRequest;
    private readonly SessionId _sessionId;
    private readonly string? _requesterSenderId;
    private readonly PrincipalClassification? _requesterPrincipal;
    private readonly bool _hasAdoptedContext;
    private readonly bool _hasThirdPartyAdoptedContext;
    private readonly IReadOnlyList<string> _adoptedSpeakerIds;

    public ParentSessionApprovalBridge(
        IApprovalChannel channel,
        Action<ToolInteractionRequest> emitRequest,
        SessionId sessionId,
        string? requesterSenderId,
        PrincipalClassification? requesterPrincipal,
        bool hasAdoptedContext,
        bool hasThirdPartyAdoptedContext,
        IReadOnlyList<string> adoptedSpeakerIds)
    {
        _channel = channel;
        _emitRequest = emitRequest;
        _sessionId = sessionId;
        _requesterSenderId = requesterSenderId;
        _requesterPrincipal = requesterPrincipal;
        _hasAdoptedContext = hasAdoptedContext;
        _hasThirdPartyAdoptedContext = hasThirdPartyAdoptedContext;
        _adoptedSpeakerIds = adoptedSpeakerIds;
    }

    public async Task<ParentApprovalDecision> RequestApprovalAsync(
        ToolCallId callId,
        string toolName,
        string displayText,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> approvalEntries,
        IReadOnlyList<string> directoryRoots,
        CancellationToken ct)
    {
        var waitTask = _channel.WaitForApprovalAsync(callId, Timeout.InfiniteTimeSpan, ct);

        // Labels are the fixed `ApprovalOptionKeys` constants — see that type
        // for `MaxLabelLength` and the channel-cap rationale.
        _emitRequest(new ToolInteractionRequest
        {
            SessionId = _sessionId,
            Kind = "approval",
            CallId = callId.Value,
            ToolName = toolName,
            DisplayText = displayText,
            RequesterSenderId = _requesterSenderId,
            RequesterPrincipal = _requesterPrincipal,
            Patterns = patterns,
            ApprovalEntries = approvalEntries,
            DirectoryRoots = directoryRoots,
            HasAdoptedContext = _hasAdoptedContext,
            HasThirdPartyAdoptedContext = _hasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = _adoptedSpeakerIds,
            PersistedAdoptedContext = _hasAdoptedContext,
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSession, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlways, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
            ]
        });

        var decision = await waitTask;

        return decision switch
        {
            ApprovalDecision.ApprovedOnce => ParentApprovalDecision.ApprovedOnce,
            ApprovalDecision.ApprovedSession => ParentApprovalDecision.ApprovedSession,
            ApprovalDecision.ApprovedAlways => ParentApprovalDecision.ApprovedAlways,
            ApprovalDecision.TimedOut => ParentApprovalDecision.TimedOut,
            _ => ParentApprovalDecision.Denied
        };
    }
}
