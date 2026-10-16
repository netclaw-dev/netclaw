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
    public bool IsPendingChildStart(ToolCallId callId) => _activeBatch.IsPending(callId);

    public void ValidateRecoveredAuthority(TurnContext context)
    {
        foreach (var entry in _calls.Values.Where(call => call.Pending.Request.SourceChildRunId is null))
        {
            if (entry.Pending.TurnContext is not { } authority || !authority.HasSameAuthority(context))
                throw new InvalidDataException("A legacy task context differs from its recovered approval authority.");
        }
    }

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
