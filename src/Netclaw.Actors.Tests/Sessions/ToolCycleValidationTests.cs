// -----------------------------------------------------------------------
// <copyright file="ToolCycleValidationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ToolCycleValidationTests
{
    [Theory]
    [InlineData("missing-rationale")]
    [InlineData("invalid-timeout")]
    [InlineData("invalid-background")]
    public void Valid_metadata_repair_escapes_both_cycle_interventions(string scenario)
    {
        var executor = CreateExecutor();
        var original = Call("original", "same");
        switch (scenario)
        {
            case "missing-rationale": original.Arguments!.Remove("_rationale"); break;
            case "invalid-timeout": original.Arguments!["_timeout_seconds"] = -1; break;
            case "invalid-background": original.Arguments!["_background"] = "invalid"; break;
        }

        var rejection = Assert.IsType<ToolArgumentRejection>(executor.ValidateToolCall(original));
        var batch = ToolCycleSignatureFactory.Prepare([original], executor);
        var completed = Complete(batch, rejection.Message);
        var tracker = new TurnStateTracker();
        tracker.ObserveCompleted(completed);
        tracker.ObserveCompleted(completed);

        var repaired = Call("repair", "same");
        Assert.Null(executor.InterpretToolCall(repaired).Rejection);
        var repairedBatch = ToolCycleSignatureFactory.Prepare([repaired], executor);
        Assert.NotEqual(batch.Action, repairedBatch.Action);
        Assert.Equal(ToolCycleDecisionKind.Execute, tracker.EvaluateBeforeDispatch(repairedBatch.Action).Kind);
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(batch.Action).Kind);
        Assert.Equal(ToolCycleDecisionKind.Execute, tracker.EvaluateBeforeDispatch(repairedBatch.Action).Kind);
        Assert.Equal(ToolCycleDecisionKind.Stop, tracker.EvaluateBeforeDispatch(batch.Action).Kind);
    }

    [Fact]
    public void Valid_metadata_changes_preserve_identity_but_rejected_values_do_not()
    {
        var executor = CreateExecutor();
        var first = Call("a", "same");
        var second = Call("b", "same");
        second.Arguments!["_rationale"] = "Read the same value for another reason.";
        second.Arguments["_timeout_seconds"] = 60;
        Assert.Null(executor.ValidateToolCall(first));
        Assert.Null(executor.ValidateToolCall(second));
        Assert.Equal(ToolCycleSignatureFactory.Prepare([first], executor).Action,
            ToolCycleSignatureFactory.Prepare([second], executor).Action);

        first.Arguments!["_timeout_seconds"] = -1;
        second.Arguments["_timeout_seconds"] = -2;
        Assert.NotNull(executor.ValidateToolCall(first));
        Assert.NotNull(executor.ValidateToolCall(second));
        Assert.NotEqual(ToolCycleSignatureFactory.Prepare([first], executor).Action,
            ToolCycleSignatureFactory.Prepare([second], executor).Action);
    }

    [Fact]
    public void Parallel_members_keep_their_own_validation_state()
    {
        var executor = CreateExecutor();
        var a = Call("a", "first");
        var b = Call("b", "second");
        a.Arguments!.Remove("_rationale");
        var firstBatch = ToolCycleSignatureFactory.Prepare([a, b], executor);

        a.Arguments["_rationale"] = "Read the first value.";
        b.Arguments!.Remove("_rationale");
        var nextBatch = ToolCycleSignatureFactory.Prepare([b, a], executor);
        Assert.NotEqual(firstBatch.Action, nextBatch.Action);
    }

    private static DispatchingToolExecutor CreateExecutor()
    {
        var registry = new ToolRegistry();
        registry.Register(AIFunctionFactory.Create((string text) => text, "probe"), "builtin");
        return new DispatchingToolExecutor(registry, new ToolAccessPolicy(
            new NetclawPaths(), new ToolConfig(),
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([])));
    }

    private static FunctionCallContent Call(string id, string text) => new(id, "probe",
        new Dictionary<string, object?>
        {
            ["text"] = text,
            ["_rationale"] = "Read the value.",
            ["_timeout_seconds"] = 30
        });

    private static CompletedToolCycleIteration Complete(PreparedToolCycleBatch batch, string text)
        => ToolCycleSignatureFactory.Complete(batch, batch.Calls.ToDictionary(
            call => call.CallId.Value,
            _ => new ToolCycleResult(ToolInvocationOutcomeCategory.InvalidInput, text)));
}
