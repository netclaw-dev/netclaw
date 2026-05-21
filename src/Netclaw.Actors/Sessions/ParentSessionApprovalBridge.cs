// -----------------------------------------------------------------------
// <copyright file="ParentSessionApprovalBridge.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
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
    private readonly SenderId? _requesterSenderId;
    private readonly PrincipalClassification? _requesterPrincipal;
    private readonly bool _hasAdoptedContext;
    private readonly bool _hasThirdPartyAdoptedContext;
    private readonly IReadOnlyList<string> _adoptedSpeakerIds;

    public ParentSessionApprovalBridge(
        IApprovalChannel channel,
        Action<ToolInteractionRequest> emitRequest,
        SessionId sessionId,
        SenderId? requesterSenderId,
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
        IReadOnlyList<string> candidateVerbs,
        IReadOnlyList<ParentApprovalCandidate> candidates,
        string? cwd,
        IReadOnlyList<ParentApprovalOption> options,
        bool isMessy,
        CancellationToken ct)
    {
        var waitTask = _channel.WaitForApprovalAsync(callId, Timeout.InfiniteTimeSpan, ct);

        // Emit verbatim from the gate's computed options so persistent-grant
        // buttons (Always here / Always anywhere) and the messy-command
        // four-button fallback stay in lock-step with the parent path. The
        // earlier hardcoded list silently dropped "Always anywhere" for
        // sub-agents.
        _emitRequest(new ToolInteractionRequest
        {
            SessionId = _sessionId,
            Kind = "approval",
            CallId = callId,
            ToolName = new Netclaw.Tools.ToolName(toolName),
            DisplayText = displayText,
            RequesterSenderId = _requesterSenderId,
            RequesterPrincipal = _requesterPrincipal,
            Patterns = patterns,
            CandidateVerbs = candidateVerbs,
            Candidates = candidates.Select(c => new ApprovalCandidate(c.Verb, c.Directory)).ToList(),
            Cwd = cwd,
            IsMessy = isMessy,
            HasAdoptedContext = _hasAdoptedContext,
            HasThirdPartyAdoptedContext = _hasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = _adoptedSpeakerIds,
            PersistedAdoptedContext = _hasAdoptedContext,
            Options = options
                .Select(o => new ToolInteractionOption(new ApprovalOptionKey(o.Key), o.Label))
                .ToList()
        });

        var decision = await waitTask;

        return decision switch
        {
            ApprovalDecision.ApprovedOnce => ParentApprovalDecision.ApprovedOnce,
            ApprovalDecision.ApprovedSession => ParentApprovalDecision.ApprovedSession,
            ApprovalDecision.ApprovedAlways => ParentApprovalDecision.ApprovedAlways,
            ApprovalDecision.ApprovedEverywhere => ParentApprovalDecision.ApprovedEverywhere,
            ApprovalDecision.TimedOut => ParentApprovalDecision.TimedOut,
            _ => ParentApprovalDecision.Denied
        };
    }
}
