// -----------------------------------------------------------------------
// <copyright file="ParentSessionApprovalBridge.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Bridges a sub-agent's approval requests to the parent session's interactive channel.
/// Wraps the session's <see cref="IApprovalChannel"/> and request emitter into the
/// cross-layer <see cref="IParentApprovalBridge"/> contract.
/// </summary>
internal sealed class ParentSessionApprovalBridge : IParentApprovalBridge
{
    private readonly IApprovalChannel _channel;
    private readonly Action<ToolInteractionRequestDispatch> _emitRequest;
    private readonly SessionId _sessionId;
    private readonly string _approvalScopeId;
    private readonly SenderId? _requesterSenderId;
    private readonly PrincipalClassification? _requesterPrincipal;
    private readonly bool _hasAdoptedContext;
    private readonly bool _hasThirdPartyAdoptedContext;
    private readonly IReadOnlyList<string> _adoptedSpeakerIds;
    private int _nextApprovalRequestId;

    public ParentSessionApprovalBridge(
        IApprovalChannel channel,
        Action<ToolInteractionRequestDispatch> emitRequest,
        SessionId sessionId,
        string approvalScopeId,
        SenderId? requesterSenderId,
        PrincipalClassification? requesterPrincipal,
        bool hasAdoptedContext,
        bool hasThirdPartyAdoptedContext,
        IReadOnlyList<string> adoptedSpeakerIds)
    {
        _channel = channel;
        _emitRequest = emitRequest;
        _sessionId = sessionId;
        _approvalScopeId = approvalScopeId;
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
        EnsureAuthorityContext();

        var parentCallId = CreateParentCallId();
        var waitTask = _channel.WaitForApprovalAsync(parentCallId, Timeout.InfiniteTimeSpan, ct);

        // Emit verbatim from the gate's computed options so persistent-grant
        // buttons (Always here / Always anywhere) and the messy-command
        // four-button fallback stay in lock-step with the parent path. The
        // earlier hardcoded list silently dropped "Always anywhere" for
        // sub-agents.
        _emitRequest(new ToolInteractionRequestDispatch(new ToolInteractionRequest
        {
            SessionId = _sessionId,
            Kind = "approval",
            CallId = parentCallId,
            ToolName = new Netclaw.Tools.ToolName(toolName),
            DisplayText = displayText,
            RequesterSenderId = _requesterSenderId,
            RequesterPrincipal = _requesterPrincipal,
            Patterns = patterns,
            CandidateVerbs = candidateVerbs,
            Candidates = candidates.Select(c => new ApprovalCandidate(c.Verb, c.Directory)
            {
                Shell = c.Shell,
                VerbTokens = c.VerbTokens,
            }).ToList(),
            Cwd = cwd,
            IsMessy = isMessy,
            HasAdoptedContext = _hasAdoptedContext,
            HasThirdPartyAdoptedContext = _hasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = _adoptedSpeakerIds,
            PersistedAdoptedContext = _hasAdoptedContext,
            Options = options
                .Select(o => new ToolInteractionOption(new ApprovalOptionKey(o.Key), o.Label))
                .ToList()
        }, PersistApprovalState: false));

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

    private ToolCallId CreateParentCallId()
    {
        // This bridge is created by the session actor but used by thread-pool
        // tool tasks. Multiple child tool calls can request approval at once,
        // so the sequence allocation is not actor-mailbox confined.
        var requestId = Interlocked.Increment(ref _nextApprovalRequestId);

        // Child call ids are only unique inside the sub-agent's tool loop. The
        // parent approval channel is session-wide, so include the spawning tool
        // call scope plus a per-bridge sequence. Keep this short: approval
        // button payloads are capped by the most restrictive channel adapter.
        return new ToolCallId($"{_approvalScopeId}/subagent-approval/{requestId}");
    }

    private void EnsureAuthorityContext()
    {
        // Approval responses are authorized against the parent requester. If we
        // cannot reconstruct that authority context, emitting a prompt would let
        // the channel decide without a safe requester binding.
        if (_requesterPrincipal is null)
            throw new ParentApprovalUnavailableException(
                "Sub-agent approval requires parent requester principal context.");

        if (_requesterPrincipal is not PrincipalClassification.VerifiedAutomation
            && _requesterSenderId is null)
        {
            throw new ParentApprovalUnavailableException(
                "Sub-agent approval requires parent requester sender context.");
        }
    }
}
