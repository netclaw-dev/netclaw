// -----------------------------------------------------------------------
// <copyright file="SessionToolWorkflow.Children.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

internal sealed partial class SessionToolWorkflow
{
    private sealed record SelectedChild(ChildContinuation Continuation, long ModelGeneration);
    private SelectedChild? _selectedChild;
    public bool IsPendingChildStart(ToolCallId callId) => _activeBatch.IsPending(callId);
    public bool HasChildContinuation => _selectedChild is not null;

    public BackgroundChildRun? SelectChild(SessionState state, long modelGeneration, bool allowSelection)
    {
        if (_selectedChild is { } selected)
        {
            _selectedChild = selected with { ModelGeneration = modelGeneration };
            return state.ChildRuns[selected.Continuation.RunId];
        }
        if (!allowSelection)
            return null;
        var run = state.ChildRuns.Values.FirstOrDefault(run => run.Lifecycle is ChildRunLifecycle.Consumed)
            ?? state.ChildRuns.Values.FirstOrDefault(run => run.Lifecycle is ChildRunLifecycle.Prepared);
        var delivery = run?.Lifecycle switch
        {
            ChildRunLifecycle.Prepared prepared => prepared.Delivery,
            ChildRunLifecycle.Consumed consumed => consumed.Delivery,
            _ => null
        };
        if (run is null || delivery is null)
            return null;
        _selectedChild = new SelectedChild(new ChildContinuation(run.RunId, delivery.Result.ToolCallId!.Value), modelGeneration);
        return run;
    }

    public SessionState ModelState(SessionState state)
    {
        if (_selectedChild is not { } selected ||
            state.ChildRuns[selected.Continuation.RunId].Lifecycle is not ChildRunLifecycle.Prepared prepared)
            return state;
        return state with { History = state.History.Add(prepared.Delivery.Call).Add(prepared.Delivery.Result) };
    }

    public ChildContinuation? ChildDecision(long modelGeneration)
    {
        if (_selectedChild is not { } selected)
            return null;
        if (selected.ModelGeneration != modelGeneration)
            throw new InvalidOperationException("A stale model generation cannot consume a child result.");
        return selected.Continuation;
    }

    public void ChildContinuationClosed() => _selectedChild = null;
    public int ChildApprovalCount(SubAgentRunId runId)
        => _calls.Values.Count(call => call is PendingToolApproval && call.Pending.Request.SourceChildRunId == runId);
    public bool IsChildApproval(ToolCallId callId)
        => _calls.TryGetValue(callId.Value, out var call) && call.Pending.Request.SourceChildRunId is not null;
    public void ExpireRecoveredChildApprovals()
    {
        foreach (var key in _calls.Where(pair => pair.Value.Pending.Request.SourceChildRunId is not null)
                     .Select(pair => pair.Key).ToArray())
            _calls.Remove(key);
    }
    public void ExpireChildApprovals(SubAgentRunId runId)
    {
        foreach (var key in _calls.Where(pair => pair.Value.Pending.Request.SourceChildRunId == runId)
                     .Select(pair => pair.Key).ToArray())
            _calls.Remove(key);
    }

    public InteractiveApprovalCapability ChildApprovalCapability(BackgroundChildRun run, IActorRef owner)
    {
        if (!TurnContext.TryFromRecord(run.OriginalContext, out var context, out var reason) || context is null)
            throw new InvalidOperationException($"Child authority is invalid: {reason}");
        if (!context.SupportsInteractiveApproval)
            return new InteractiveApprovalCapability.Unavailable();
        var bridge = new ParentSessionApprovalBridge(_approvalChannel,
            dispatch => owner.Tell(dispatch with
            {
                PersistApprovalState = true,
                SourceChildRunId = run.RunId,
                ChildContext = context
            }), new ToolExecutionTimeout(Timeout.InfiniteTimeSpan), context.SessionId, run.RunId.Value,
            context.RequesterSenderId, context.RequesterPrincipal, context.HasAdoptedContext,
            context.HasThirdPartyAdoptedContext, context.AdoptedSpeakerIds);
        return new InteractiveApprovalCapability.Available(bridge);
    }
}
