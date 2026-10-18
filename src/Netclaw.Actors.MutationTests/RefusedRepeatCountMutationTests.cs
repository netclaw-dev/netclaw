// -----------------------------------------------------------------------
// <copyright file="RefusedRepeatCountMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// Focused mutation target for <c>TurnStateTracker.CountRefusedRepeat</c> (change
/// <c>nudge-repeated-refused-calls</c>, design decision 1). The count of refused repeats in one turn decides
/// when the model gets the plain nudge, the escalated nudge, and the quiet end. A mutant of the increment, the
/// cap, or the two thresholds moves the escalation or the end by one response, or ends the turn at once.
/// Each case drives the public decision of the tracker, so no case depends on an actor or a timer.
/// </summary>
public sealed class RefusedRepeatCountMutationTests
{
    [Fact]
    public void Refused_repeats_get_the_nudge_four_times_then_the_escalation_then_the_end()
    {
        var tracker = RefusedTracker(out var repeated);

        var actions = Enumerable.Range(0, 8).Select(_ => tracker.EvaluateBeforeDispatch(repeated).RefusedRepeat).ToArray();

        // The cap keeps the count at the end value, so a late evaluation still ends the turn.
        Assert.Equal(
        [
            RefusedRepeatAction.Nudge, RefusedRepeatAction.Nudge, RefusedRepeatAction.Nudge,
            RefusedRepeatAction.Nudge, RefusedRepeatAction.EscalatedNudge, RefusedRepeatAction.EndTurn,
            RefusedRepeatAction.EndTurn, RefusedRepeatAction.EndTurn
        ], actions);
    }

    [Fact]
    public void A_batch_with_no_repeat_adds_nothing_to_the_count()
    {
        var tracker = RefusedTracker(out var repeated);
        var unrelated = Prepare(Call("other", "sample/other"));

        for (var repeat = 0; repeat < 4; repeat++)
            Assert.Equal(RefusedRepeatAction.Nudge, tracker.EvaluateBeforeDispatch(repeated).RefusedRepeat);
        // A new call is no repeat. If it counted, the next repeat would end the turn.
        Assert.Equal(RefusedRepeatAction.None, tracker.EvaluateBeforeDispatch(unrelated).RefusedRepeat);
        Assert.Equal(RefusedRepeatAction.EscalatedNudge, tracker.EvaluateBeforeDispatch(repeated).RefusedRepeat);
    }

    [Fact]
    public void One_batch_with_two_repeated_calls_counts_once()
    {
        var tracker = new TurnStateTracker();
        var first = Prepare(Call("a", "sample/a"));
        var second = Prepare(Call("b", "sample/b"));
        var both = Prepare(Call("both-a", "sample/a"), Call("both-b", "sample/b"));
        Refuse(tracker, first, "a");
        Refuse(tracker, second, "b");

        for (var repeat = 0; repeat < 4; repeat++)
            Assert.Equal(RefusedRepeatAction.Nudge, tracker.EvaluateBeforeDispatch(both).RefusedRepeat);
        Assert.Equal(RefusedRepeatAction.EscalatedNudge, tracker.EvaluateBeforeDispatch(both).RefusedRepeat);
    }

    private static TurnStateTracker RefusedTracker(out PreparedToolCycleBatch repeated)
    {
        var tracker = new TurnStateTracker();
        repeated = Prepare(Call("call-a", "sample/a"));
        Refuse(tracker, repeated, "call-a");
        return tracker;
    }

    // Two equal completed rounds, then the first correction. The first correction is no refused repeat.
    private static void Refuse(TurnStateTracker tracker, PreparedToolCycleBatch batch, string callId)
    {
        var completed = ToolCycleSignatureFactory.Complete(
            batch,
            new Dictionary<string, ToolCycleResult>(StringComparer.Ordinal)
            {
                // The default category is Success. The enum is internal to another assembly.
                [callId] = new(default, "same")
            });
        tracker.ObserveCompleted(completed);
        tracker.ObserveCompleted(completed);
        Assert.Equal(RefusedRepeatAction.None, tracker.EvaluateBeforeDispatch(batch).RefusedRepeat);
    }

    private static PreparedToolCycleBatch Prepare(params FunctionCallContent[] calls)
        => ToolCycleSignatureFactory.Prepare(calls, new NoToolExecutor());

    private static FunctionCallContent Call(string callId, string toolName)
        => new(callId, toolName, new Dictionary<string, object?>(StringComparer.Ordinal));

    // Prepare needs an executor only for argument validation, and the default validation accepts every call.
    private sealed class NoToolExecutor : IToolExecutor
    {
        public Task<string> ExecuteAsync(
            FunctionCallContent toolCall, ToolExecutionContext context, CancellationToken ct = default)
            => throw new NotSupportedException("The tracker never executes a call.");
    }
}
