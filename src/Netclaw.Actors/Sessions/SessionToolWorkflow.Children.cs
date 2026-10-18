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
    // The child whose result the running turn delivers. Unlike ChildDecision, this read has no model-generation check.
    public ChildContinuation? SelectedContinuation => _selectedChild?.Continuation;

    public BackgroundChildRun? SelectChild(SessionState state, long modelGeneration, bool allowSelection)
    {
        if (_selectedChild is { } selected)
        {
            _selectedChild = selected with { ModelGeneration = modelGeneration };
            return state.ChildRuns[selected.Continuation.RunId];
        }
        if (!allowSelection)
            return null;
        var run = state.GetExecutableChildRuns().FirstOrDefault(run => run.Lifecycle is ChildRunLifecycle.Consumed)
            ?? state.GetExecutableChildRuns().FirstOrDefault(run => run.Lifecycle is ChildRunLifecycle.Prepared);
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

    public void ValidateRecoveredAuthority(TurnContext context)
    {
        foreach (var entry in _calls.Values.Where(call => call.Pending.Request.SourceChildRunId is null))
        {
            if (entry.Pending.TurnContext is not { } authority || !authority.HasSameAuthority(context))
                throw new InvalidDataException("A legacy task context differs from its recovered approval authority.");
        }
    }

    public void RestoreConsumedChild(SessionState state, TurnContext authority, long modelGeneration)
    {
        var run = state.GetExecutableChildRuns().SingleOrDefault(child => child.Lifecycle is ChildRunLifecycle.Consumed);
        if (run is null)
            return;
        if (!TurnContext.TryFromRecord(run.OriginalContext, out var original, out _) || original is null ||
            original.TurnId != authority.TurnId || !original.HasSameAuthority(authority))
            throw new InvalidOperationException("A recovered child continuation has different approval authority.");
        var delivery = ((ChildRunLifecycle.Consumed)run.Lifecycle).Delivery;
        _selectedChild = new SelectedChild(new ChildContinuation(run.RunId, delivery.Result.ToolCallId!.Value), modelGeneration);
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
    public IEnumerable<PendingToolInteraction> OpenApprovals()
        => _calls.Values.OfType<PendingToolApproval>().Select(call => call.Pending);
    // Test-first stub (#2525). The implementer replaces this body: remove every entry of the run, ignore a missing one.
    public void ApplyChildApprovalsClosed(ChildApprovalsClosed evt)
    {
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
