// -----------------------------------------------------------------------
// <copyright file="ParentSessionApprovalBridge.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Adds call-local correlation to the immutable approval context produced by policy.
/// </summary>
internal sealed record ParentApprovalRequest(
    AuthorizationAttemptId AuthorizationAttemptId,
    ToolCallId CallId,
    ToolApprovalContext Approval);

/// <summary>
/// Internal extension used by Netclaw-owned bridges to preserve diagnostic
/// correlation without changing the public approval-bridge contract.
/// </summary>
internal interface IAuthorizationAttemptAwareParentApprovalBridge
{
    Task<ParentApprovalDecision> RequestApprovalAsync(
        ParentApprovalRequest request,
        CancellationToken ct);
}

/// <summary>
/// Bridges a sub-agent's approval requests to the parent session's interactive channel.
/// Wraps the session's <see cref="IApprovalChannel"/> and request emitter into the
/// cross-layer <see cref="IParentApprovalBridge"/> contract.
/// </summary>
internal sealed class ParentSessionApprovalBridge :
    IParentApprovalBridge,
    IAuthorizationAttemptAwareParentApprovalBridge
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

    public Task<ParentApprovalDecision> RequestApprovalAsync(
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
        => RequestApprovalCoreAsync(
            new ParentApprovalRequest(
                AuthorizationAttemptId.New(),
                callId,
                new ToolApprovalContext(
                    toolName,
                    displayText,
                    patterns,
                    candidateVerbs,
                    options.Select(static option => new ToolApprovalOption(
                        new ApprovalOptionKey(option.Key),
                        option.Label)).ToList(),
                    Cwd: cwd,
                    IsMessy: isMessy,
                    Candidates: candidates.Select(static candidate => new ApprovalCandidate(
                        candidate.Verb,
                        candidate.Directory)
                    {
                        Shell = candidate.Shell,
                        VerbTokens = candidate.VerbTokens,
                    }).ToList())),
            ct);

    Task<ParentApprovalDecision> IAuthorizationAttemptAwareParentApprovalBridge.RequestApprovalAsync(
        ParentApprovalRequest request,
        CancellationToken ct)
        => RequestApprovalCoreAsync(request, ct);

    private async Task<ParentApprovalDecision> RequestApprovalCoreAsync(
        ParentApprovalRequest request,
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
            AuthorizationAttemptId = request.AuthorizationAttemptId.Value,
            ToolName = new Netclaw.Tools.ToolName(request.Approval.ToolName),
            DisplayText = request.Approval.DisplayText,
            RequesterSenderId = _requesterSenderId,
            RequesterPrincipal = _requesterPrincipal,
            Patterns = request.Approval.Patterns,
            CandidateVerbs = request.Approval.CandidateVerbs,
            Candidates = (request.Approval.Candidates ?? [])
                .Select(static candidate => new ApprovalCandidate(candidate.Verb, candidate.Directory)
                {
                    Shell = candidate.Shell,
                    VerbTokens = candidate.VerbTokens,
                }).ToList(),
            Cwd = request.Approval.Cwd,
            IsMessy = request.Approval.IsMessy,
            HasAdoptedContext = _hasAdoptedContext,
            HasThirdPartyAdoptedContext = _hasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = _adoptedSpeakerIds,
            PersistedAdoptedContext = _hasAdoptedContext,
            Options = request.Approval.Options
                .Select(static option => new ToolInteractionOption(option.Key, option.Label))
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
