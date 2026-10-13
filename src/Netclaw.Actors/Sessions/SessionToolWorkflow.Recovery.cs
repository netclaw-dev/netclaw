// -----------------------------------------------------------------------
// <copyright file="SessionToolWorkflow.Recovery.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

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
                    "Tool call was not completed — approval state could not be recovered safely.",
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
        => ResolvedCount > 0 && PendingCount == 0;

    public bool ShouldAbandonResolvedForIdle() => ResolvedCount > 0;

    public ToolBatchAbandoned? PlanNewInputAbandonment(
        SessionState state,
        SessionId sessionId,
        long nowMs)
    {
        if (PendingCount > 0)
        {
            MarkAbandoning();
            return CreateAbandonment(
                state,
                sessionId,
                "Tool call was not completed — the request was "
                + "superseded by a new message before approval was given.",
                nowMs);
        }

        if (ResolvedCount > 0)
            return CreateAbandonment(
                state,
                sessionId,
                "Tool call was not completed — the session restarted after approval before the action completed.",
                nowMs);

        return ShouldAbandonInterruptedAfterRecovery(state)
            ? CreateAbandonment(
                state,
                sessionId,
                "Tool call was not completed — the session restarted before the action completed.",
                nowMs)
            : null;
    }

    public ToolBatchAbandoned? PlanFailedRunAbandonment(
        SessionState state,
        SessionId sessionId,
        long nowMs)
        => ParkedToolBatchHistory.FindRedrivableAssistantMessage(state.History, null) is null
            ? null
            : CreateAbandonment(
                state,
                sessionId,
                "Tool call was not completed — the tool run failed.",
                nowMs);

    public bool ShouldAbandonInterruptedAfterRecovery(SessionState state)
        => PendingCount == 0
           && ResolvedCount == 0
           && ParkedToolBatchHistory.FindRedrivableAssistantMessage(state.History, null) is not null;

    public bool HasPendingApprovals() => PendingCount > 0;

    public int PendingDiagnosticCount => PendingCount;

    public int ResolvedDiagnosticCount => ResolvedCount;
}
