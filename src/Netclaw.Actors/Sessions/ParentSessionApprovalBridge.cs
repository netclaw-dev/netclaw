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
/// The consent loop of the session's own tool calls: one prompt shape, one wait with the session's approval timeout,
/// and one decision from the answer. A child does not wait here: it parks, and the owner asks the run requester
/// (durable-child-consent-wait, design D1).
/// </summary>
internal sealed class ParentSessionApprovalBridge : IParentApprovalBridge
{
    private readonly IApprovalChannel _channel;
    private readonly Action<ToolInteractionRequestDispatch> _emitRequest;
    private readonly ToolExecutionTimeout _timeout;
    private readonly ApprovalRequestBuilder _requests;

    public ParentSessionApprovalBridge(
        IApprovalChannel channel,
        Action<ToolInteractionRequestDispatch> emitRequest,
        ToolExecutionTimeout timeout,
        SessionId sessionId,
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
        _requests = new ApprovalRequestBuilder(
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
        => PromptAsync(_requests.ForSessionCall(request), request, ct);

    private async Task<ConsentStep> PromptAsync(
        ToolInteractionRequestDispatch dispatch,
        ParentApprovalRequest request,
        CancellationToken ct)
    {
        var waitTask = _channel.WaitForApprovalAsync(dispatch.Request.CallId, _timeout.Value, ct);
        _emitRequest(dispatch);
        return ConsentStep.From(await waitTask, request.CallName, request.Approval);
    }
}

/// <summary>
/// Makes the approval request of one session. The request of a session call and the request of a child call have one
/// shape (durable-child-consent-wait, design D2). The builder copies the gate facts of the call to the request without
/// change, and it binds the request to the requester of the session turn.
/// </summary>
internal sealed class ApprovalRequestBuilder
{
    private readonly SessionId _sessionId;
    private readonly SenderId? _requesterSenderId;
    private readonly PrincipalClassification? _requesterPrincipal;
    private readonly bool _hasAdoptedContext;
    private readonly bool _hasThirdPartyAdoptedContext;
    private readonly IReadOnlyList<string> _adoptedSpeakerIds;

    public ApprovalRequestBuilder(
        SessionId sessionId,
        SenderId? requesterSenderId,
        PrincipalClassification? requesterPrincipal,
        bool hasAdoptedContext,
        bool hasThirdPartyAdoptedContext,
        IReadOnlyList<string> adoptedSpeakerIds)
    {
        _sessionId = sessionId;
        _requesterSenderId = requesterSenderId;
        _requesterPrincipal = requesterPrincipal;
        _hasAdoptedContext = hasAdoptedContext;
        _hasThirdPartyAdoptedContext = hasThirdPartyAdoptedContext;
        _adoptedSpeakerIds = adoptedSpeakerIds;
    }

    /// <summary>
    /// Makes the request for a call of the session itself. The request uses the call ID of the call, and the session
    /// journals the wait.
    /// </summary>
    public ToolInteractionRequestDispatch ForSessionCall(ParentApprovalRequest request)
        => Build(request.CallId, request) with
        {
            ManagedTemporaryDirectory = request.Approval.IsManagedTemporaryRetry
                ? request.Approval.ManagedTemporaryDirectory
                : null
        };

    /// <summary>
    /// Makes the request for a parked call of a child run, with the call ID <paramref name="promptCallId"/>. The
    /// request keeps the call ID of the child. The owner of the run journals the request.
    /// </summary>
    /// <exception cref="ParentApprovalUnavailableException">
    /// The builder has no requester that can answer the request.
    /// </exception>
    public ToolInteractionRequestDispatch ForChildCall(ToolCallId promptCallId, ParentApprovalRequest request)
    {
        EnsureAuthorityContext();
        // The retry target of a parked child call is deferred, so a child prompt carries no managed temporary
        // directory.
        return Build(promptCallId, request) with { OriginalChildCallId = request.CallId };
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

    private ToolInteractionRequestDispatch Build(
        ToolCallId promptCallId,
        ParentApprovalRequest request)
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
            Notes = approval.Notes,
            RequesterSenderId = _requesterSenderId,
            RequesterPrincipal = _requesterPrincipal,
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
        });
    }
}
