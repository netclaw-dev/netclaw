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
    public IReadOnlyList<KeyValuePair<string, ToolApprovalCallState>> ExpireRecoveredChildApprovals()
    {
        var closed = _calls.Where(pair => pair.Value.Pending.Request.SourceChildRunId is not null).ToArray();
        foreach (var pair in closed)
            _calls.Remove(pair.Key);
        return closed;
    }

    // The live close rule: a cancel record or a terminal record (also the Lost record that recovery writes) closes every
    // entry of its run. A checkpoint or a prepared result leaves the prompt open. Replay does not call this method.
    public IReadOnlyList<KeyValuePair<string, ToolApprovalCallState>> CloseChildApprovals(ChildRunEvent evt)
    {
        if (evt is not (ChildRunEvent.CancellationRequested or ChildRunEvent.TerminalRecorded))
            return [];
        var closed = _calls.Where(pair => pair.Value.Pending.Request.SourceChildRunId == evt.RunId).ToArray();
        foreach (var pair in closed)
            _calls.Remove(pair.Key);
        return closed;
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

        return RememberClosedChildCalls(CloseChildApprovals(evt), _ => reason)
            .Select(closed => AgentEventFactory.ApprovalResolved(owner, run, closed.CallId,
                new ChildApprovalResolution.Withdrawn(closed.Reason), at))
            .ToArray();
    }

    // Recovery closes the child entries that replay left open (ExpireRecoveredChildApprovals). The run lifecycle in the
    // state gives the reason of each open entry: a run that was live at the restart, a cancelled run, or an ended run.
    // Recovery records a live run as lost only later (RecoverChildRuns), so "live" here means "lost by this restart".
    public IReadOnlyList<(ToolCallId CallId, WithdrawReason Reason)> WithdrawRecoveredChildApprovals(SessionState state)
        => RememberClosedChildCalls(ExpireRecoveredChildApprovals(),
            request => RecoveredWithdrawReason(state, request.SourceChildRunId!.Value));

    private static WithdrawReason RecoveredWithdrawReason(SessionState state, SubAgentRunId runId)
    {
        // A legacy journal can hold a child prompt with no run record. No child survives a restart, so the restart
        // withdrew that prompt.
        if (!state.ChildRuns.TryGetValue(runId, out var run))
            return WithdrawReason.DaemonRestarted;
        return run.Lifecycle switch
        {
            ChildRunLifecycle.Accepted => WithdrawReason.DaemonRestarted,
            ChildRunLifecycle.Cancelling => WithdrawReason.CancelledByParent,
            ChildRunLifecycle.Terminal terminal
                when terminal.Result.Result.OutcomeReason == SubAgentOutcomeReason.CancelledByParent
                => WithdrawReason.CancelledByParent,
            ChildRunLifecycle.Reported { Reason: var reported } when reported == SubAgentOutcomeReason.CancelledByParent
                => WithdrawReason.CancelledByParent,
            // A run that ended before the restart, also a run that the earlier process lost (coordinator ruling).
            ChildRunLifecycle.Terminal or ChildRunLifecycle.Reported => WithdrawReason.ChildEnded,
            _ => throw new InvalidOperationException($"Unknown child run lifecycle {run.Lifecycle.GetType().Name}.")
        };
    }

    // Keeps how each closed child prompt ended, so a late click gets the true reply (RejectClosedCall). Returns the
    // entries that were open, with their reasons, in request order. Actor-local: recovery rebuilds it from the replayed
    // approval events and the run lifecycle.
    private IReadOnlyList<(ToolCallId CallId, WithdrawReason Reason)> RememberClosedChildCalls(
        IReadOnlyList<KeyValuePair<string, ToolApprovalCallState>> closed,
        Func<ToolApprovalRequested, WithdrawReason> openReason)
    {
        var withdrawn = new List<(ToolCallId, WithdrawReason)>();
        foreach (var (callId, call) in closed.OrderBy(static pair => pair.Value.Pending.Request.RequestedAtMs))
        {
            switch (call)
            {
                case PendingToolApproval pending:
                    var reason = openReason(pending.Pending.Request);
                    _closedChildCalls[callId] = new ChildApprovalResolution.Withdrawn(reason);
                    withdrawn.Add((new ToolCallId(callId), reason));
                    break;
                case ResolvedToolApproval resolved:
                    _closedChildCalls[callId] = ChildResolution(resolved.Answer);
                    break;
            }
        }
        return withdrawn;
    }

    // A snapshot deletes the approval events of the closed child prompts, so replay cannot rebuild their closes.
    public void ForgetClosedChildCalls() => _closedChildCalls.Clear();

    // A channel answers prompt_unavailable when it cannot show a child prompt. The prompt has buttons only (design D3,
    // S7), so no person saw it: the session withdraws the request, and the call is refused.
    private static ChildApprovalResolution ChildResolution(ConsentAnswer answer)
        => answer is ConsentAnswer.Refused { Kind: RefusalKind.PromptUnavailable }
            ? new ChildApprovalResolution.Withdrawn(WithdrawReason.PromptUnavailable)
            : ChildApprovalResolution.From(answer);

    // The answer of a child request is durable. Only the workflow turns it into the typed event, after it applied it.
    public AgentEvent.ChildApprovalResolved CreateChildResolution(
        ToolApprovalResolved resolution, BackgroundChildRun run, SessionId owner, DateTimeOffset at)
        => AgentEventFactory.ApprovalResolved(owner, run, new ToolCallId(resolution.CallId),
            ChildResolution(ConsentAnswerCodec.FromJournalText(resolution.Decision)), at);

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
