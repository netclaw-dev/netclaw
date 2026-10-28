// -----------------------------------------------------------------------
// <copyright file="LoopFeedbackTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class LoopFeedbackTests
{
    public static TheoryData<string, int> CorrectedCases => new()
    {
        { "invalid_rationale", 3 }, { "invalid_rationale", 4 },
        { "args_parse_error", 3 }, { "args_parse_error", 4 },
        { "unrecognized_argument", 3 }, { "unrecognized_argument", 4 },
        { "invalid_meta_value", 3 }, { "invalid_meta_value", 4 }
    };

    [Theory]
    [MemberData(nameof(CorrectedCases))]
    public void A_success_retires_active_cause_advice_but_keeps_recent_evidence(string code, int failures)
    {
        var tracker = new TurnStateTracker();
        for (var i = 0; i < failures; i++)
        {
            tracker.ObserveProgress([Failure(code)], true);
            Deliver(tracker);
        }
        tracker.ObserveProgress([new(ToolInvocationOutcomeCategory.Success, "corrected")], true);
        Assert.Equal(RefusedRepeatAction.None, tracker.EvaluateProgress());
        Assert.Null(Deliver(tracker));
        tracker.ObserveProgress([Failure(code)], true);
        Assert.Equal(RefusedRepeatAction.Nudge, tracker.EvaluateProgress());
    }

    [Theory]
    [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
    [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
    public void A_consent_split_keeps_the_same_cause_decision(int beforePark, bool restore)
    {
        var tracker = new TurnStateTracker();
        tracker.ObserveProgress(Enumerable.Repeat(Failure("invalid_rationale"), beforePark), false);
        Assert.Equal(RefusedRepeatAction.None, tracker.EvaluateProgress());
        if (restore)
        {
            var resumed = new TurnStateTracker();
            resumed.RestoreLoopGuard(tracker.ExportLoopGuard());
            tracker = resumed;
        }
        tracker.ObserveProgress(Enumerable.Repeat(Failure("invalid_rationale"), 3 - beforePark), true);
        Assert.Equal(RefusedRepeatAction.Nudge, tracker.EvaluateProgress());
        Assert.Equal(LoopInterventionReason.FailureCauseRecurrence, tracker.EvaluateProgressReason());
        Assert.Equal(3, tracker.CallsSinceProgress);
    }

    [Theory]
    [InlineData("invalid_rationale")] [InlineData("args_parse_error")]
    [InlineData("unrecognized_argument")] [InlineData("invalid_meta_value")]
    public void Old_cause_evidence_expires_after_six_feedback_batches(string code)
    {
        var tracker = new TurnStateTracker();
        tracker.ObserveProgress([Failure(code), Failure(code)], true);
        for (var i = 0; i < 6; i++)
            tracker.ObserveProgress([new(ToolInvocationOutcomeCategory.Success, "distinct useful work")], true);
        tracker.ObserveProgress([Failure(code)], true);
        Assert.Equal(RefusedRepeatAction.None, tracker.EvaluateProgress());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void An_escalated_repeat_warning_wins_over_a_plain_cause_nudge(bool child)
    {
        var repeat = child ? ToolCycleMessages.EscalatedChildRepeatNudge(["tool"])
            : ToolCycleMessages.EscalatedRepeatNudge(["tool"]);
        var cause = new ProgressDecision(RefusedRepeatAction.Nudge,
            LoopInterventionReason.FailureCauseRecurrence, new ToolFailureKey("netclaw", "invalid_rationale"));
        Assert.Equal(repeat, ToolCycleMessages.AfterResults((RefusedRepeatAction.EscalatedNudge, repeat), cause, 3, child).Text);
    }

    [Theory]
    [InlineData("invalid_rationale", "args_parse_error")]
    [InlineData("args_parse_error", "invalid_rationale")]
    public void A_cause_without_delivered_advice_starts_at_the_first_nudge(string first, string second)
    {
        var tracker = new TurnStateTracker();
        tracker.ObserveProgress([Failure(first), Failure(first), Failure(first),
            Failure(second), Failure(second), Failure(second),
            new(ToolInvocationOutcomeCategory.Success, "useful sibling")], true);
        Assert.Equal(first, tracker.EvaluateActiveCause()?.Code);
        Assert.Contains(first, Deliver(tracker), StringComparison.Ordinal);
        tracker.ObserveProgress([Failure(second)], true);
        Assert.Equal(second, tracker.EvaluateActiveCause()?.Code);
        Assert.Equal(RefusedRepeatAction.Nudge, tracker.EvaluateProgress());
    }

    public static TheoryData<int, bool, bool> DeliveryCases => new()
    {
        { 3, false, false }, { 4, false, false }, { 5, false, false },
        { 3, true, false }, { 4, true, false }, { 5, true, false },
        { 3, false, true }, { 4, false, true }, { 5, false, true },
        { 3, true, true }, { 4, true, true }, { 5, true, true }
    };

    [Theory]
    [MemberData(nameof(DeliveryCases))]
    public void Only_delivered_advice_advances_and_repeated_evaluation_is_stable(int failures, bool deliver, bool restore)
    {
        var tracker = new TurnStateTracker();
        for (var i = 0; i < failures; i++)
        {
            tracker.ObserveProgress([Failure("invalid_rationale"), new(ToolInvocationOutcomeCategory.Success, "sibling")], true);
            var expected = !deliver || i < 3 ? RefusedRepeatAction.Nudge
                : i == 3 ? RefusedRepeatAction.EscalatedNudge : RefusedRepeatAction.EndTurn;
            if (i < 2)
                expected = RefusedRepeatAction.None;
            Assert.Equal(expected, tracker.EvaluateProgress());
            if (deliver)
                Deliver(tracker);
            var decision = tracker.EvaluateProgressDecision();
            Assert.Equal(decision, tracker.EvaluateProgressDecision());
            if (restore)
            {
                var resumed = new TurnStateTracker();
                resumed.RestoreLoopGuard(tracker.ExportLoopGuard());
                Assert.Equal(decision, resumed.EvaluateProgressDecision());
                tracker = resumed;
            }
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void A_changed_cause_receives_its_own_first_advice(int repeats)
    {
        var tracker = new TurnStateTracker();
        tracker.ObserveProgress(Enumerable.Repeat(Failure("invalid_rationale"), 3), true);
        Deliver(tracker);
        tracker.ObserveProgress(Enumerable.Repeat(Failure("args_parse_error"), repeats)
            .Append(new ToolCycleResult(ToolInvocationOutcomeCategory.Success, "sibling")), true);
        Assert.Equal(repeats == 3 ? RefusedRepeatAction.Nudge : RefusedRepeatAction.None, tracker.EvaluateProgress());
        Assert.NotEqual("invalid_rationale", tracker.EvaluateActiveCause()?.Code);
    }

    [Theory]
    [InlineData("history-overflow")] [InlineData("unknown-advice")]
    [InlineData("unmatched-cause")] [InlineData("empty-source")]
    public void Damaged_cause_state_cannot_resume(string damage)
    {
        var tracker = new TurnStateTracker();
        tracker.ObserveProgress(Enumerable.Repeat(Failure("invalid_rationale"), 3), true);
        Deliver(tracker);
        var state = tracker.ExportLoopGuard();
        var batch = state.CauseBatches[0];
        state = damage switch
        {
            "history-overflow" => state with { CauseBatches = Enumerable.Repeat(batch, 7).ToArray() },
            "unknown-advice" => state with { CauseBatches = [batch with { Advice = batch.Advice with { Stage = (RefusedRepeatAction)99 } }] },
            "unmatched-cause" => state with { CauseBatches = [batch with { Keys = [] }] },
            "empty-source" => state with { IncompleteFailureKeys = [new ToolFailureKey("", "invalid_rationale")] },
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        };
        Assert.Equal("the cause evidence is invalid", ChildParkRules.LoopGuardDamage(state));
        Assert.Throws<ArgumentException>(() => new TurnStateTracker().RestoreLoopGuard(state));
    }

    [Theory]
    [InlineData(1, 1, true, true)] [InlineData(2, 1, true, true)]
    [InlineData(1, 2, true, false)] [InlineData(2, 2, true, true)]
    [InlineData(1, 2, false, false)] [InlineData(2, 1, false, true)]
    public void Advice_selection_compares_severity_before_detector_priority(
        int repeatStage, int progressStage, bool cause, bool repeatWins)
    {
        var progress = new ProgressDecision((RefusedRepeatAction)progressStage,
            cause ? LoopInterventionReason.FailureCauseRecurrence : LoopInterventionReason.FailureBudget,
            cause ? new ToolFailureKey("netclaw", "invalid_rationale") : null);
        var advice = ToolCycleMessages.AfterResults(((RefusedRepeatAction)repeatStage, "repeat advice"), progress, 4, false);
        Assert.Equal(repeatWins ? LoopInterventionReason.ActionRecurrence : progress.Reason, advice.Decision.Reason);
        Assert.Equal(repeatWins, advice.Text == "repeat advice");
        Assert.Equal((RefusedRepeatAction)Math.Max(repeatStage, progressStage), advice.Decision.Stage);
    }

    private static string? Deliver(TurnStateTracker tracker)
        => tracker.CreateNudgeAfterResults(null, false);

    private static ToolCycleResult Failure(string code)
        => new(ToolInvocationOutcomeCategory.InvalidInput, "rejected")
        { FailureKey = new ToolFailureKey("netclaw", code) };
}
