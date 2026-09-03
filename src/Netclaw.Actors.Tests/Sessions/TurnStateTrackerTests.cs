// -----------------------------------------------------------------------
// <copyright file="TurnStateTrackerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Covers <see cref="TurnStateTracker.EvaluateEmptyResponse"/>: a thinking-only
/// response (reasoning emitted, no final answer) must get a thinking-specific
/// nudge, and the post-tool path must tolerate several retries before failing
/// the turn.
/// </summary>
public sealed class TurnStateTrackerTests
{
    private const string ThinkingNudgeMarker = "only reasoning";

    public enum ToolPhase
    {
        BeforeAnyToolUse,
        AfterToolUse,
    }

    [Theory]
    [InlineData(ToolPhase.AfterToolUse, LlmResponseKind.ThinkingOnly)]
    [InlineData(ToolPhase.AfterToolUse, LlmResponseKind.Empty)]
    [InlineData(ToolPhase.BeforeAnyToolUse, LlmResponseKind.ThinkingOnly)]
    [InlineData(ToolPhase.BeforeAnyToolUse, LlmResponseKind.Empty)]
    public void EmptyResponse_NudgeMatchesResponseKind(ToolPhase phase, LlmResponseKind kind)
    {
        var tracker = new TurnStateTracker();
        if (phase == ToolPhase.AfterToolUse)
            tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);

        var action = tracker.EvaluateEmptyResponse(kind, truncated: false);

        var retry = Assert.IsType<EmptyResponseAction.Retry>(action);
        if (kind == LlmResponseKind.ThinkingOnly)
            Assert.Contains(ThinkingNudgeMarker, retry.NudgeText);
        else
            Assert.DoesNotContain(ThinkingNudgeMarker, retry.NudgeText);
    }

    [Fact]
    public void TruncatedThinkingOnly_GetsBrevityNudge_NotStopThinkingScold()
    {
        var tracker = new TurnStateTracker();
        tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);

        // A length-truncated thinking-only response was cut off, not refused —
        // it must get the brevity nudge, never the "stop thinking" scold.
        var truncated = Assert.IsType<EmptyResponseAction.Retry>(
            tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: true));
        Assert.Contains("cut off", truncated.NudgeText);
        Assert.DoesNotContain(ThinkingNudgeMarker, truncated.NudgeText);

        // A non-truncated thinking-only still gets the stop-thinking nudge.
        var normal = Assert.IsType<EmptyResponseAction.Retry>(
            tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: false));
        Assert.Contains(ThinkingNudgeMarker, normal.NudgeText);
    }

    [Fact]
    public void EmptyResponseCounters_ResetOnToolBatch()
    {
        // A thinking model that emits a thinking-only response, then does tool
        // work, then emits another thinking-only response should NOT accumulate
        // toward the failure threshold — the consecutive counters reset on each
        // tool batch.
        var tracker = new TurnStateTracker();
        tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);

        for (var i = 0; i < 10; i++)
        {
            // One thinking-only response followed by a tool batch reset — the
            // consecutive counter resets each time, so this never fails.
            Assert.IsType<EmptyResponseAction.Retry>(
                tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: false));
            tracker.ResetEmptyResponseGuards();
        }
    }

    [Fact]
    public void EmptyResponseReset_PreservesForcedTextOnlyState()
    {
        var tracker = new TurnStateTracker
        {
            ForceNoToolsActive = true
        };

        tracker.ResetEmptyResponseGuards();

        Assert.True(tracker.ForceNoToolsActive);
    }

    [Fact]
    public void PostToolThinkingOnly_RetriesSeveralTimesBeforeFailing()
    {
        var tracker = new TurnStateTracker();
        tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);

        // The first 8 consecutive thinking-only responses retry.
        for (var i = 0; i < 8; i++)
            Assert.IsType<EmptyResponseAction.Retry>(tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: false));

        // The 9th fails the turn.
        Assert.IsType<EmptyResponseAction.Fail>(tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly, truncated: false));
    }

    [Fact]
    public void ParallelToolBatch_CountsAsOneIteration()
    {
        var tracker = new TurnStateTracker();

        var status = tracker.RecordToolCompletion(resultCount: 8, maxToolIterationsPerTurn: 30);

        Assert.IsType<ToolBudgetStatus.Ok>(status);
        Assert.Equal(1, tracker.ToolIterationCount);
        // ToolCallCount remains for telemetry only — it counts results, not iterations.
        Assert.Equal(8, tracker.ToolCallCount);
    }

    [Fact]
    public void MultipleSerialRounds_CountAsMultipleIterations()
    {
        var tracker = new TurnStateTracker();

        tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);
        tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);
        tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: 30);

        Assert.Equal(3, tracker.ToolIterationCount);
    }

    [Fact]
    public void ReachingIterationCap_ReturnsExhausted()
    {
        var tracker = new TurnStateTracker();
        const int cap = 4;

        // First (cap - 1) iterations stay below the limit.
        for (var i = 0; i < cap - 1; i++)
        {
            var status = tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: cap);
            Assert.IsNotType<ToolBudgetStatus.Exhausted>(status);
        }

        // The cap-th iteration hits the limit.
        var capped = tracker.RecordToolCompletion(resultCount: 1, maxToolIterationsPerTurn: cap);
        var exhausted = Assert.IsType<ToolBudgetStatus.Exhausted>(capped);
        Assert.Contains("executive summary", exhausted.NudgeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Partial or Unknown", exhausted.NudgeText, StringComparison.Ordinal);
        Assert.Equal(cap, tracker.ToolIterationCount);
    }

    [Fact]
    public void RawCallVolume_DoesNotControlTheLimit()
    {
        // 100 tool results delivered in a single iteration must NOT trigger the cap.
        var tracker = new TurnStateTracker();

        var status = tracker.RecordToolCompletion(resultCount: 100, maxToolIterationsPerTurn: 5);

        Assert.IsType<ToolBudgetStatus.Ok>(status);
        Assert.Equal(1, tracker.ToolIterationCount);
        Assert.Equal(100, tracker.ToolCallCount);
    }

    [Fact]
    public void ActionSignature_UsesCleanedCanonicalArguments()
    {
        var first = Prepare(new FunctionCallContent(
            "call-1",
            "sample/read",
            new Dictionary<string, object?>
            {
                ["id"] = "item-1",
                ["mode"] = "full",
                ["_rationale"] = "first"
            }));
        var second = Prepare(new FunctionCallContent(
            "call-2",
            "sample/read",
            new Dictionary<string, object?>
            {
                ["_rationale"] = "second",
                ["mode"] = "full",
                ["id"] = "item-1"
            }));

        Assert.Equal(first.Action, second.Action);
    }

    [Fact]
    public void ActionSignature_PreservesIdentifiersArraysAndMultiplicity()
    {
        var first = Prepare(Call(
            "call-1",
            "sample/read",
            ("id", "item-1"),
            ("values", new[] { 1, 2 })));
        var changedId = Prepare(Call(
            "call-2",
            "sample/read",
            ("id", "item-2"),
            ("values", new[] { 1, 2 })));
        var changedArray = Prepare(Call(
            "call-3",
            "sample/read",
            ("id", "item-1"),
            ("values", new[] { 2, 1 })));
        var duplicate = Prepare(
            Call("call-4", "sample/read", ("id", "item-1"), ("values", new[] { 1, 2 })),
            Call("call-5", "sample/read", ("id", "item-1"), ("values", new[] { 1, 2 })));

        Assert.NotEqual(first.Action, changedId.Action);
        Assert.NotEqual(first.Action, changedArray.Action);
        Assert.NotEqual(first.Action, duplicate.Action);
    }

    [Fact]
    public void ActionSignature_TreatsAbsentAndEmptyArgumentsAsEqual()
    {
        var absent = Prepare(new FunctionCallContent("call-1", "sample/read"));
        var empty = Prepare(new FunctionCallContent(
            "call-2",
            "sample/read",
            new Dictionary<string, object?>()));

        Assert.Equal(absent.Action, empty.Action);
    }

    [Fact]
    public void CompletedSignature_UsesTypedCategoryAndExactResult()
    {
        var batch = Prepare(Call("call-1", "sample/read"));

        var success = Complete(
            batch,
            ("call-1", ToolInvocationOutcomeCategory.Success, "Error: this is data"));
        var categoryChanged = Complete(
            batch,
            ("call-1", ToolInvocationOutcomeCategory.TransientFailure, "Error: this is data"));
        var resultChanged = Complete(
            batch,
            ("call-1", ToolInvocationOutcomeCategory.Success, "Error: this is other data"));

        Assert.NotEqual(success, categoryChanged);
        Assert.NotEqual(success, resultChanged);
    }

    [Theory]
    [InlineData("A,A", "A", 1)]
    [InlineData("A,B,A,B", "A", 2)]
    [InlineData("A,B,C,A,B,C", "A", 3)]
    public void ExactCycle_BlocksTheNextExpectedAction(
        string completedActions,
        string candidateAction,
        int expectedPeriod)
    {
        var tracker = new TurnStateTracker();
        foreach (var action in completedActions.Split(','))
        {
            var batch = Prepare(Call("call-" + action, "sample/" + action));
            tracker.ObserveCompleted(Complete(
                batch,
                ("call-" + action, ToolInvocationOutcomeCategory.Success, "result-" + action)));
        }

        var candidate = Prepare(Call("candidate", "sample/" + candidateAction));

        var decision = tracker.EvaluateBeforeDispatch(candidate.Action);

        Assert.Equal(ToolCycleDecisionKind.Correct, decision.Kind);
        Assert.Equal(expectedPeriod, decision.Period);
        Assert.Equal(2, decision.Repetitions);
    }

    [Fact]
    public void ChangedResult_DoesNotBlockAnEqualAction()
    {
        var tracker = new TurnStateTracker();
        var batch = Prepare(Call("call-1", "sample/read"));
        tracker.ObserveCompleted(Complete(
            batch,
            ("call-1", ToolInvocationOutcomeCategory.Success, "first")));
        tracker.ObserveCompleted(Complete(
            batch,
            ("call-1", ToolInvocationOutcomeCategory.Success, "second")));

        var decision = tracker.EvaluateBeforeDispatch(batch.Action);

        Assert.Equal(ToolCycleDecisionKind.Execute, decision.Kind);
    }

    [Fact]
    public void MixedParallelResult_DoesNotBlockTheBatch()
    {
        var tracker = new TurnStateTracker();
        var batch = Prepare(
            Call("call-a", "sample/a"),
            Call("call-b", "sample/b"));
        tracker.ObserveCompleted(Complete(
            batch,
            ("call-a", ToolInvocationOutcomeCategory.Success, "same"),
            ("call-b", ToolInvocationOutcomeCategory.Success, "old")));
        tracker.ObserveCompleted(Complete(
            batch,
            ("call-a", ToolInvocationOutcomeCategory.Success, "same"),
            ("call-b", ToolInvocationOutcomeCategory.Success, "new")));

        var decision = tracker.EvaluateBeforeDispatch(batch.Action);

        Assert.Equal(ToolCycleDecisionKind.Execute, decision.Kind);
    }

    [Fact]
    public void RepeatedBlockedAction_StopsUntilAnotherActionCompletes()
    {
        var tracker = new TurnStateTracker();
        var repeated = Prepare(Call("call-a", "sample/a"));
        var other = Prepare(Call("call-b", "sample/b"));
        var completed = Complete(
            repeated,
            ("call-a", ToolInvocationOutcomeCategory.Success, "same"));
        tracker.ObserveCompleted(completed);
        tracker.ObserveCompleted(completed);

        Assert.Equal(
            ToolCycleDecisionKind.Correct,
            tracker.EvaluateBeforeDispatch(repeated.Action).Kind);
        Assert.Equal(
            ToolCycleDecisionKind.Stop,
            tracker.EvaluateBeforeDispatch(repeated.Action).Kind);

        tracker.ObserveCompleted(Complete(
            other,
            ("call-b", ToolInvocationOutcomeCategory.Success, "different")));

        Assert.Equal(
            ToolCycleDecisionKind.Execute,
            tracker.EvaluateBeforeDispatch(repeated.Action).Kind);
    }

    [Fact]
    public void NewTurnClearsCycleState_ButAnUnrelatedBoundaryDoesNot()
    {
        var tracker = new TurnStateTracker();
        var batch = Prepare(Call("call-1", "sample/read"));
        var completed = Complete(
            batch,
            ("call-1", ToolInvocationOutcomeCategory.Success, "same"));
        tracker.ObserveCompleted(completed);
        tracker.ObserveCompleted(completed);

        Assert.Equal(
            ToolCycleDecisionKind.Correct,
            tracker.EvaluateBeforeDispatch(batch.Action).Kind);

        tracker.ResetForNewTurn();

        Assert.Equal(0, tracker.CompletedCycleHistoryCount);
        Assert.Equal(
            ToolCycleDecisionKind.Execute,
            tracker.EvaluateBeforeDispatch(batch.Action).Kind);
    }

    [Fact]
    public void CompletedCycleHistory_KeepsOnlySixIterations()
    {
        var tracker = new TurnStateTracker();
        for (var i = 0; i < 12; i++)
        {
            var batch = Prepare(Call("call-" + i, "sample/" + i));
            tracker.ObserveCompleted(Complete(
                batch,
                ("call-" + i, ToolInvocationOutcomeCategory.Success, "result-" + i)));
        }

        Assert.Equal(6, tracker.CompletedCycleHistoryCount);
    }

    private static PreparedToolCycleBatch Prepare(params FunctionCallContent[] calls)
        => ToolCycleSignatureFactory.Prepare(calls, new FakeToolExecutor());

    private static CompletedToolCycleIteration Complete(
        PreparedToolCycleBatch batch,
        params (string CallId, ToolInvocationOutcomeCategory Category, string Text)[] results)
        => ToolCycleSignatureFactory.Complete(
            batch,
            results.ToDictionary(
                static result => result.CallId,
                static result => new ToolCycleResult(result.Category, result.Text),
                StringComparer.Ordinal));

    private static FunctionCallContent Call(
        string callId,
        string toolName,
        params (string Name, object? Value)[] arguments)
        => new(
            callId,
            toolName,
            arguments.ToDictionary(
                static argument => argument.Name,
                static argument => argument.Value,
                StringComparer.Ordinal));
}
