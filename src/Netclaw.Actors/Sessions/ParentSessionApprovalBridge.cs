// -----------------------------------------------------------------------
// <copyright file="ParentSessionApprovalBridge.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Netclaw.Tools.Authorization.Consent;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Adds call-local correlation to the immutable approval context produced by policy.
/// </summary>
/// <param name="CallName">
/// The tool name as the call states it. The one-time consent binds this name,
/// because the gate matches the consent against the call.
/// </param>
internal sealed record ParentApprovalRequest(
    AuthorizationAttemptId AuthorizationAttemptId,
    ToolCallId CallId,
    string CallName,
    ToolApprovalContext Approval);

/// <summary>
/// The result of one consent request: the operator's answer and, when the
/// operator approved, the one-time consent for the exact retry.
/// </summary>
/// <remarks>
/// Every approval answer seeds the one-time consent, also a session or
/// persistent grant. A stored grant can miss a candidate (for example a piped
/// verb without a path), and a sub-agent's grant scope differs from the
/// session's. The consent is bound to the exact prompted candidates and lasts
/// for one retry (#1802).
/// </remarks>
internal sealed record ConsentStep(ConsentAnswer Answer, OneTimeConsent? RetryConsent)
{
    internal static ConsentStep From(ConsentAnswer answer, string callName, ToolApprovalContext request)
        => new(answer, answer is ConsentAnswer.Refused ? null : OneTimeApprovalKeys.CreateConsent(callName, request));
}

/// <summary>
/// The request contract of a parent consent bridge. A sub-agent sends the
/// immutable approval context and receives the consent step. The parent
/// session and the sub-agent use the same <see cref="ParentSessionApprovalBridge"/>.
/// </summary>
internal interface IParentConsentBridge : IParentApprovalBridge
{
    Task<ConsentStep> RequestConsentAsync(
        ParentApprovalRequest request,
        CancellationToken ct);
}

/// <summary>
/// The one consent loop of an interactive session. The session's own tool
/// calls and the tool calls of its sub-agents ask the operator here: one prompt
/// shape, one wait with the session's approval timeout, and one decision from
/// the answer. A sub-agent receives this object as its parent bridge.
/// </summary>
internal sealed class ParentSessionApprovalBridge : IParentConsentBridge
{
    private readonly IApprovalChannel _channel;
    private readonly Action<ToolInteractionRequestDispatch> _emitRequest;
    private readonly ToolExecutionTimeout _timeout;
    private readonly string _approvalScopeId;
    private readonly ApprovalPromptBuilder _prompts;
    private int _nextApprovalRequestId;

    public ParentSessionApprovalBridge(
        IApprovalChannel channel,
        Action<ToolInteractionRequestDispatch> emitRequest,
        ToolExecutionTimeout timeout,
        SessionId sessionId,
        string approvalScopeId,
        SenderId? requesterSenderId,
        PrincipalClassification? requesterPrincipal,
        bool hasAdoptedContext,
        bool hasThirdPartyAdoptedContext,
        IReadOnlyList<string> adoptedSpeakerIds)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(emitRequest);
        ArgumentNullException.ThrowIfNull(timeout);
        _channel = channel;
        _emitRequest = emitRequest;
        _timeout = timeout;
        _approvalScopeId = approvalScopeId;
        _prompts = new ApprovalPromptBuilder(
            sessionId,
            requesterSenderId,
            requesterPrincipal,
            hasAdoptedContext,
            hasThirdPartyAdoptedContext,
            adoptedSpeakerIds);
    }

    /// <summary>
    /// Asks for consent for a call of the session itself. The prompt uses the
    /// call id, and the session journals the wait, so an answer can resume the
    /// call after passivation.
    /// </summary>
    public Task<ConsentStep> RequestSessionConsentAsync(
        ParentApprovalRequest request,
        CancellationToken ct)
        => PromptAsync(request.CallId, request, persistApprovalState: true, ct);

    /// <summary>
    /// Asks for consent for a call of a sub-agent. The prompt gets a call id in
    /// the spawning call's scope. The session does not journal the wait,
    /// because a sub-agent cannot resume after passivation.
    /// </summary>
    public Task<ConsentStep> RequestConsentAsync(
        ParentApprovalRequest request,
        CancellationToken ct)
    {
        EnsureAuthorityContext();
        return PromptAsync(CreateParentCallId(), request, persistApprovalState: false, ct);
    }

    private async Task<ConsentStep> PromptAsync(
        ToolCallId promptCallId,
        ParentApprovalRequest request,
        bool persistApprovalState,
        CancellationToken ct)
    {
        var waitTask = _channel.WaitForApprovalAsync(promptCallId, _timeout.Value, ct);
        _emitRequest(_prompts.Build(promptCallId, request, persistApprovalState));
        return ConsentStep.From(await waitTask, request.CallName, request.Approval);
    }

    private ToolCallId CreateParentCallId()
    {
        // This prompt is created by the session actor but used by thread-pool
        // tool tasks. Multiple child tool calls can request approval at once,
        // so the sequence allocation is not actor-mailbox confined.
        var requestId = Interlocked.Increment(ref _nextApprovalRequestId);

        // Child call ids are only unique inside the sub-agent's tool loop. The
        // parent approval channel is session-wide, so include the spawning tool
        // call scope plus a per-prompt sequence. Keep this short: approval
        // button payloads are capped by the most restrictive channel adapter.
        return ChildApprovalPrompt.CreateCallId(_approvalScopeId, requestId);
    }

    private void EnsureAuthorityContext()
    {
        // Approval responses are authorized against the parent requester. If we
        // cannot reconstruct that authority context, emitting a prompt would let
        // the channel decide without a safe requester binding.
        if (_prompts.RequesterPrincipal is null)
            throw new ParentApprovalUnavailableException(
                "Sub-agent approval requires parent requester principal context.");

        if (_prompts.RequesterPrincipal is not PrincipalClassification.VerifiedAutomation
            && _prompts.RequesterSenderId is null)
        {
            throw new ParentApprovalUnavailableException(
                "Sub-agent approval requires parent requester sender context.");
        }
    }
}

/// <summary>
/// Makes the approval prompt of one session. The prompt of a session call and the prompt of a child call have one
/// shape (durable-child-consent-wait, design D2). The builder copies the gate facts of the request to the prompt
/// without change, and it binds the prompt to the requester of the session turn.
/// </summary>
internal sealed class ApprovalPromptBuilder
{
    private readonly SessionId _sessionId;
    private readonly bool _hasAdoptedContext;
    private readonly bool _hasThirdPartyAdoptedContext;
    private readonly IReadOnlyList<string> _adoptedSpeakerIds;

    public ApprovalPromptBuilder(
        SessionId sessionId,
        SenderId? requesterSenderId,
        PrincipalClassification? requesterPrincipal,
        bool hasAdoptedContext,
        bool hasThirdPartyAdoptedContext,
        IReadOnlyList<string> adoptedSpeakerIds)
    {
        _sessionId = sessionId;
        RequesterSenderId = requesterSenderId;
        RequesterPrincipal = requesterPrincipal;
        _hasAdoptedContext = hasAdoptedContext;
        _hasThirdPartyAdoptedContext = hasThirdPartyAdoptedContext;
        _adoptedSpeakerIds = adoptedSpeakerIds;
    }

    /// <summary>The sender that can answer the prompt.</summary>
    public SenderId? RequesterSenderId { get; }

    /// <summary>The principal of the sender that can answer the prompt.</summary>
    public PrincipalClassification? RequesterPrincipal { get; }

    /// <summary>
    /// Makes the prompt for <paramref name="request"/> with the call ID <paramref name="promptCallId"/>. A journaled
    /// prompt (<paramref name="persistApprovalState"/>) is a prompt for a session call. Any other prompt is for a child
    /// call, and it keeps the call ID of the child.
    /// </summary>
    public ToolInteractionRequestDispatch Build(
        ToolCallId promptCallId,
        ParentApprovalRequest request,
        bool persistApprovalState)
    {
        var approval = request.Approval;

        // Emit verbatim from the gate's computed options, so that the
        // persistent-grant buttons stay the same for the session and its
        // sub-agents.
        return new ToolInteractionRequestDispatch(new ToolInteractionRequest
        {
            SessionId = _sessionId,
            Kind = "approval",
            CallId = promptCallId,
            AuthorizationAttemptId = request.AuthorizationAttemptId.Value,
            ToolName = new ToolName(approval.ToolName),
            DisplayText = approval.DisplayText,
            RequesterSenderId = RequesterSenderId,
            RequesterPrincipal = RequesterPrincipal,
            HasAdoptedContext = _hasAdoptedContext,
            HasThirdPartyAdoptedContext = _hasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = _adoptedSpeakerIds,
            PersistedAdoptedContext = _hasAdoptedContext,
            Patterns = approval.Patterns,
            CandidateVerbs = approval.CandidateVerbs,
            Candidates = approval.Candidates ?? [],
            Cwd = approval.Cwd,
            RepositoryCommonDirectory = approval.RepositoryCommonDirectory,
            IsMessy = approval.IsMessy,
            Options = approval.Options
                .Where(option => !ApprovalOptionKeys.IsRepository(option.Key.Value)
                                 || approval.RepositoryCommonDirectory is not null)
                .Select(static option => new ToolInteractionOption(option.Key, option.Label))
                .ToList()
        }, persistApprovalState)
        {
            // Only a journaled wait can restore the managed temporary retry after passivation.
            OriginalChildCallId = persistApprovalState ? null : request.CallId,
            ManagedTemporaryDirectory = persistApprovalState && approval.IsManagedTemporaryRetry
                ? approval.ManagedTemporaryDirectory
                : null
        };
    }
}
