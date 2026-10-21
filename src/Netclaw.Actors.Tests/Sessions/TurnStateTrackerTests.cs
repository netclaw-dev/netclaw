// -----------------------------------------------------------------------
// <copyright file="TurnStateTrackerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using CsCheck;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Covers turn state, tool recurrence, and empty-response behavior.
/// </summary>
public sealed class TurnStateTrackerTests
{
    private const string ThinkingNudgeMarker = "only reasoning";
    private const string RecurrenceSampleSeed = "ToolRepeatModel1";

    private sealed record CompletedRound(string Identity, int Outcome);
    private sealed record RecurrenceScenario(CompletedRound[] Rounds, string CandidateIdentity);

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
            tracker.RecordToolCompletion(resultCount: 1);

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
        tracker.RecordToolCompletion(resultCount: 1);

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
        tracker.RecordToolCompletion(resultCount: 1);

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
    public void PostToolThinkingOnly_RetriesSeveralTimesBeforeFailing()
    {
        var tracker = new TurnStateTracker();
        tracker.RecordToolCompletion(resultCount: 1);

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

        tracker.RecordToolCompletion(resultCount: 8);
        Assert.Equal(1, tracker.ToolIterationCount);
        // ToolCallCount remains for telemetry only — it counts results, not iterations.
        Assert.Equal(8, tracker.ToolCallCount);
    }

    [Fact]
    public void MultipleSerialRounds_CountAsMultipleIterations()
    {
        var tracker = new TurnStateTracker();

        tracker.RecordToolCompletion(resultCount: 1);
        tracker.RecordToolCompletion(resultCount: 1);
        tracker.RecordToolCompletion(resultCount: 1);

        Assert.Equal(3, tracker.ToolIterationCount);
    }

    [Fact]
    public void IterationCountDoesNotStopUsefulWork()
    {
        var tracker = new TurnStateTracker();
        for (var i = 0; i < 64; i++)
            tracker.RecordToolCompletion(resultCount: 1);

        Assert.Equal(64, tracker.ToolIterationCount);
    }

    [Fact]
    public void RawCallVolume_DoesNotControlTheLimit()
    {
        // 100 tool results delivered in a single iteration must NOT trigger the cap.
        var tracker = new TurnStateTracker();

        tracker.RecordToolCompletion(resultCount: 100);
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

        var decision = tracker.EvaluateAdjacentBeforeDispatch(candidate.Action);

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

        var decision = tracker.EvaluateAdjacentBeforeDispatch(batch.Action);

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

        var decision = tracker.EvaluateBeforeDispatch(batch);

        Assert.Equal(ToolCycleDecisionKind.Correct, decision.Kind);
        Assert.Equal(["call-a"], decision.RefusedCallIds);
    }

    [Fact]
    public void DuplicateCallsCountOncePerRound_AndEveryRepeatedCallIdGetsRefused()
    {
        var tracker = new TurnStateTracker();
        var duplicateRound = Prepare(
            Call("round-1-a", "sample/read", ("id", "item-1")),
            Call("round-1-b", "sample/read", ("id", "item-1")));
        var singleRound = Prepare(Call("round-2", "sample/read", ("id", "item-1")));
        tracker.ObserveCompleted(Complete(
            duplicateRound,
            ("round-1-a", ToolInvocationOutcomeCategory.Success, "same"),
            ("round-1-b", ToolInvocationOutcomeCategory.Success, "same")));
        tracker.ObserveCompleted(Complete(
            singleRound,
            ("round-2", ToolInvocationOutcomeCategory.Success, "same")));

        var candidate = Prepare(
            Call("candidate-a", "sample/read", ("id", "item-1")),
            Call("candidate-b", "sample/read", ("id", "item-1")));

        var decision = tracker.EvaluateBeforeDispatch(candidate);

        Assert.Equal(ToolCycleDecisionKind.Correct, decision.Kind);
        Assert.Equal(new[] { "candidate-a", "candidate-b" }, decision.RefusedCallIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RepeatedIdentityDoesNotBlockAProductiveSibling()
    {
        var tracker = new TurnStateTracker();
        var first = Prepare(Call("round-1", "sample/read", ("id", "item-1")));
        var second = Prepare(Call("round-2", "sample/read", ("id", "item-1")));
        tracker.ObserveCompleted(Complete(first, ("round-1", ToolInvocationOutcomeCategory.Success, "same")));
        tracker.ObserveCompleted(Complete(second, ("round-2", ToolInvocationOutcomeCategory.Success, "same")));

        var candidate = Prepare(
            Call("repeat", "sample/read", ("id", "item-1")),
            Call("diagnostic", "sample/diagnostic", ("id", "host-1")));

        var decision = tracker.EvaluateBeforeDispatch(candidate);

        Assert.Equal(ToolCycleDecisionKind.Correct, decision.Kind);
        Assert.Equal(new[] { "repeat" }, decision.RefusedCallIds);
    }

    [Fact]
    public void ChangedOutcomeResetsOnlyThatIdentity()
    {
        var tracker = new TurnStateTracker();
        var first = Prepare(
            Call("round-1-a", "sample/read", ("id", "a")),
            Call("round-1-b", "sample/read", ("id", "b")));
        var second = Prepare(
            Call("round-2-a", "sample/read", ("id", "a")),
            Call("round-2-b", "sample/read", ("id", "b")));
        var changed = Prepare(
            Call("round-3-a", "sample/read", ("id", "a")),
            Call("round-3-b", "sample/read", ("id", "b")));
        tracker.ObserveCompleted(Complete(first,
            ("round-1-a", ToolInvocationOutcomeCategory.Success, "same"),
            ("round-1-b", ToolInvocationOutcomeCategory.Success, "same")));
        tracker.ObserveCompleted(Complete(second,
            ("round-2-a", ToolInvocationOutcomeCategory.Success, "same"),
            ("round-2-b", ToolInvocationOutcomeCategory.Success, "same")));
        tracker.ObserveCompleted(Complete(changed,
            ("round-3-a", ToolInvocationOutcomeCategory.Success, "changed"),
            ("round-3-b", ToolInvocationOutcomeCategory.Success, "same")));

        var candidate = Prepare(
            Call("candidate-a", "sample/read", ("id", "a")),
            Call("candidate-b", "sample/read", ("id", "b")));

        var decision = tracker.EvaluateBeforeDispatch(candidate);

        Assert.Equal(ToolCycleDecisionKind.Correct, decision.Kind);
        Assert.Equal(new[] { "candidate-b" }, decision.RefusedCallIds);
    }

    [Fact]
    public void SyntheticCorrectionDoesNotCountAsAnActualOutcome()
    {
        var tracker = new TurnStateTracker();
        var call = Prepare(Call("call-1", "sample/read", ("id", "item-1")));
        tracker.ObserveCompleted(Complete(call, ("call-1", ToolInvocationOutcomeCategory.Success, "same")));
        tracker.ObserveCompleted(CompleteSynthetic(
            call,
            ("call-1", ToolInvocationOutcomeCategory.Success, "correction")));

        Assert.Equal(ToolCycleDecisionKind.Execute, tracker.EvaluateBeforeDispatch(call).Kind);
    }

    [Fact]
    public void Generated_completed_rounds_match_the_independent_identity_model()
    {
        var identities = Gen.OneOfConst("alpha", "beta", "gamma");
        var rounds = Gen.Select(identities, Gen.Int[0, 2],
            static (identity, outcome) => new CompletedRound(identity, outcome));
        var scenarios = Gen.Select(rounds.Array[1, 8], identities,
            static (completed, candidate) => new RecurrenceScenario(completed, candidate));
        var cases = 0;

        scenarios.Sample(scenario =>
        {
            var tracker = new TurnStateTracker();
            var model = new Dictionary<string, (int Outcome, int EqualRounds)>(StringComparer.Ordinal);
            for (var i = 0; i < scenario.Rounds.Length; i++)
            {
                var round = scenario.Rounds[i];
                var target = Call($"target-{i}", "sample/read", ("id", round.Identity));
                var unrelated = Call($"noise-{i}", "sample/diagnostic", ("id", i));
                var batch = Prepare(target, unrelated);
                tracker.ObserveCompleted(Complete(
                    batch,
                    ($"target-{i}", ToolInvocationOutcomeCategory.Success, $"outcome-{round.Outcome}"),
                    ($"noise-{i}", ToolInvocationOutcomeCategory.Success, $"noise-{i}")));

                model.TryGetValue(round.Identity, out var prior);
                var equalRounds = prior.Outcome == round.Outcome ? prior.EqualRounds + 1 : 1;
                model[round.Identity] = (round.Outcome, equalRounds);
            }

            var candidate = Prepare(Call(
                "candidate",
                "sample/read",
                ("id", scenario.CandidateIdentity)));
            var shouldRefuse = model.TryGetValue(scenario.CandidateIdentity, out var evidence)
                && evidence.EqualRounds >= 2;
            var decision = tracker.EvaluateBeforeDispatch(candidate);

            Assert.Equal(shouldRefuse ? ToolCycleDecisionKind.Correct : ToolCycleDecisionKind.Execute, decision.Kind);
            Assert.Equal(
                shouldRefuse ? new[] { "candidate" } : Array.Empty<string>(),
                decision.RefusedCallIds);
            if (shouldRefuse)
            {
                // A repeat after the correction gets the same refusal. The turn does not stop.
                var repeat = tracker.EvaluateBeforeDispatch(candidate);
                Assert.Equal(ToolCycleDecisionKind.Correct, repeat.Kind);
                Assert.Equal(new[] { "candidate" }, repeat.RefusedCallIds);
            }
            cases++;
        }, iter: 1_000, seed: RecurrenceSampleSeed, threads: 1,
            print: scenario => $"candidate={scenario.CandidateIdentity}; "
                + string.Join(" -> ", scenario.Rounds.Select(round => $"{round.Identity}:{round.Outcome}")));

        Assert.True(cases >= 1_000, $"CsCheck ran only {cases} cases.");
    }

    [Fact]
    public void RepeatedCall_IsRefusedAgainUntilItsOwnOutcomeChanges()
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
            tracker.EvaluateBeforeDispatch(repeated).Kind);
        var repeat = tracker.EvaluateBeforeDispatch(repeated);
        Assert.Equal(ToolCycleDecisionKind.Correct, repeat.Kind);
        Assert.Equal(["call-a"], repeat.RefusedCallIds);

        tracker.ObserveCompleted(Complete(
            other,
            ("call-b", ToolInvocationOutcomeCategory.Success, "different")));

        var afterOtherWork = tracker.EvaluateBeforeDispatch(repeated);
        Assert.Equal(ToolCycleDecisionKind.Correct, afterOtherWork.Kind);
        Assert.Equal(["call-a"], afterOtherWork.RefusedCallIds);

        tracker.ObserveCompleted(Complete(
            repeated,
            ("call-a", ToolInvocationOutcomeCategory.Success, "changed")));

        Assert.Equal(
            ToolCycleDecisionKind.Execute,
            tracker.EvaluateBeforeDispatch(repeated).Kind);
    }

    [Fact]
    public void RepeatedRefusedCall_DoesNotBlockANewSibling()
    {
        // Negative example: a refused call repeats in a batch with a new call. Only the repeat is refused.
        var tracker = new TurnStateTracker();
        var repeated = Prepare(Call("call-a", "sample/a"));
        var completed = Complete(
            repeated,
            ("call-a", ToolInvocationOutcomeCategory.Success, "same"));
        tracker.ObserveCompleted(completed);
        tracker.ObserveCompleted(completed);
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(repeated).Kind);

        var mixed = Prepare(
            Call("repeat", "sample/a"),
            Call("diagnostic", "sample/diagnostic", ("id", "host-1")));
        var decision = tracker.EvaluateBeforeDispatch(mixed);

        Assert.Equal(ToolCycleDecisionKind.Correct, decision.Kind);
        Assert.Equal(["repeat"], decision.RefusedCallIds);
    }

    [Fact]
    public void RefusedRepeats_GetTheNudgeFourTimes_TheEscalationAtTheFifth_AndTheEndAtTheSixth()
    {
        // Aaron's rule (18 Oct 2026): the loop gets an end, but the person never sees a Netclaw error.
        var tracker = new TurnStateTracker();
        var repeated = Prepare(Call("call-a", "sample/a"));
        var completed = Complete(
            repeated,
            ("call-a", ToolInvocationOutcomeCategory.Success, "same"));
        tracker.ObserveCompleted(completed);
        tracker.ObserveCompleted(completed);

        var correction = tracker.EvaluateBeforeDispatch(repeated);
        Assert.Equal(ToolCycleDecisionKind.Correct, correction.Kind);
        Assert.Equal(RefusedRepeatAction.None, correction.RefusedRepeat);

        var actions = Enumerable.Range(0, 6)
            .Select(_ => tracker.EvaluateBeforeDispatch(repeated))
            .ToArray();

        Assert.All(actions, static decision => Assert.Equal(["call-a"], decision.RefusedCallIds));
        Assert.Equal(
            [
                RefusedRepeatAction.Nudge, RefusedRepeatAction.Nudge, RefusedRepeatAction.Nudge,
                RefusedRepeatAction.Nudge, RefusedRepeatAction.EscalatedNudge, RefusedRepeatAction.EndTurn
            ],
            actions.Select(static decision => decision.RefusedRepeat));
    }

    [Fact]
    public void RefusedRepeatCount_CoversEveryIdentityOfTheTurn_AndOnlyAnExplicitResetClearsIt()
    {
        var tracker = new TurnStateTracker();
        var first = Prepare(Call("call-a", "sample/a"));
        var second = Prepare(Call("call-b", "sample/b"));
        var both = Prepare(Call("both-a", "sample/a"), Call("both-b", "sample/b"));
        var unrelated = Prepare(Call("call-c", "sample/c"));
        RefuseTwice(tracker, first, "call-a");
        RefuseTwice(tracker, second, "call-b");

        // Repeats 1 to 4. One batch with two repeated calls counts once. Other completed work does not reset the count.
        Assert.Equal(RefusedRepeatAction.Nudge, tracker.EvaluateBeforeDispatch(first).RefusedRepeat);
        Assert.Equal(RefusedRepeatAction.Nudge, tracker.EvaluateBeforeDispatch(both).RefusedRepeat);
        tracker.ObserveCompleted(Complete(unrelated, ("call-c", ToolInvocationOutcomeCategory.Success, "new")));
        Assert.Equal(RefusedRepeatAction.Nudge, tracker.EvaluateBeforeDispatch(second).RefusedRepeat);
        Assert.Equal(RefusedRepeatAction.Nudge, tracker.EvaluateBeforeDispatch(first).RefusedRepeat);
        // A new call is no repeat, so it does not count.
        Assert.Equal(RefusedRepeatAction.None, tracker.EvaluateBeforeDispatch(unrelated).RefusedRepeat);
        Assert.Equal(RefusedRepeatAction.EscalatedNudge, tracker.EvaluateBeforeDispatch(second).RefusedRepeat);

        // ResetForNewTurn clears the recurrence evidence. A child result in the middle of the loop and a system
        // input that joins an automation turn both call it, and neither may clear the count (S1 of the re-review).
        tracker.ResetForNewTurn();
        RefuseTwice(tracker, first, "call-a");
        Assert.Equal(RefusedRepeatAction.EndTurn, tracker.EvaluateBeforeDispatch(first).RefusedRepeat);

        // New person input, and a fresh turn, call the explicit reset. It starts a new count.
        tracker.ResetForNewTurn();
        tracker.ResetRefusedRepeatCount();
        RefuseTwice(tracker, first, "call-a");
        Assert.Equal(RefusedRepeatAction.Nudge, tracker.EvaluateBeforeDispatch(first).RefusedRepeat);

        static void RefuseTwice(TurnStateTracker tracker, PreparedToolCycleBatch batch, string callId)
        {
            var completed = Complete(batch, (callId, ToolInvocationOutcomeCategory.Success, "same"));
            tracker.ObserveCompleted(completed);
            tracker.ObserveCompleted(completed);
            Assert.Equal(RefusedRepeatAction.None, tracker.EvaluateBeforeDispatch(batch).RefusedRepeat);
        }
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
            tracker.EvaluateBeforeDispatch(batch).Kind);

        tracker.ResetForNewTurn();

        Assert.Equal(0, tracker.CompletedCycleHistoryCount);
        Assert.Equal(
            ToolCycleDecisionKind.Execute,
            tracker.EvaluateBeforeDispatch(batch).Kind);
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
        => Complete(batch, results, synthetic: false);

    private static CompletedToolCycleIteration CompleteSynthetic(
        PreparedToolCycleBatch batch,
        params (string CallId, ToolInvocationOutcomeCategory Category, string Text)[] results)
        => Complete(batch, results, synthetic: true);

    private static CompletedToolCycleIteration Complete(
        PreparedToolCycleBatch batch,
        (string CallId, ToolInvocationOutcomeCategory Category, string Text)[] results,
        bool synthetic = false)
        => ToolCycleSignatureFactory.Complete(
            batch,
            results.ToDictionary(
                static result => result.CallId,
                result => new ToolCycleResult(result.Category, result.Text) { Synthetic = synthetic },
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
