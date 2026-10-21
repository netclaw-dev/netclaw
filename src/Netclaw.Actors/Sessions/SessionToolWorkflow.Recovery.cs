// -----------------------------------------------------------------------
// <copyright file="SessionToolWorkflow.Recovery.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Configuration;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

// The decision for a new input while the session is Ready. An open approval can remain from a cold recovery.
internal abstract record NewInputPlan
{
    private NewInputPlan() { }

    internal sealed record Activate : NewInputPlan;
    internal sealed record AbandonThenActivate(ToolBatchAbandoned Event) : NewInputPlan;
    // The input waits in the buffer. The turn that closes the approval, or the close itself, starts its turn.
    internal sealed record WaitForApproval : NewInputPlan;
}

internal sealed partial class SessionToolWorkflow
{
    public RecoveredContinuation PlanRecoveredContinuation(
        SessionState state,
        string callId,
        SessionId sessionId,
        long nowMs)
    {
        var assistant = ParkedToolBatchHistory.FindRedrivableAssistantMessage(state.History, callId);
        if (assistant is null)
            return new RecoveredContinuation.Expired("no unanswered assistant tool batch in history");

        if (assistant.ToolCalls.Any(call => HasPending(call.CallId.Value)))
            return new RecoveredContinuation.WaitForSiblings();

        if (!TryGetResolved(callId, out var resolved))
            return new RecoveredContinuation.Expired("approval decision was not recoverable");

        var aiMessage = ChatMessageConverter.ToAiMessage(assistant, reinjectMeta: true);
        var calls = aiMessage.Contents
            .OfType<FunctionCallContent>()
            .Where(call => !ParkedToolBatchHistory.HasToolResult(state.History, call.CallId)
                && (call.CallId == callId || !HasPending(call.CallId)))
            .ToList();
        if (calls.Count == 0)
            return UnsafeReplay("assistant message has no tool calls");
        if (resolved.Pending.TurnContext is not { } context)
            return UnsafeReplay("turn context is not recoverable: "
                + (resolved.Pending.TurnContextRestoreFailure ?? "missing turn context"));
        if (!MarkRedriving(resolved.Pending))
            return UnsafeReplay($"approval turn phase is {TurnPhase}");

        var plan = BuildRedrivePlan(assistant.ToolCalls.Select(call => call.CallId.Value));
        return new RecoveredContinuation.Dispatch(calls, context, plan);

        RecoveredContinuation.Abandon UnsafeReplay(string diagnostic)
            => new(
                CreateAbandonment(
                    state,
                    sessionId,
                    ParkedToolBatchHistory.AbandonedResultPrefix + "approval state could not be recovered safely.",
                    nowMs),
                diagnostic);
    }

    public ToolBatchAbandoned CreateAbandonment(
        SessionState state,
        SessionId sessionId,
        string resultContent,
        long nowMs)
    {
        var assistant = ParkedToolBatchHistory.FindRedrivableAssistantMessage(state.History, null);
        var results = assistant is null
            ? (IReadOnlyList<SerializableChatMessage>)[]
            : ParkedToolBatchHistory.BuildSyntheticAbandonResults(state.History, assistant, resultContent);
        return new ToolBatchAbandoned
        {
            SessionId = sessionId,
            ToolResults = results,
            AbandonedAtMs = nowMs
        };
    }

    public SessionState ApplyAbandonment(SessionState state, ToolBatchAbandoned abandoned)
    {
        foreach (var result in abandoned.ToolResults)
            state = ApplyRecordedResult(state, result);
        ClearCalls();
        ClearApprovalTurn();
        ClearCompletedBatch();
        return state;
    }

    public bool ShouldAbandonResolvedAfterRecovery()
        => ResolvedParentCount > 0 && PendingParentCount == 0;

    public bool ShouldAbandonResolvedForIdle() => ResolvedParentCount > 0;

    // Security boundary: only a person can abandon an open approval. Automation (a reminder fire, a webhook, a
    // background job completion, a child result notice) cannot answer a prompt, so it must not cancel one. Its input
    // waits until the approval closes, and the approval stays open. Any person may still abandon the approval, as
    // before; this check does not compare the person with the requester of the parked call.
    // A notice carries the principal of the person who owns the child, so the kind of the input decides and not the
    // principal. A resolved or interrupted batch has no open approval, so any input closes it before its own turn.
    public NewInputPlan PlanNewInputAbandonment(
        SessionState state,
        SessionId sessionId,
        PrincipalClassification? inputPrincipal,
        bool isChildNotice,
        long nowMs)
    {
        if (PendingParentCount > 0)
        {
            if (isChildNotice || IsAutomation(inputPrincipal))
                return new NewInputPlan.WaitForApproval();

            MarkAbandoning();
            return new NewInputPlan.AbandonThenActivate(CreateAbandonment(
                state,
                sessionId,
                ParkedToolBatchHistory.AbandonedResultPrefix + "the request was "
                + "superseded by a new message before approval was given.",
                nowMs));
        }

        // <child-consent-hold>
        // The child consent pause holds automated input until the last child request closes. A person input runs.
        // An approval notice never comes here: the owner admits it on its own path.
        if (IsChildConsentPaused && (isChildNotice || IsAutomation(inputPrincipal)))
            return new NewInputPlan.WaitForApproval();
        // </child-consent-hold>

        if (ResolvedParentCount > 0)
            return new NewInputPlan.AbandonThenActivate(CreateAbandonment(
                state,
                sessionId,
                ParkedToolBatchHistory.AbandonedResultPrefix + "the session restarted after approval before the action completed.",
                nowMs));

        return ShouldAbandonInterruptedAfterRecovery(state)
            ? new NewInputPlan.AbandonThenActivate(CreateAbandonment(
                state,
                sessionId,
                ParkedToolBatchHistory.AbandonedResultPrefix + "the session restarted before the action completed.",
                nowMs))
            : new NewInputPlan.Activate();
    }

    // An input with no source has the default principal of TurnContext.FromMessageSource (UntrustedExternal).
    internal static bool IsAutomation(PrincipalClassification? principal)
        => principal is PrincipalClassification.VerifiedAutomation or PrincipalClassification.SystemProcess;

    public ToolBatchAbandoned? PlanFailedRunAbandonment(
        SessionState state,
        SessionId sessionId,
        long nowMs)
        => ParkedToolBatchHistory.FindRedrivableAssistantMessage(state.History, null) is null
            ? null
            : CreateAbandonment(
                state,
                sessionId,
                ParkedToolBatchHistory.AbandonedResultPrefix + "the tool run failed.",
                nowMs);

    public bool ShouldAbandonInterruptedAfterRecovery(SessionState state)
        => PendingParentCount == 0
           && ResolvedParentCount == 0
           && ParkedToolBatchHistory.FindRedrivableAssistantMessage(state.History, null) is not null;

    public bool HasPendingApprovals() => PendingParentCount > 0;

    // A parked batch still waits for an answer, or has an answer and no result yet. Input admitted after it waits.
    // Only parent approvals count here. The child consent pause holds automated input on its own rule.
    public bool HasParkedApprovals() => PendingParentCount > 0 || ResolvedParentCount > 0;

    public int PendingDiagnosticCount => PendingParentCount;

    public int ResolvedDiagnosticCount => ResolvedParentCount;
}
