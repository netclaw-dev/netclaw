// -----------------------------------------------------------------------
// <copyright file="SessionToolWorkflow.Children.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Authorization.Consent;
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
    // <child-approval-close>
    // A child owner never survives a restart, so recovery closes every child entry that replay left open.
    public void ExpireRecoveredChildApprovals()
    {
        foreach (var key in _calls.Where(pair => pair.Value.Pending.Request.SourceChildRunId is not null)
                     .Select(pair => pair.Key).ToArray())
            _calls.Remove(key);
    }

    // The live close rule: a cancel record or a terminal record (also the Lost record that recovery writes) closes every
    // entry of its run. A checkpoint or a prepared result leaves the prompt open. Replay does not call this method.
    public void CloseChildApprovals(ChildRunEvent evt)
    {
        if (evt is not (ChildRunEvent.CancellationRequested or ChildRunEvent.TerminalRecorded))
            return;
        foreach (var key in _calls.Where(pair => pair.Value.Pending.Request.SourceChildRunId == evt.RunId)
                     .Select(pair => pair.Key).ToArray())
            _calls.Remove(key);
    }
    // </child-approval-close>

    // A cancel or the end of a run closes its entries (CloseChildApprovals). Each entry that was still open becomes a
    // withdrawal with the reason of the record. An entry with an answer closed already, so it gives no event.
    public IReadOnlyList<AgentEvent.ChildApprovalResolved> WithdrawChildApprovals(
        ChildRunEvent evt, BackgroundChildRun run, SessionId owner, DateTimeOffset at)
    {
        WithdrawReason reason;
        switch (evt)
        {
            case ChildRunEvent.CancellationRequested:
                reason = WithdrawReason.CancelledByParent;
                break;
            case ChildRunEvent.TerminalRecorded terminal:
                reason = terminal.Terminal.Lost ? WithdrawReason.ChildLost : WithdrawReason.ChildEnded;
                break;
            default:
                throw new InvalidOperationException($"A {evt.GetType().Name} record closes no child approval.");
        }

        var open = OpenChildRequests().Where(request => request.SourceChildRunId == evt.RunId)
            .Select(static request => new ToolCallId(request.CallId))
            .ToArray();
        CloseChildApprovals(evt);
        return open.Select(callId => AgentEventFactory.ApprovalResolved(owner, run, callId,
            new ChildApprovalResolution.Withdrawn(reason), at)).ToArray();
    }

    // The answer of a child request is durable. Only the workflow turns it into the typed event, after it applied it.
    public AgentEvent.ChildApprovalResolved CreateChildResolution(
        ToolApprovalResolved resolution, BackgroundChildRun run, SessionId owner, DateTimeOffset at)
        => AgentEventFactory.ApprovalResolved(owner, run, new ToolCallId(resolution.CallId),
            ChildApprovalResolution.From(ConsentAnswerCodec.FromJournalText(resolution.Decision)), at);

    // <child-consent-pause>
    // The child consent pause (design D5): the session is paused while any child consent request of the session is
    // open. A request with an answer is closed, also when its run did not resume yet. Actor-local and derived.
    public bool IsChildConsentPaused
        => _calls.Values.Any(static call => call is PendingToolApproval && call.Pending.Request.SourceChildRunId is not null);

    // While paused, the model cannot start more children. A slash start is accepted, because a person typed it.
    public bool RefusesChildStart(ChildRunStartKey startKey)
        => startKey is ChildRunStartKey.Tool && IsChildConsentPaused;
    // </child-consent-pause>

    /// <summary>The open child consent requests of the session, in request order.</summary>
    public IReadOnlyList<ToolApprovalRequested> OpenChildRequests()
        => _calls.Values.OfType<PendingToolApproval>()
            .Select(static call => call.Pending.Request)
            .Where(static request => request.SourceChildRunId is not null)
            .OrderBy(static request => request.RequestedAtMs)
            .ToArray();

    public IReadOnlyList<PendingToolInteraction> OpenApprovals()
        => _calls.Values.OfType<PendingToolApproval>().Select(static call => call.Pending).ToArray();

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
