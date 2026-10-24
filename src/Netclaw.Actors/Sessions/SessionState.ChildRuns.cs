// -----------------------------------------------------------------------
// <copyright file="SessionState.ChildRuns.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Channels;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

public sealed partial record SessionState
{
    public ImmutableDictionary<SubAgentRunId, BackgroundChildRun> ChildRuns { get; init; } = [];

    // <open-child-runs>
    public IEnumerable<BackgroundChildRun> GetExecutableChildRuns()
        => ChildRuns.Values.Where(run => !TryGetLegacyChildDisposition(run.RunId, out _));

    /// <summary>
    /// The runs whose result has not yet had its follow-up parent turn: a run with no notice, and a run whose notice
    /// input is still pending. The turn that consumes the notice, or a failed turn that closes it, ends the run here.
    /// The order is stable: acceptance time, then run id. A run that the legacy reader quarantined never reports, so
    /// it is not open.
    /// </summary>
    public IReadOnlyList<SubAgentRunId> GetOpenChildRunIds()
        => [.. GetExecutableChildRuns()
            .Where(run => run.Lifecycle is not ChildRunLifecycle.Reported reported
                          || (reported.Report is ChildRunReport.Notice notice
                              && PendingInputs.Any(input => input.InputId == notice.NoticeInputId)))
            .OrderBy(run => run.AcceptedAtMs)
            .ThenBy(run => run.RunId.Value, StringComparer.Ordinal)
            .Select(run => run.RunId)];
    // </open-child-runs>

    /// <summary>Runs whose result the parent has not received: still running, or ended without a notice.</summary>
    public IEnumerable<BackgroundChildRun> GetUnreportedRuns()
        => GetExecutableChildRuns().Where(run => run.Lifecycle is not ChildRunLifecycle.Reported);

    public SessionState Apply(ChildRunAccepted evt)
    {
        var run = evt.Run;
        if (run.StartKey.SessionId != evt.SessionId || run.OriginalContext.SessionId != evt.SessionId ||
            run.StartKey.TurnId != run.OriginalContext.TurnId || run.Lifecycle is not ChildRunLifecycle.Accepted ||
            run.InitialWorkingSnapshot is null ||
            string.IsNullOrWhiteSpace(run.RunId.Value) || string.IsNullOrWhiteSpace(run.ArgumentsDigest) ||
            !TurnContext.TryFromRecord(run.OriginalContext, out _, out _))
            throw new InvalidOperationException("Child acceptance has invalid identity or authority.");
        if (ChildRuns.ContainsKey(run.RunId) || ChildRuns.Values.Any(existing => existing.StartKey == run.StartKey))
            throw new InvalidOperationException("Child acceptance repeats a durable start identity.");
        if (run.StartKey is ChildRunStartKey.Tool tool &&
            (evt.AcceptanceResult is not { Role: ChatRole.Tool } result || result.ToolCallId != tool.CallId ||
             !History.Any(message => message.ToolCalls.Any(call => call.CallId == tool.CallId))))
            throw new InvalidOperationException("Child acceptance must close its original tool call.");
        if (run.StartKey is ChildRunStartKey.Slash && evt.AcceptanceResult is not null)
            throw new InvalidOperationException("A slash start cannot create an initial tool result.");
        return this with
        {
            ChildRuns = ChildRuns.Add(run.RunId, run),
            History = evt.AcceptanceResult is { } accepted ? History.Add(accepted) : History
        };
    }

    public SessionState Apply(ChildRunEvent evt)
    {
        if (!ChildRuns.TryGetValue(evt.RunId, out var run) || run.OriginalContext.SessionId != evt.SessionId)
            throw new InvalidOperationException("Child event does not name an accepted run.");
        if (run.InitialWorkingSnapshot is null && run.Lifecycle is ChildRunLifecycle.Accepted or ChildRunLifecycle.Cancelling
            or ChildRunLifecycle.Parked or ChildRunLifecycle.Terminal { LegacyPrepared: null })
            throw new InvalidOperationException("An unprepared child requires its initial context snapshot.");
        ChildRunLifecycle next = (run.Lifecycle, evt) switch
        {
            (ChildRunLifecycle.Accepted current, ChildRunEvent.Checkpointed update)
                when update.Checkpoint.CompletedRound > (current.Checkpoint?.CompletedRound ?? -1)
                => new ChildRunLifecycle.Accepted(update.Checkpoint),
            (ChildRunLifecycle.Accepted current, ChildRunEvent.CancellationRequested)
                => new ChildRunLifecycle.Cancelling(current.Checkpoint),
            (ChildRunLifecycle.Cancelling, ChildRunEvent.CancellationRequested) => run.Lifecycle,
            (ChildRunLifecycle.Accepted or ChildRunLifecycle.Cancelling or ChildRunLifecycle.Parked, ChildRunEvent.TerminalRecorded terminal)
                when terminal.Terminal.Result.RunId == run.RunId && terminal.Terminal.Result.ScopeId == run.ScopeId
                => new ChildRunLifecycle.Terminal(terminal.Terminal),
            // durable-child-park-recovery, design D3. The writer numbers the prompts from the counter of the run.
            (ChildRunLifecycle.Accepted, ChildRunEvent.Parked parked)
                when parked.Requests.Count > 0 && parked.PromptsIssued == run.PromptsIssued + parked.Requests.Count
                     && parked.Requests.All(request => request.SourceChildRunId == run.RunId)
                => new ChildRunLifecycle.Parked(parked.Park, [.. parked.Requests.Select(static request => new ChildParkRequest(request, null))]),
            // The resume record comes only after every answer. The checkpoint of the resume holds the round of the park,
            // so the first checkpoint of the resumed child is the next round.
            (ChildRunLifecycle.Parked current, ChildRunEvent.Resumed)
                when current.Requests.All(static request => request.Resolution is not null)
                => new ChildRunLifecycle.Accepted(new ChildRunCheckpoint(current.Park.LoopGuard.ToolIterationCount,
                    "The child resumed after consent.", current.Park.FileActivity)),
            (ChildRunLifecycle.Terminal { LegacyPrepared: null } terminal, ChildRunEvent.ResultPrepared prepared)
                when IsCanonicalChildPair(run, prepared.Call, prepared.Result) &&
                     (terminal.Result.Result.WorkingContext is not null || prepared.ReviewedWorkingContext is null)
                => terminal with
                {
                    LegacyPrepared = new LegacyPreparedResult(prepared.Call, prepared.Result, prepared.ReviewedWorkingContext)
                },
            _ => throw new InvalidOperationException("Child event is invalid for its current lifecycle.")
        };
        return this with
        {
            ChildRuns = ChildRuns.SetItem(run.RunId, run with
            {
                Lifecycle = next,
                InitialWorkingSnapshot = next is ChildRunLifecycle.Terminal { LegacyPrepared: not null } ? null : run.InitialWorkingSnapshot,
                PromptsIssued = evt is ChildRunEvent.Parked { PromptsIssued: var issued } ? issued : run.PromptsIssued
            })
        };
    }

    // The answer of a parked request goes into the park, so a snapshot keeps it. Any other answer changes nothing: a
    // parent answer, or the answer of a 0.28 live wait that a replay reads.
    public SessionState Apply(ToolApprovalResolved evt)
    {
        if (evt.SourceChildRunId is not { } runId || !ChildRuns.TryGetValue(runId, out var run)
            || run.Lifecycle is not ChildRunLifecycle.Parked parked
            || parked.Requests.FirstOrDefault(request => request.Request.CallId == evt.CallId) is not { Resolution: null } open)
            return this;
        return this with
        {
            ChildRuns = ChildRuns.SetItem(runId, run with
            {
                Lifecycle = parked with { Requests = parked.Requests.Replace(open, open with { Resolution = evt }) }
            })
        };
    }

    public SessionState Apply(InputClosed evt)
    {
        if (evt.RejectedJobReport is not null && evt.SourceBackgroundJobId is null)
            throw new InvalidDataException("A rejected job report requires its source job identity.");
        var next = CloseLegacyContinuation(evt.ChildContinuation, close: true)
            .CloseInputs(evt.InputIds).CompleteTurnBackgroundJobBookkeeping(evt.SourceBackgroundJobId);
        return evt.RejectedJobReport is { } report ? next with { History = next.History.Add(report) } : next;
    }

    // Security boundary: one notice per ended run. The run state is the guard. The recent source keys are not,
    // because a restart drops them. The notice carries the recorded authority of the run owner, never a wider one.
    private SessionState ApplyChildNotice(InputAdmitted evt, ChildNotice notice)
    {
        var run = RequireNoticeRun(evt, notice);
        var terminal = (ChildRunLifecycle.Terminal)run.Lifecycle;
        var result = terminal.Result.Result;
        return this with
        {
            ChildRuns = ChildRuns.SetItem(run.RunId, run with
            {
                InitialWorkingSnapshot = null,
                Lifecycle = new ChildRunLifecycle.Reported(result.Outcome, result.OutcomeReason,
                    new ChildRunReport.Notice(evt.InputId))
            }),
            WorkingContext = notice.ReviewedWorkingContext is { } reviewed
                ? WorkingContextUpdater.MergeSuccessfulSubAgentWorkingContext(WorkingContext, new ChildRunCompletion.Completed(reviewed))
                : WorkingContext,
            LegacyCoordinationEvidence = WithoutChildContinuationEvidence(run.RunId)
        };
    }

    private BackgroundChildRun RequireNoticeRun(InputAdmitted evt, ChildNotice notice)
    {
        var run = ChildRuns.GetValueOrDefault(notice.RunId);
        if (run is null || run.OriginalContext.SessionId != evt.SessionId)
            throw new InvalidOperationException("A child notice does not name an accepted run.");
        if (run.Lifecycle is not ChildRunLifecycle.Terminal)
            throw new InvalidOperationException("A child notice requires an ended run that has no notice yet.");
        if (evt.UserMessage.Role != ChatRole.User || !LegacyCoordinationReplay.SameAuthority(run.OriginalContext, evt.TurnContext))
            throw new InvalidOperationException("A child notice must carry the recorded authority of its run owner.");
        // A run with a delta can carry a reviewed context, also when it is not a success: a run that a restart lost
        // keeps the merge of its checkpoint. An older binary reads that run as a success and writes the same notice.
        if (notice.ReviewedWorkingContext is not null && ((ChildRunLifecycle.Terminal)run.Lifecycle).Result.Result.WorkingContext is null)
            throw new InvalidOperationException("Only a child run with a working-context delta can carry a reviewed working context.");
        return run;
    }

    // The deployed reader. A deployed parent decision named the tool pair that it consumed. The closure puts the pair
    // into the history once and marks the run as reported, so the result never returns as a notice.
    // close: false is a tool batch that continues the turn. close: true ends the decision.
    internal SessionState CloseLegacyContinuation(ChildContinuation? continuation, bool close)
    {
        if (continuation is null)
            return this;
        if (!ChildRuns.TryGetValue(continuation.RunId, out var run))
            throw new InvalidOperationException("A parent decision names an unknown child result.");
        var evidence = close ? WithoutChildContinuationEvidence(run.RunId) : LegacyCoordinationEvidence;
        switch (run.Lifecycle)
        {
            case ChildRunLifecycle.Reported { Report: ChildRunReport.LegacyDelivery delivered }
                when delivered.DeliveryCallId == continuation.DeliveryCallId:
                return this with { LegacyCoordinationEvidence = evidence };
            case ChildRunLifecycle.Terminal { LegacyPrepared: { } prepared } terminal
                when prepared.Result.ToolCallId == continuation.DeliveryCallId:
                var result = terminal.Result.Result;
                return this with
                {
                    History = History.Add(prepared.Call).Add(prepared.Result),
                    WorkingContext = prepared.ReviewedWorkingContext is { } reviewed
                        ? WorkingContextUpdater.MergeSuccessfulSubAgentWorkingContext(WorkingContext, new ChildRunCompletion.Completed(reviewed))
                        : WorkingContext,
                    ChildRuns = ChildRuns.SetItem(run.RunId, run with
                    {
                        Lifecycle = new ChildRunLifecycle.Reported(result.Outcome, result.OutcomeReason,
                            new ChildRunReport.LegacyDelivery(continuation.DeliveryCallId))
                    }),
                    LegacyCoordinationEvidence = evidence
                };
            default:
                throw new InvalidOperationException("A parent decision names a child result that no deployed delivery prepared.");
        }
    }

    private ImmutableList<LegacyCoordinationEvidence> WithoutChildContinuationEvidence(SubAgentRunId runId)
        => LegacyCoordinationEvidence.RemoveAll(item =>
            item.Disposition == LegacyCoordinationDisposition.AppliedChildContinuation && item.ChildRunId == runId);

    private static bool IsCanonicalChildPair(BackgroundChildRun run, SerializableChatMessage call, SerializableChatMessage result)
        => call is { Role: ChatRole.Assistant, ToolCalls.Count: 1 } &&
           result is { Role: ChatRole.Tool, ToolCallId: { } resultId } &&
           call.ToolCalls[0].CallId == resultId &&
           call.ToolCalls[0].Name.Value == "child_run_result" &&
           result.Name == "child_run_result" &&
           (run.StartKey is not ChildRunStartKey.Tool original || original.CallId != resultId);
}
