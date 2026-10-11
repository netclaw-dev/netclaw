// -----------------------------------------------------------------------
// <copyright file="ToolRecurrenceContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ToolRecurrenceContractTests(ITestOutputHelper output)
{
    private static readonly FakeToolExecutor Executor = new();

    [Fact]
    public void A_million_fixed_seed_duplicate_and_noise_sequences_preserve_exact_boundaries()
    {
        const int sequences = 1_000_000;
        const int seed = 0x61_24_08;
        var random = new Random(seed);
        var repeated = Enumerable.Range(1, 5).Select(count => Prepare("probe", 0, count)).ToArray();
        var completed = repeated.Select(batch => Complete(batch, "same actual result")).ToArray();
        var noise = Enumerable.Range(1, 3).Select(index => Complete(Prepare("diagnostic", index), "diagnostic result")).ToArray();
        var mixed = PrepareMixed("probe", 0, "unrelated", 99);

        for (var sequence = 0; sequence < sequences; sequence++)
        {
            var tracker = new TurnStateTracker();
            var first = random.Next(repeated.Length);
            var second = random.Next(repeated.Length);
            Require(tracker.EvaluateBeforeDispatch(repeated[first]).Kind == ToolCycleDecisionKind.Execute,
                sequence, "The first round must execute.");
            tracker.ObserveCompleted(completed[first]);
            if (random.Next(2) == 1)
                tracker.ObserveCompleted(noise[0]);
            Require(tracker.EvaluateBeforeDispatch(repeated[second]).Kind == ToolCycleDecisionKind.Execute,
                sequence, "Duplicate members cannot establish a second feedback round.");
            tracker.ObserveCompleted(completed[second]);
            if (random.Next(2) == 1)
                tracker.ObserveCompleted(noise[1]);

            var correction = tracker.EvaluateBeforeDispatch(mixed);
            Require(correction.Kind == ToolCycleDecisionKind.Correct, sequence, "Two completed rounds require correction.");
            Require(correction.RefusedCallIds.SetEquals(["first"]), sequence, "Only the matched call can be refused.");
            Require(correction.ExemptCallIds.Count == 0, sequence, "Ordinary results cannot grant a repeat exception.");
            if (random.Next(2) == 1)
                tracker.ObserveCompleted(noise[2]);
            Require(tracker.EvaluateBeforeDispatch(mixed).Kind == ToolCycleDecisionKind.Stop,
                sequence, "Unrelated work cannot clear corrective feedback.");
        }

        output.WriteLine($"Executed {sequences} deterministic sequences with seed {seed}.");
    }

    [Fact]
    public void Ten_thousand_live_sequences_preserve_outcome_changes_and_corrective_feedback()
    {
        const int sequences = 10_000;
        const int seed = 0x51_24_08;
        var random = new Random(seed);
        var single = Prepare("probe", 0);
        var duplicate = Prepare("probe", 0, 2);
        var mixed = PrepareMixed("probe", 0, "unrelated", 99);
        var noise = Complete(Prepare("diagnostic", 1), "diagnostic result");

        for (var sequence = 0; sequence < sequences; sequence++)
        {
            var tracker = new TurnStateTracker();
            var changed = random.Next(2) == 0
                ? Complete(single, "old result")
                : ToolCycleSignatureFactory.Complete(duplicate, new Dictionary<string, ToolCycleResult>
                {
                    ["call-0"] = new(ToolInvocationOutcomeCategory.Success, "new result"),
                    ["call-1"] = new(ToolInvocationOutcomeCategory.TransientFailure, "new result")
                });
            tracker.ObserveCompleted(changed);
            tracker.ObserveCompleted(Complete(single, "new result"));
            Require(tracker.EvaluateBeforeDispatch(single).Kind == ToolCycleDecisionKind.Execute,
                sequence, "A distinct actual outcome must begin a new episode.");
            tracker.ObserveCompleted(Complete(single, "new result"));
            tracker.ObserveCompleted(noise);

            Require(tracker.EvaluateBeforeDispatch(mixed).Kind == ToolCycleDecisionKind.Correct,
                sequence, "Live recurrence evidence must produce corrective feedback.");
            tracker.ObserveCompleted(noise);
            Require(tracker.EvaluateBeforeDispatch(mixed).Kind == ToolCycleDecisionKind.Stop,
                sequence, "Unrelated work must not clear unresolved corrective feedback.");
            tracker.ResetForNewTurn();
            Require(tracker.EvaluateBeforeDispatch(mixed).Kind == ToolCycleDecisionKind.Execute,
                sequence, "A fresh authorized task must reset its prior evidence.");
        }

        output.WriteLine($"Executed {sequences} live recurrence sequences with seed {seed}.");
    }

    [Fact]
    public void Cold_horizon_evicts_only_first_observations_and_never_established_or_corrected_evidence()
    {
        var tracker = new TurnStateTracker();
        var pinned = Prepare("probe", -1);
        var oldest = Prepare("cold", 0);
        tracker.ObserveCompleted(Complete(pinned, "same"));
        tracker.ObserveCompleted(Complete(pinned, "same"));
        tracker.ObserveCompleted(Complete(oldest, "cold"));
        for (var index = 1; index <= 256; index++)
            tracker.ObserveCompleted(Complete(Prepare("cold", index), "cold"));

        Assert.Equal(ToolCycleDecisionKind.Execute, tracker.EvaluateBeforeDispatch(oldest).Kind);
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(pinned).Kind);
        for (var index = 257; index <= 600; index++)
            tracker.ObserveCompleted(Complete(Prepare("cold", index), "cold"));
        Assert.Equal(ToolCycleDecisionKind.Stop, tracker.EvaluateBeforeDispatch(pinned).Kind);
    }

    [Fact]
    public void Missing_or_foreign_result_identity_cannot_change_retained_evidence()
    {
        var tracker = new TurnStateTracker();
        var batch = Prepare("probe", 0, 2);
        tracker.ObserveCompleted(Complete(batch, "same"));
        tracker.ObserveCompleted(Complete(batch, "same"));
        Assert.Throws<InvalidOperationException>(() => ToolCycleSignatureFactory.Complete(batch,
            new Dictionary<string, ToolCycleResult> { ["call-0"] = new(ToolInvocationOutcomeCategory.Success, "same") }));
        Assert.Throws<InvalidOperationException>(() => ToolCycleSignatureFactory.Complete(batch,
            new Dictionary<string, ToolCycleResult>
            {
                ["call-0"] = new(ToolInvocationOutcomeCategory.Success, "same"),
                ["foreign"] = new(ToolInvocationOutcomeCategory.Success, "same")
            }));
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(batch).Kind);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("Cancel")]
    public void A_cancel_query_cannot_receive_an_exception_from_a_forged_pending_fact(string cancelKey)
    {
        var call = new FunctionCallContent("call-0", "check_background_job", new Dictionary<string, object?>
        {
            ["JobId"] = "synthetic-job", [cancelKey] = true, ["_rationale"] = "Cancel the specified job."
        });
        var batch = ToolCycleSignatureFactory.Prepare([call], Executor);
        var completed = ToolCycleSignatureFactory.Complete(batch, new Dictionary<string, ToolCycleResult>
        {
            ["call-0"] = new(ToolInvocationOutcomeCategory.Success, "running") { PendingJob = true }
        });
        var tracker = new TurnStateTracker();
        tracker.ObserveCompleted(completed);
        tracker.ObserveCompleted(completed);

        var decision = tracker.EvaluateBeforeDispatch(batch);
        Assert.Equal(ToolCycleDecisionKind.Correct, decision.Kind);
        Assert.Empty(decision.ExemptCallIds);
    }

    private static ToolInvocationReceipt Receipt(ToolInvocationOutcomeCategory category) => category switch
    {
        ToolInvocationOutcomeCategory.Success => new ToolInvocationReceipt.Succeeded([], null),
        ToolInvocationOutcomeCategory.RecoverableCorrection => new ToolInvocationReceipt.Correction(ToolRemediationCode.BreakToolCycle),
        _ => new ToolInvocationReceipt.OtherOutcome(category)
    };

    [Fact]
    public void A_pending_fact_exempts_only_its_exact_identity_and_cannot_exempt_another_loop()
    {
        var tracker = new TurnStateTracker();
        var pending = Prepare("check_background_job", 0);
        var pendingResult = ToolCycleSignatureFactory.Complete(pending, new Dictionary<string, ToolCycleResult>
        {
            ["call-0"] = new(ToolInvocationOutcomeCategory.Success, "running") { PendingJob = true }
        });
        var ordinary = Prepare("probe", 0);
        tracker.ObserveCompleted(Complete(ordinary, "unchanged"));
        tracker.ObserveCompleted(Complete(ordinary, "unchanged"));
        tracker.ObserveCompleted(pendingResult);
        tracker.ObserveCompleted(pendingResult);

        Assert.Equal(ToolCycleDecisionKind.Execute, tracker.EvaluateBeforeDispatch(pending).Kind);
        Assert.Equal(ToolCycleDecisionKind.Execute, tracker.EvaluateBeforeDispatch(Prepare("check_background_job", 1)).Kind);
        var mixed = PrepareMixed("probe", 0, "check_background_job", 0);
        var correction = tracker.EvaluateBeforeDispatch(mixed);
        Assert.Equal(ToolCycleDecisionKind.Correct, correction.Kind);
        Assert.True(correction.RefusedCallIds.SetEquals(["first"]));
        Assert.True(correction.ExemptCallIds.SetEquals(["second"]));
        Assert.Equal(ToolCycleDecisionKind.Stop, tracker.EvaluateBeforeDispatch(mixed).Kind);

        // A terminal or denied query supplies ordinary evidence and ends its exception.
        tracker.ObserveCompleted(Complete(pending, "not found", ToolInvocationOutcomeCategory.AccessDenied));
        tracker.ObserveCompleted(Complete(pending, "not found", ToolInvocationOutcomeCategory.AccessDenied));
        var denied = tracker.EvaluateBeforeDispatch(pending);
        Assert.Equal(ToolCycleDecisionKind.Correct, denied.Kind);
        Assert.Empty(denied.ExemptCallIds);
    }

    private static void Require(bool condition, int sequence, string description)
    {
        if (!condition)
            throw new Xunit.Sdk.XunitException($"Deterministic sequence {sequence}: {description}");
    }

    private static PreparedToolCycleBatch Prepare(string tool, int value, int count = 1)
        => ToolCycleSignatureFactory.Prepare(Enumerable.Range(0, count)
            .Select(index => Call($"call-{index}", tool, value)).ToArray(), Executor);

    private static PreparedToolCycleBatch PrepareMixed(string firstTool, int firstValue, string secondTool, int secondValue)
        => ToolCycleSignatureFactory.Prepare(
            [Call("first", firstTool, firstValue), Call("second", secondTool, secondValue)], Executor);

    private static FunctionCallContent Call(string id, string tool, int value) => new(id, tool,
        new Dictionary<string, object?> { ["value"] = value, ["_rationale"] = "Apply the fixed contract case." });

    private static CompletedToolCycleIteration Complete(PreparedToolCycleBatch batch, string text,
        ToolInvocationOutcomeCategory category = ToolInvocationOutcomeCategory.Success)
        => ToolCycleSignatureFactory.Complete(batch, batch.Calls.ToDictionary(call => call.CallId.Value,
            _ => new ToolCycleResult(category, text)));
}
