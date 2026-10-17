// -----------------------------------------------------------------------
// <copyright file="SessionToolWorkflow.Approvals.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

internal enum ApprovalNoticeKind
{
    Expired,
    WrongRequester,
    OptionUnavailable
}

internal abstract record ApprovalResponseResult
{
    private ApprovalResponseResult() { }

    internal sealed record Rejected(string NackReason, ApprovalNoticeKind Notice, string Diagnostic)
        : ApprovalResponseResult;

    internal abstract record Accepted(
        PendingToolInteraction Pending,
        ConsentAnswer Answer) : ApprovalResponseResult
    {
        public ToolCallId CallId => new(Pending.Request.CallId);
        public ToolName ToolName => new(Pending.Request.ToolName);
        public TrustAudience Audience => Pending.TurnContext?.Audience ?? Pending.Request.Audience;
    }

    internal abstract record LiveOnlyAccepted(
        PendingToolInteraction Pending,
        ConsentAnswer Answer) : Accepted(Pending, Answer);

    internal abstract record DurableAccepted(
        PendingToolInteraction Pending,
        ConsentAnswer Answer) : Accepted(Pending, Answer);
}

internal abstract record RecoveredContinuation
{
    private RecoveredContinuation() { }

    internal sealed record WaitForSiblings : RecoveredContinuation;
    internal sealed record Expired(string Diagnostic) : RecoveredContinuation;
    internal sealed record Abandon(ToolBatchAbandoned Event, string Diagnostic) : RecoveredContinuation;
    internal sealed record Dispatch(
        List<FunctionCallContent> Calls,
        TurnContext TurnContext,
        ApprovalRedrivePlan ApprovalPlan) : RecoveredContinuation;
}

internal sealed partial class SessionToolWorkflow
{
    private sealed record ValidatedAccepted(
        PendingToolInteraction Pending,
        ConsentAnswer Answer) : ApprovalResponseResult.Accepted(Pending, Answer);

    private sealed record ClaimedLiveOnlyAccepted(
        PendingToolInteraction Pending,
        ConsentAnswer Answer,
        ClaimedApprovalWait Claim) : ApprovalResponseResult.LiveOnlyAccepted(Pending, Answer);

    private sealed record ClaimedDurableAccepted(
        PendingToolInteraction Pending,
        ConsentAnswer Answer,
        ClaimedApprovalWait Claim) : ApprovalResponseResult.DurableAccepted(Pending, Answer);

    private sealed record RecoveredDurableAccepted(
        PendingToolInteraction Pending,
        ConsentAnswer Answer) : ApprovalResponseResult.DurableAccepted(Pending, Answer);

    public ToolApprovalRequested CreateApprovalRequest(
        ToolInteractionRequestDispatch dispatch,
        SessionId sessionId,
        TurnContext? currentTurn,
        MessageSource? currentSource,
        TrustAudience fallbackAudience,
        long nowMs)
    {
        var request = dispatch.Request;
        currentTurn = dispatch.ChildContext ?? currentTurn;
        return new ToolApprovalRequested
        {
            SessionId = sessionId,
            SourceChildRunId = dispatch.SourceChildRunId,
            OriginalChildCallId = dispatch.OriginalChildCallId,
            CallId = request.CallId.Value,
            AuthorizationAttemptId = request.AuthorizationAttemptId,
            ToolName = request.ToolName.Value,
            Patterns = request.Patterns,
            CandidateVerbs = request.CandidateVerbs,
            Audience = currentTurn?.Audience ?? fallbackAudience,
            Boundary = currentTurn?.Boundary ?? currentSource?.Boundary,
            ChannelType = currentTurn?.ChannelType?.ToWireValue() ?? currentSource?.ChannelType.ToWireValue(),
            SupportsInteractiveApproval = currentTurn?.SupportsInteractiveApproval
                                          ?? currentSource?.ChannelType.SupportsInteractiveApproval(),
            RequesterSenderId = request.RequesterSenderId,
            RequesterPrincipal = request.RequesterPrincipal,
            HasThirdPartyAdoptedContext = request.HasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = request.AdoptedSpeakerIds,
            Cwd = request.Cwd,
            RepositoryCommonDirectory = request.RepositoryCommonDirectory,
            OptionKeys = request.Options.Select(option => option.Key.Value).ToArray(),
            Candidates = request.Candidates,
            TurnContext = currentTurn?.ToRecord(),
            ManagedTemporaryDirectory = dispatch.ManagedTemporaryDirectory,
            RequestedAtMs = nowMs
        };
    }

    public PendingToolInteraction ApplyApprovalRequested(
        ToolApprovalRequested request,
        bool durable,
        bool recovered)
        => Request(request, durable, recovered);

    public bool TryResolveTextResponse(
        ToolInteractionTextResponse text,
        SessionState state,
        [NotNullWhen(true)] out ToolInteractionResponse? response,
        out string? nackReason)
    {
        response = null;
        nackReason = null;
        var pending = FindLatestPending(candidate => CanApprovePending(candidate, text.SenderId));
        if (pending is null)
        {
            nackReason = PendingParentCount > 0
                ? ApprovalNackReasons.WrongRequester
                : HasApprovalHistory(state)
                    ? ApprovalNackReasons.PromptExpired
                    : ApprovalNackReasons.NoHistory;
            return false;
        }

        var optionKeys = pending.Request.OptionKeys.Count > 0
            ? pending.Request.OptionKeys
            :
            [
                ApprovalOptionKeys.ApproveOnce,
                ApprovalOptionKeys.ApproveSession,
                ApprovalOptionKeys.ApproveAlways,
                ApprovalOptionKeys.Deny
            ];
        var options = optionKeys
            .Select(key => new ToolInteractionOption(
                new ApprovalOptionKey(key),
                ApprovalOptionKeys.LabelFor(key, new ToolName(pending.Request.ToolName).IsMcp)))
            .ToArray();
        if (!ToolInteractionResponseParser.TryParseApprovalResponse(text.Text, options, out var selectedKey)
            || selectedKey is null)
        {
            nackReason = ApprovalNackReasons.OptionUnavailable;
            return false;
        }

        response = new ToolInteractionResponse
        {
            SessionId = text.SessionId,
            CallId = new ToolCallId(pending.Request.CallId),
            SelectedKey = new ApprovalOptionKey(selectedKey),
            SenderId = text.SenderId
        };
        return true;
    }

    public ApprovalResponseResult PrepareLiveResponse(ToolInteractionResponse response)
    {
        if (!TryGetPending(response.CallId.Value, out var pending))
            return new ApprovalResponseResult.Rejected(
                ApprovalNackReasons.PromptExpired,
                ApprovalNoticeKind.Expired,
                "unknown_call");

        var validation = ValidateResponse(pending, response);
        if (validation is ApprovalResponseResult.Rejected rejected)
            return rejected;
        if (!_approvalChannel.TryClaim(response.CallId, out var claim))
            return new ApprovalResponseResult.Rejected(
                ApprovalNackReasons.PromptExpired,
                ApprovalNoticeKind.Expired,
                "no_live_wait");

        if (!pending.PersistApprovalState)
            RemovePending(response.CallId.Value);
        var validated = (ValidatedAccepted)validation;
        return pending.PersistApprovalState
            ? new ClaimedDurableAccepted(pending, validated.Answer, claim)
            : new ClaimedLiveOnlyAccepted(pending, validated.Answer, claim);
    }

    public ApprovalResponseResult PrepareRecoveredResponse(
        ToolInteractionResponse response,
        SessionState state)
    {
        if (!TryGetPending(response.CallId.Value, out var pending))
            return new ApprovalResponseResult.Rejected(
                ApprovalNackReasons.PromptExpired,
                ApprovalNoticeKind.Expired,
                ClassifyUnknownCall(response.CallId.Value, state.History));

        var validation = ValidateResponse(pending, response);
        if (validation is ApprovalResponseResult.Rejected rejected)
            return rejected;
        return new RecoveredDurableAccepted(pending, ((ValidatedAccepted)validation).Answer);
    }

    public string ClassifyUnknownCall(string callId, IReadOnlyList<SerializableChatMessage> history)
    {
        if (HasResolved(callId))
            return "already_resolved";
        if (ParkedToolBatchHistory.HasToolResult(history, callId))
            return "already_completed";
        if (ParkedToolBatchHistory.FindRedrivableAssistantMessage(history, callId) is not null)
            return "orphaned_assistant_tool_use";
        return "unknown";
    }

    public IReadOnlyList<ToolApprovalGrant> CreateApprovalGrants(
        ApprovalResponseResult.Accepted accepted,
        string sessionDirectory)
    {
        if (accepted.Answer is not ConsentAnswer.Grant grant)
            return [];
        var request = accepted.Pending.Request;
        return GrantBuilder.Build(
            request.Candidates,
            grant.Scope,
            request.Cwd,
            sessionDirectory,
            request.RepositoryCommonDirectory);
    }

    public ToolApprovalResolved CreateResolution(
        ApprovalResponseResult.DurableAccepted accepted,
        SessionId sessionId,
        long nowMs)
        => new()
        {
            SessionId = sessionId,
            CallId = accepted.CallId.Value,
            SourceChildRunId = accepted.Pending.Request.SourceChildRunId,
            AuthorizationAttemptId = accepted.Pending.AuthorizationAttemptId.Value,
            Decision = ConsentAnswerCodec.ToJournalText(accepted.Answer),
            ResolvedAtMs = nowMs
        };

    public void CompleteLiveResponse(ApprovalResponseResult.LiveOnlyAccepted accepted)
    {
        if (accepted is ClaimedLiveOnlyAccepted live)
            live.Claim.Complete(live.Answer);
    }

    public void FailLiveResponse(ApprovalResponseResult.Accepted accepted)
    {
        switch (accepted)
        {
            case ClaimedLiveOnlyAccepted live:
                live.Claim.Complete(ConsentAnswer.Denied);
                break;
            case ClaimedDurableAccepted durable:
                durable.Claim.Complete(ConsentAnswer.Denied);
                break;
        }
    }

    public void ApplyResolvedResponse(ToolApprovalResolved resolution)
        => Resolve(resolution.CallId, ConsentAnswerCodec.FromJournalText(resolution.Decision), out _);

    public void ApplyResolvedResponse(
        ToolApprovalResolved resolution,
        ApprovalResponseResult.DurableAccepted accepted)
    {
        ApplyResolvedResponse(resolution);
        if (accepted is ClaimedDurableAccepted live)
            live.Claim.Complete(live.Answer);
    }

    public bool HasApprovalHistory(SessionState state)
        => ResolvedParentCount > 0
           || ParkedToolBatchHistory.FindRedrivableAssistantMessage(state.History, null) is not null;

    private static ApprovalResponseResult ValidateResponse(
        PendingToolInteraction pending,
        ToolInteractionResponse response)
    {
        if (!TryGetApprovalAuthority(pending, out var principal, out var senderId, out var failure))
            return new ApprovalResponseResult.Rejected(
                ApprovalNackReasons.PromptExpired,
                ApprovalNoticeKind.Expired,
                failure ?? "authority_unavailable");
        if (!ApprovalButtonValueCodec.CanApprove(principal, senderId, response.SenderId.Value))
            return new ApprovalResponseResult.Rejected(
                ApprovalNackReasons.WrongRequester,
                ApprovalNoticeKind.WrongRequester,
                senderId ?? "wrong_requester");
        if (!ConsentAnswerCodec.TryParseOption(
                response.SelectedKey.Value,
                pending.Request.OptionKeys,
                pending.Request.RepositoryCommonDirectory,
                out var answer))
            return new ApprovalResponseResult.Rejected(
                ApprovalNackReasons.OptionUnavailable,
                ApprovalNoticeKind.OptionUnavailable,
                response.SelectedKey.Value);
        return new ValidatedAccepted(pending, answer);
    }

    private static bool CanApprovePending(PendingToolInteraction pending, SenderId senderId)
        => TryGetApprovalAuthority(pending, out var principal, out var requesterSenderId, out _)
           && ApprovalButtonValueCodec.CanApprove(principal, requesterSenderId, senderId.Value);

    private static bool TryGetApprovalAuthority(
        PendingToolInteraction pending,
        out PrincipalClassification? requesterPrincipal,
        out string? requesterSenderId,
        out string? failure)
    {
        if (pending.TurnContext is { } context)
        {
            requesterPrincipal = context.RequesterPrincipal;
            if (!context.HasApprovalRequester)
            {
                requesterSenderId = null;
                failure = "turn context has no requester sender for a non-automation principal";
                return false;
            }
            requesterSenderId = context.RequesterSenderId is { } sender ? sender.Value : null;
            failure = null;
            return true;
        }

        requesterPrincipal = pending.Request.RequesterPrincipal;
        requesterSenderId = pending.Request.RequesterSenderId?.Value;
        if (pending.TurnContextRestoreFailure is not null)
        {
            failure = pending.TurnContextRestoreFailure;
            return false;
        }
        if (requesterPrincipal is not PrincipalClassification.VerifiedAutomation
            && string.IsNullOrWhiteSpace(requesterSenderId))
        {
            failure = "legacy approval state has no requester sender for a non-automation principal";
            return false;
        }
        failure = null;
        return true;
    }
}
