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

    /// <summary>
    /// durable-child-consent-wait, task 2.3 (M10). A parked child keeps its loop guard, so a park does not reset it.
    /// The live tracker gets a value in each field of the loop guard: two counts, six completed cycles after one
    /// eviction, a last blocked action, six recurrence episodes (a corrected one, a pending-job one, and an episode
    /// whose cycle left the history), and a refused-repeat count. A new tracker restores the export.
    /// Planted bugs: the export or the restore drops a field, reorders the cycles, or shares a collection with the
    /// tracker. A resumed child then refuses a different set of calls or escalates at a different repeat.
    /// </summary>
    [Fact]
    public void An_exported_loop_guard_restores_the_same_decisions()
    {
        // Arrange: the live tracker of a child before its park.
        var live = new TurnStateTracker();
        var e = Prepare(Call("e1", "sample/e"));
        var d = Prepare(Call("d1", "sample/d"));
        var b = Prepare(Call("b1", "sample/b"));
        var j = Prepare(Call("j1", "check_background_job", ("job_id", "job-1")));
        var c = Prepare(Call("c1", "sample/c"));
        var a = Prepare(Call("a1", "sample/a"));
        var completedE = Complete(e, ("e1", ToolInvocationOutcomeCategory.Success, "out-e"));
        var completedD = Complete(d, ("d1", ToolInvocationOutcomeCategory.Success, "out-d"));
        var completedB = Complete(b, ("b1", ToolInvocationOutcomeCategory.Success, "out-b"));
        var completedJ = ToolCycleSignatureFactory.Complete(j,
            new Dictionary<string, ToolCycleResult>(StringComparer.Ordinal)
            {
                ["j1"] = new(ToolInvocationOutcomeCategory.Success, "Job job-1: pending") { PendingJob = true }
            });
        var completedC = Complete(c, ("c1", ToolInvocationOutcomeCategory.Success, "out-c"));
        var completedA = Complete(a, ("a1", ToolInvocationOutcomeCategory.NotFound, "out-a"));
        foreach (var completed in new[] { completedE, completedD, completedB, completedJ, completedC, completedC, completedA, completedA })
        {
            live.ObserveCompleted(completed);
            live.RecordToolCompletion(resultCount: 1);
        }

        live.RecordToolCompletion(resultCount: 3);
        Assert.Equal(new DecisionShape(ToolCycleDecisionKind.Correct, "a1", "", "", RefusedRepeatAction.None),
            Shape(live.EvaluateBeforeDispatch(a)));
        Assert.Equal(RefusedRepeatAction.Nudge, live.EvaluateBeforeDispatch(a).RefusedRepeat);
        Assert.Equal(RefusedRepeatAction.Nudge, live.EvaluateBeforeDispatch(a).RefusedRepeat);

        // The cycle of E left the history (six entries at most). The pending job is not in the history.
        var expected = new TurnLoopGuardState(
            ToolIterationCount: 9,
            ToolCallCount: 11,
            CompletedCycles:
            [
                (d.Action, completedD.OutcomeValue), (b.Action, completedB.OutcomeValue),
                (c.Action, completedC.OutcomeValue), (c.Action, completedC.OutcomeValue),
                (a.Action, completedA.OutcomeValue), (a.Action, completedA.OutcomeValue)
            ],
            LastBlockedAction: a.Action,
            Episodes: new Dictionary<ToolRecurrenceKey, ToolRecurrenceEpisode>
            {
                [Key(e)] = new(Outcome(completedE), EqualRounds: 1, Corrected: false, PendingJob: false),
                [Key(d)] = new(Outcome(completedD), EqualRounds: 1, Corrected: false, PendingJob: false),
                [Key(b)] = new(Outcome(completedB), EqualRounds: 1, Corrected: false, PendingJob: false),
                [Key(j)] = new(Outcome(completedJ), EqualRounds: 0, Corrected: false, PendingJob: true),
                [Key(c)] = new(Outcome(completedC), EqualRounds: 2, Corrected: false, PendingJob: false),
                [Key(a)] = new(Outcome(completedA), EqualRounds: 2, Corrected: true, PendingJob: false)
            },
            RefusedRepeatCount: 2, CallsSinceProgress: 2);

        // Act
        var exported = live.ExportLoopGuard();
        var resumed = new TurnStateTracker();
        resumed.RestoreLoopGuard(exported);

        // Assert 1: the export holds each field, and the restore gives the same export back.
        AssertSameLoopGuard(expected, exported);
        AssertSameLoopGuard(expected, resumed.ExportLoopGuard());
        Assert.Equal(live.ToolIterationCount, resumed.ToolIterationCount);
        Assert.Equal(live.ToolCallCount, resumed.ToolCallCount);
        Assert.Equal(live.CompletedCycleHistoryCount, resumed.CompletedCycleHistoryCount);

        // Assert 2: both trackers make the same decisions, step by step.
        // The last blocked action makes the adjacent repeat a repeated correction.
        AssertBoth(new ToolCycleDecision(ToolCycleDecisionKind.Correct, Repeated: true),
            tracker => tracker.EvaluateAdjacentBeforeDispatch(a.Action));
        // The corrected episode refuses A again as a repeat. It is the third refused repeat.
        AssertBoth(new DecisionShape(ToolCycleDecisionKind.Correct, "a1", "a1", "", RefusedRepeatAction.Nudge),
            tracker => Shape(tracker.EvaluateBeforeDispatch(a)));
        // Two equal rounds of C refuse C. No adjacent cycle ends in C.
        AssertBoth(new DecisionShape(ToolCycleDecisionKind.Correct, "c1", "", "", RefusedRepeatAction.None),
            tracker => Shape(tracker.EvaluateBeforeDispatch(c)));
        // A pending background job is exempt.
        AssertBoth(new DecisionShape(ToolCycleDecisionKind.Execute, "", "", "j1", RefusedRepeatAction.None),
            tracker => Shape(tracker.EvaluateBeforeDispatch(j)));
        // One completed round of B refuses nothing.
        AssertBoth(new DecisionShape(ToolCycleDecisionKind.Execute, "", "", "", RefusedRepeatAction.None),
            tracker => Shape(tracker.EvaluateBeforeDispatch(b)));
        // The restored refused-repeat count decides the escalation and the end of the loop.
        AssertBoth(RefusedRepeatAction.Nudge, tracker => tracker.EvaluateBeforeDispatch(a).RefusedRepeat);
        AssertBoth(RefusedRepeatAction.EscalatedNudge, tracker => tracker.EvaluateBeforeDispatch(a).RefusedRepeat);
        AssertBoth(RefusedRepeatAction.EndTurn, tracker => tracker.EvaluateBeforeDispatch(a).RefusedRepeat);
        // The restored iteration count keeps the post-tool nudge for an empty reply.
        Assert.Equal(
            Assert.IsType<EmptyResponseAction.Retry>(live.EvaluateEmptyResponse(LlmResponseKind.Empty, truncated: false)),
            Assert.IsType<EmptyResponseAction.Retry>(resumed.EvaluateEmptyResponse(LlmResponseKind.Empty, truncated: false)));

        // Assert 3: the decisions above changed both trackers, but not the exported copy.
        AssertSameLoopGuard(expected, exported);

        void AssertBoth<T>(T expectedDecision, Func<TurnStateTracker, T> decide)
        {
            Assert.Equal(expectedDecision, decide(live));
            Assert.Equal(expectedDecision, decide(resumed));
        }

        static ToolRecurrenceKey Key(PreparedToolCycleBatch batch)
            => new(batch.Calls[0].ToolName, batch.Calls[0].ArgumentsHash);

        static string Outcome(CompletedToolCycleIteration completed) => Assert.Single(completed.Groups).Outcome;
    }

    // Record equality compares a list by reference. Compare each field, and each collection member by value.
    private static void AssertSameLoopGuard(TurnLoopGuardState expected, TurnLoopGuardState actual)
    {
        Assert.Equal(expected.ToolIterationCount, actual.ToolIterationCount);
        Assert.Equal(expected.ToolCallCount, actual.ToolCallCount);
        Assert.Equal(expected.CompletedCycles.ToArray(), actual.CompletedCycles.ToArray());
        Assert.Equal(expected.LastBlockedAction, actual.LastBlockedAction);
        Assert.Equal(Entries(expected.Episodes), Entries(actual.Episodes));
        Assert.Equal(expected.RefusedRepeatCount, actual.RefusedRepeatCount);
        Assert.Equal(expected.CallsSinceProgress, actual.CallsSinceProgress);
        Assert.Equal(expected.IncompleteBatchHadSuccess, actual.IncompleteBatchHadSuccess);

        static (string Tool, string ArgumentsHash, ToolRecurrenceEpisode Episode)[] Entries(
            IReadOnlyDictionary<ToolRecurrenceKey, ToolRecurrenceEpisode> episodes)
            => episodes
                .Select(static entry => (Tool: entry.Key.ToolName.Value, entry.Key.ArgumentsHash, Episode: entry.Value))
                .OrderBy(static entry => entry.Tool, StringComparer.Ordinal)
                .ThenBy(static entry => entry.ArgumentsHash, StringComparer.Ordinal)
                .ToArray();
    }

    private sealed record DecisionShape(
        ToolCycleDecisionKind Kind,
        string Refused,
        string Repeated,
        string Exempt,
        RefusedRepeatAction RefusedRepeat);

    // ── No-progress guard: the progress axis counts outcomes, not action identity ──

    [Fact]
    public void Identical_failures_across_different_tools_trip_the_progress_guard()
    {
        // The reported incident: rotating tools, identical error, no action repeats. The cycle guard cannot
        // see it because every action identity differs. The progress axis must.
        var tracker = new TurnStateTracker();
        var tools = new[] { "sample/a", "sample/b", "sample/c", "sample/d" };
        for (var i = 0; i < tools.Length; i++)
        {
            var batch = Prepare(Call($"call-{i}", tools[i]));
            Assert.Equal(RefusedRepeatAction.None, tracker.EvaluateBeforeDispatch(batch).Progress);
            tracker.ObserveCompleted(Complete(batch,
                ($"call-{i}", ToolInvocationOutcomeCategory.InvalidInput, "Error: the same failure.")));
        }

        var next = Prepare(Call("call-next", "sample/e"));
        Assert.Equal(RefusedRepeatAction.Nudge, tracker.EvaluateBeforeDispatch(next).Progress);
    }

    [Fact]
    public void A_success_resets_the_progress_counter()
    {
        var tracker = new TurnStateTracker();
        for (var i = 0; i < 3; i++)
        {
            var batch = Prepare(Call($"fail-{i}", "sample/a"));
            tracker.ObserveCompleted(Complete(batch,
                ($"fail-{i}", ToolInvocationOutcomeCategory.InvalidInput, "Error: no.")));
        }

        var ok = Prepare(Call("ok", "sample/b"));
        tracker.ObserveCompleted(Complete(ok, ("ok", ToolInvocationOutcomeCategory.Success, "done")));

        // Three failures then a success: the counter is back to zero, so three more failures stay quiet.
        for (var i = 0; i < 3; i++)
        {
            var batch = Prepare(Call($"again-{i}", "sample/c"));
            Assert.Equal(RefusedRepeatAction.None, tracker.EvaluateBeforeDispatch(batch).Progress);
            tracker.ObserveCompleted(Complete(batch,
                ($"again-{i}", ToolInvocationOutcomeCategory.InvalidInput, "Error: no.")));
        }
    }

    [Fact]
    public void The_progress_guard_escalates_then_ends_the_turn()
    {
        // The guard evaluates before dispatch, so the counter holds completed failures. After N failures the
        // next evaluation reads N. The first nudge therefore appears on the call after the Nth failure.
        var tracker = new TurnStateTracker();
        var seen = new List<RefusedRepeatAction>();
        for (var i = 0; i <= TurnStateTracker.ProgressEndTurnAt; i++)
        {
            var batch = Prepare(Call($"call-{i}", "sample/a"));
            seen.Add(tracker.EvaluateBeforeDispatch(batch).Progress);
            tracker.ObserveCompleted(Complete(batch,
                ($"call-{i}", ToolInvocationOutcomeCategory.InvalidInput, "Error: no.")));
        }

        Assert.Equal(RefusedRepeatAction.None, seen[0]);
        Assert.Equal(RefusedRepeatAction.None, seen[TurnStateTracker.ProgressNudgeAt - 1]);
        Assert.Equal(RefusedRepeatAction.Nudge, seen[TurnStateTracker.ProgressNudgeAt]);
        Assert.Equal(RefusedRepeatAction.EscalatedNudge, seen[TurnStateTracker.ProgressEscalateAt]);
        Assert.Equal(RefusedRepeatAction.EndTurn, seen[TurnStateTracker.ProgressEndTurnAt]);
    }

    [Fact]
    public void A_pending_background_job_is_not_a_failure()
    {
        // Counting a pending job as a failure would punish the model for doing what the repeat nudge asks.
        var tracker = new TurnStateTracker();
        for (var i = 0; i < TurnStateTracker.ProgressEndTurnAt; i++)
        {
            var batch = Prepare(Call($"job-{i}", "check_background_job"));
            tracker.ObserveCompleted(ToolCycleSignatureFactory.Complete(batch,
                new Dictionary<string, ToolCycleResult>(StringComparer.Ordinal)
                {
                    [$"job-{i}"] = new(ToolInvocationOutcomeCategory.Success, "running") { PendingJob = true }
                }));
        }

        var next = Prepare(Call("next", "sample/a"));
        Assert.Equal(RefusedRepeatAction.None, tracker.EvaluateBeforeDispatch(next).Progress);
    }

    [Fact]
    public void A_synthetic_result_does_not_move_the_progress_counter()
    {
        // A synthetic correction is the guard's own output. Counting it would make the guard trip itself.
        var tracker = new TurnStateTracker();
        for (var i = 0; i < TurnStateTracker.ProgressEndTurnAt; i++)
        {
            var batch = Prepare(Call($"call-{i}", "sample/a"));
            tracker.ObserveCompleted(CompleteSynthetic(batch,
                ($"call-{i}", ToolInvocationOutcomeCategory.RecoverableCorrection, "Corrected.")));
        }

        var next = Prepare(Call("next", "sample/b"));
        Assert.Equal(RefusedRepeatAction.None, tracker.EvaluateBeforeDispatch(next).Progress);
    }

    [Fact]
    public void The_progress_counter_survives_an_export_and_restore()
    {
        var tracker = new TurnStateTracker();
        for (var i = 0; i < TurnStateTracker.ProgressNudgeAt; i++)
        {
            var batch = Prepare(Call($"call-{i}", "sample/a"));
            tracker.ObserveCompleted(Complete(batch,
                ($"call-{i}", ToolInvocationOutcomeCategory.InvalidInput, "Error: no.")));
        }

        var restored = new TurnStateTracker();
        restored.RestoreLoopGuard(tracker.ExportLoopGuard());

        var next = Prepare(Call("next", "sample/b"));
        Assert.Equal(RefusedRepeatAction.Nudge, restored.EvaluateBeforeDispatch(next).Progress);
    }

    [Fact]
    public void A_new_turn_resets_the_progress_counter()
    {
        var tracker = new TurnStateTracker();
        for (var i = 0; i < TurnStateTracker.ProgressNudgeAt; i++)
        {
            var batch = Prepare(Call($"call-{i}", "sample/a"));
            tracker.ObserveCompleted(Complete(batch,
                ($"call-{i}", ToolInvocationOutcomeCategory.InvalidInput, "Error: no.")));
        }

        tracker.ResetForNewTurn();

        var next = Prepare(Call("next", "sample/b"));
        Assert.Equal(RefusedRepeatAction.None, tracker.EvaluateBeforeDispatch(next).Progress);
    }

    [Fact]
    public void Pending_and_synthetic_successes_do_not_reset_actual_failures()
    {
        var tracker = new TurnStateTracker();
        var batch = Prepare(Call("failed", "sample/a"), Call("pending", "check_background_job"),
            Call("synthetic", "sample/b"));
        var completed = ToolCycleSignatureFactory.Complete(batch,
            new Dictionary<string, ToolCycleResult>(StringComparer.Ordinal)
            {
                ["failed"] = new(ToolInvocationOutcomeCategory.InvalidInput, "Error."),
                ["pending"] = new(ToolInvocationOutcomeCategory.Success, "pending") { PendingJob = true },
                ["synthetic"] = new(ToolInvocationOutcomeCategory.Success, "synthetic") { Synthetic = true }
            });

        Assert.False(completed.AnySuccess);
        Assert.Equal(1, completed.FailureCount);
        Assert.False(completed.RecordAdjacent);
        for (var i = 0; i < TurnStateTracker.ProgressNudgeAt; i++)
            tracker.ObserveCompleted(completed);

        Assert.Equal(4, tracker.CallsSinceProgress);
        Assert.Equal(RefusedRepeatAction.Nudge, tracker.EvaluateProgress());
    }

    [Fact]
    public void Mixed_actual_outcomes_reset_progress_regardless_of_call_order()
    {
        foreach (var reverse in new[] { false, true })
        {
            var tracker = new TurnStateTracker();
            tracker.ObserveProgress([new(ToolInvocationOutcomeCategory.InvalidInput, "Error.")], batchComplete: true);
            FunctionCallContent[] calls = [Call("failed", "sample/a"), Call("ok", "sample/b"), Call("failed-again", "sample/c")];
            var batch = Prepare(reverse ? calls.Reverse().ToArray() : calls);
            var completed = Complete(batch,
                ("failed", ToolInvocationOutcomeCategory.InvalidInput, "Error."),
                ("ok", ToolInvocationOutcomeCategory.Success, "Done."),
                ("failed-again", ToolInvocationOutcomeCategory.NotFound, "Missing."));

            Assert.True(completed.AnySuccess);
            Assert.Equal(2, completed.FailureCount);
            tracker.ObserveCompleted(completed);
            Assert.Equal(0, tracker.CallsSinceProgress);
        }
    }

    [Fact]
    public void Progress_without_cycle_history_preserves_neutral_results_and_counts_actual_outcomes()
    {
        var tracker = new TurnStateTracker();
        tracker.ObserveProgress([new(ToolInvocationOutcomeCategory.InvalidInput, "Error.")], batchComplete: true);
        tracker.ObserveProgress([
            new(ToolInvocationOutcomeCategory.Success, "pending") { PendingJob = true },
            new(ToolInvocationOutcomeCategory.RecoverableCorrection, "synthetic") { Synthetic = true }
        ], batchComplete: true);
        Assert.Equal(1, tracker.CallsSinceProgress);
        tracker.ObserveProgress([new(ToolInvocationOutcomeCategory.NotFound, "Missing.")], batchComplete: true);
        Assert.Equal(2, tracker.CallsSinceProgress);
        tracker.ObserveProgress([new(ToolInvocationOutcomeCategory.Success, "Done.")], batchComplete: true);
        Assert.Equal(0, tracker.CallsSinceProgress);
        Assert.Equal(0, tracker.CompletedCycleHistoryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Progress_escalation_replaces_conflicting_repeat_advice(bool childRun)
    {
        var repeat = childRun ? ToolCycleMessages.ChildRepeatNudge(["sample/a"])
            : ToolCycleMessages.RepeatNudge(["sample/a"]);
        var nudge = ToolCycleMessages.AfterResults(repeat, RefusedRepeatAction.EscalatedNudge, 6, childRun);
        Assert.Contains("End your turn now", nudge, StringComparison.Ordinal);
        Assert.DoesNotContain("Select a different action", nudge, StringComparison.Ordinal);
        Assert.Equal(repeat, ToolCycleMessages.AfterResults(repeat, RefusedRepeatAction.Nudge, 4, childRun));
        if (childRun)
            Assert.Contains("Give your result to the parent", nudge, StringComparison.Ordinal);
    }

    [Fact]
    public void A_success_before_a_park_wins_over_failures_until_the_feedback_batch_completes()
    {
        var tracker = new TurnStateTracker();
        tracker.ObserveProgress([new(ToolInvocationOutcomeCategory.Success, "Done.")], batchComplete: false);
        var restored = new TurnStateTracker();
        restored.RestoreLoopGuard(tracker.ExportLoopGuard());
        restored.ObserveProgress([new(ToolInvocationOutcomeCategory.InvalidInput, "Error.")], batchComplete: false);
        restored.ObserveProgress([new(ToolInvocationOutcomeCategory.NotFound, "Missing.")], batchComplete: true);
        Assert.Equal(0, restored.CallsSinceProgress);
        Assert.False(restored.ExportLoopGuard().IncompleteBatchHadSuccess);
        restored.ObserveProgress([new(ToolInvocationOutcomeCategory.NotFound, "Missing.")], batchComplete: true);
        Assert.Equal(1, restored.CallsSinceProgress);
    }

    private static DecisionShape Shape(ToolCycleBatchDecision decision)
        => new(decision.Kind,
            string.Join(',', decision.RefusedCallIds.Order(StringComparer.Ordinal)),
            string.Join(',', decision.RepeatedCallIds.Order(StringComparer.Ordinal)),
            string.Join(',', decision.ExemptCallIds.Order(StringComparer.Ordinal)),
            decision.RefusedRepeat);

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
