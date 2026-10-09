// -----------------------------------------------------------------------
// <copyright file="ToolRecurrenceMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using static Netclaw.Actors.Sessions.SessionProtocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class ToolRecurrenceMutationTests
{
    [Fact]
    public void Duplicate_results_count_one_round_before_the_third_candidate_is_refused()
    {
        var tracker = new TurnStateTracker();
        var batch = Prepare("probe", 3);
        var result = Complete(batch, "same");
        tracker.ObserveCompleted(result);
        Assert.Equal(ToolCycleDecisionKind.Execute, tracker.EvaluateBeforeDispatch(Prepare("probe", 1)).Kind);
        tracker.ObserveCompleted(result);
        var decision = tracker.EvaluateBeforeDispatch(Prepare("probe", 1));
        Assert.Equal(ToolCycleDecisionKind.Correct, decision.Kind);
        Assert.Single(decision.RefusedCallIds);
    }

    [Fact]
    public void A_changed_actual_result_requires_two_new_equal_rounds()
    {
        var tracker = new TurnStateTracker();
        var batch = Prepare("probe", 1);
        tracker.ObserveCompleted(Complete(batch, "old"));
        tracker.ObserveCompleted(Complete(batch, "new"));
        Assert.Equal(ToolCycleDecisionKind.Execute, tracker.EvaluateBeforeDispatch(batch).Kind);
        tracker.ObserveCompleted(Complete(batch, "new"));
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(batch).Kind);
    }

    [Fact]
    public void An_unrelated_success_does_not_clear_the_established_suspicion()
    {
        var tracker = new TurnStateTracker();
        var batch = Prepare("probe", 1);
        tracker.ObserveCompleted(Complete(batch, "same"));
        tracker.ObserveCompleted(Complete(Prepare("diagnostic", 1), "fresh"));
        tracker.ObserveCompleted(Complete(batch, "same"));
        tracker.ObserveCompleted(Complete(Prepare("other", 1), "fresh"));
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(batch).Kind);
    }

    [Fact]
    public void A_corrected_episode_stops_after_unrelated_work_and_checkpoint_restore()
    {
        var tracker = new TurnStateTracker();
        var batch = Prepare("probe", 1);
        tracker.ObserveCompleted(Complete(batch, "same"));
        tracker.ObserveCompleted(Complete(batch, "same"));
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(batch).Kind);
        tracker.ObserveCompleted(Complete(Prepare("diagnostic", 1), "fresh"));
        var restored = new TurnStateTracker();
        restored.RestoreCheckpoint(tracker.CaptureCheckpoint("task"));
        Assert.Equal(ToolCycleDecisionKind.Stop, restored.EvaluateBeforeDispatch(batch).Kind);
    }

    [Fact]
    public void A_forged_pending_fact_cannot_exempt_an_unrelated_tool_identity()
    {
        var tracker = new TurnStateTracker();
        var batch = Prepare("probe", 1);
        tracker.ObserveCompleted(Complete(batch, "same", pending: true));
        tracker.ObserveCompleted(Complete(batch, "same", pending: true));
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(batch).Kind);
    }

    [Fact]
    public void Missing_actual_evidence_cannot_establish_a_completed_group()
    {
        var batch = Prepare("probe", 1);
        var admission = new ToolLoopAdmission
        {
            TaskId = "task", ActionHash = batch.Action.Value,
            Calls = batch.Calls.Select(call => new ToolLoopPreparedCall(call.CallId.Value,
                call.ToolName.Value, call.ArgumentsHash, call.AllowsPendingJob)).ToArray()
        };
        var state = SessionState.Empty.ApplyLoopAdmission(new ToolBatchStarted
        { LoopAdmission = admission, LoopDelta = new ToolLoopDelta { TaskId = "task", Reset = true } });
        state = state.ApplyLoopObservation(new ToolCallRecorded
        {
            LoopObservation = new ToolLoopObservation
            { CallId = "call-0", Category = -1, ResultHash = "unverified", MissingReceipt = true }
        });
        Assert.True(state.LoopReceiptFailure);
        Assert.Single(state.LoopObservations);
        Assert.Empty(state.LoopCheckpoint.Entries);
    }

    private static PreparedToolCycleBatch Prepare(string tool, int duplicates)
        => ToolCycleSignatureFactory.Prepare(Enumerable.Range(0, duplicates)
            .Select(index => new FunctionCallContent($"call-{index}", tool, new Dictionary<string, object?>())).ToArray(), new Executor());

    private static CompletedToolCycleIteration Complete(PreparedToolCycleBatch batch, string exactResultHash, bool pending = false)
        => ToolCycleSignatureFactory.CompleteEvidence(new ToolLoopAdmission
        {
            TaskId = "task", ActionHash = batch.Action.Value,
            Calls = batch.Calls.Select(call => new ToolLoopPreparedCall(call.CallId.Value,
                call.ToolName.Value, call.ArgumentsHash, call.AllowsPendingJob)).ToArray()
        }, batch.Calls.Select(call => new ToolLoopObservation
        { CallId = call.CallId.Value, Category = 0, ResultHash = exactResultHash, PendingJob = pending }).ToArray());

    private sealed class Executor : IToolExecutor
    {
        public Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("The detector fixture never executes a tool.");
    }
}
