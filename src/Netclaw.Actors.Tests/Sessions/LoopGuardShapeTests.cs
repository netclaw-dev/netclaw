// -----------------------------------------------------------------------
// <copyright file="LoopGuardShapeTests.cs" company="Petabridge, LLC">
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
/// A catalog of tool-loop shapes, split by the detector that must answer:
/// <list type="bullet">
/// <item><see cref="ActionRecurrenceShapes"/> — the same action repeats. Reason: <c>ActionRecurrence</c>.</item>
/// <item><see cref="FailureCauseRecurrenceShapes"/> — the same root failure repeats across different tools or
/// arguments. Reason: <c>FailureCauseRecurrence</c>. Red until the cause detector lands.</item>
/// <item><see cref="FailureBudgetShapes"/> — distinct failures with no recognized recurrence. Reason:
/// <c>FailureBudget</c>, the conservative backstop.</item>
/// <item><see cref="SuccessShapes"/> — the turn is progressing. Reason: <c>None</c>.</item>
/// </list>
/// Every theory asserts the <see cref="ToolCycleBatchDecision.Reason"/> and the stage, so one detector cannot
/// accidentally make a broken test green.
/// </summary>
public sealed class LoopGuardShapeTests
{
    private const string RationaleError =
        "Error: Required meta argument '_rationale' must be a non-empty string. "
        + "Supply one sentence that states the tool call intent. The tool was NOT executed.";

    private const string NetclawSource = "netclaw";

    /// <summary>One tool call in a scripted turn. The category is a name so the public theory data can carry it;
    /// the internal enum stays internal. A failure carries a structured cause, not just a category.</summary>
    public sealed record Step(string Tool, string Args, string Category, string Text)
    {
        /// <summary>True when the receipt is a pending background job, not a result.</summary>
        public bool PendingJob { get; init; }

        /// <summary>The failure-cause identity. Null means no known structured cause (budget-only).</summary>
        public string? FailureSource { get; init; }
        public string? FailureCode { get; init; }
        public string? FailureScope { get; init; }
    }

    /// <summary>A named scripted turn.</summary>
    public sealed record Shape(string Name, Step[] Steps);

    // A failure with no structured cause. Only the generic budget can react to it.
    private static Step Fail(string tool, string args, string text = RationaleError)
        => new(tool, args, nameof(ToolInvocationOutcomeCategory.InvalidInput), text);

    // A failure with a stable cause. The cause detector keys on (source, code, scope).
    private static Step FailCause(string tool, string args, string code, string text = RationaleError, string? scope = null)
        => new(tool, args, nameof(ToolInvocationOutcomeCategory.InvalidInput), text)
        {
            FailureSource = NetclawSource, FailureCode = code, FailureScope = scope
        };

    private static Step Ok(string tool, string args, string text = "ok")
        => new(tool, args, nameof(ToolInvocationOutcomeCategory.Success), text);

    private static Step Denied(string tool, string args)
        => new(tool, args, nameof(ToolInvocationOutcomeCategory.AccessDenied), "Error: path denied.");

    private static Step Missing(string tool, string args)
        => new(tool, args, nameof(ToolInvocationOutcomeCategory.NotFound), "Error: not found.")
        {
            FailureSource = NetclawSource, FailureCode = "not_found", FailureScope = args
        };

    private static Step Transient(string tool, string args)
        => new(tool, args, nameof(ToolInvocationOutcomeCategory.TransientFailure), "Error: connection reset.");

    // ── Action recurrence: the same action repeats ──

    public static TheoryData<Shape> ActionRecurrenceShapes => new()
    {
        // Same call, same result, twice. The recurrence episode reaches two equal rounds, so the third call is
        // refused by action identity.
        new Shape("same call, same result, twice", [
            Fail("search_tools", "same"), Fail("search_tools", "same"), Fail("search_tools", "same")
        ]),

        // A two-step cycle, A B A B A. Two equal adjacent copies of the period-2 action match, so the fifth call
        // is refused by the adjacent-cycle detector.
        new Shape("alternating two-step cycle", [
            Fail("search_tools", "a"), Fail("load_tool", "b"),
            Fail("search_tools", "a"), Fail("load_tool", "b"),
            Fail("search_tools", "a")
        ])
    };

    // ── Failure-cause recurrence: the same root failure repeats across different actions ──

    public static TheoryData<Shape> FailureCauseRecurrenceShapes => new()
    {
        // The reported incident: three different tools, one identical front-door rejection. Every action hash
        // differs, so only the cause detector can see this.
        new Shape("rotating tools, identical cause", [
            FailCause("spawn_agent", "a", "invalid_rationale"),
            FailCause("search_tools", "b", "invalid_rationale"),
            FailCause("load_tool", "c", "invalid_rationale")
        ]),

        // Same tool, different arguments, identical cause. The arguments hash differs, so action identity is blind.
        new Shape("same tool, varying arguments, identical cause", [
            FailCause("search_tools", "q1", "invalid_rationale"),
            FailCause("search_tools", "q2", "invalid_rationale"),
            FailCause("search_tools", "q3", "invalid_rationale")
        ]),

        // A cause that returns after unrelated successes. Success must not erase the evidence that the same
        // unresolved cause keeps coming back; a bounded window keeps it in view.
        new Shape("cause returns after unrelated successes", [
            FailCause("spawn_agent", "a", "invalid_rationale"),
            FailCause("search_tools", "b", "invalid_rationale"),
            Ok("file_read", "/other"),
            FailCause("load_tool", "c", "invalid_rationale"),
            FailCause("web_fetch", "d", "invalid_rationale")
        ]),

        // Three identical causes in one parallel batch establish the problem but consume exactly one feedback
        // opportunity. The next model round gets one nudge, not three.
        new Shape("one parallel batch of identical causes", [
            FailCause("spawn_agent", "a", "invalid_rationale"),
            FailCause("search_tools", "b", "invalid_rationale"),
            FailCause("load_tool", "c", "invalid_rationale")
        ])
    };

    // ── Failure budget: distinct failures, no recognized recurrence ──

    public static TheoryData<Shape> FailureBudgetShapes => new()
    {
        // Flailing with a different error each time. No action repeats and no cause repeats, so the budget is the
        // only thing that can stop it.
        new Shape("varying tools, varying errors", [
            Fail("spawn_agent", "a", "Error: unknown agent 'x'."),
            Fail("search_tools", "b", "Error: no server named 'y'."),
            Fail("load_tool", "c", "Error: tool 'z' is not loaded."),
            Fail("file_read", "d", "Error: path is required."),
            Fail("shell_execute", "e", "Error: command is required."),
            Fail("web_fetch", "f", "Error: url is required.")
        ]),

        // Distinct codes in the same broad category are distinct causes. invalid_rationale is not
        // unrecognized_argument is not args_parse_error. The cause detector must not conflate them.
        new Shape("different codes, same category", [
            FailCause("spawn_agent", "a", "invalid_rationale"),
            FailCause("search_tools", "b", "unrecognized_argument"),
            FailCause("load_tool", "c", "args_parse_error"),
            FailCause("file_read", "d", "invalid_meta_value")
        ]),

        // A permission wall across different paths. Each denial is its own authorization decision; no single
        // cause repeats, so the budget is the backstop.
        new Shape("access denied streak across different paths", [
            Denied("file_read", "/etc/a"), Denied("file_read", "/etc/b"),
            Denied("file_write", "/etc/c"), Denied("file_read", "/root/d"),
            Denied("file_write", "/root/e"), Denied("file_read", "/root/f")
        ]),

        // Distinct missing paths are distinct causes, not one repeated cause. Exploration, not a loop.
        new Shape("not found streak across different paths", [
            Missing("file_read", "/a"), Missing("file_read", "/b"),
            Missing("file_read", "/c"), Missing("file_read", "/d"),
            Missing("file_read", "/e"), Missing("file_read", "/f"),
            Missing("file_read", "/g"), Missing("file_read", "/h")
        ]),

        // A backend that keeps dropping the connection across different endpoints. No stable cause, budget only.
        new Shape("transient failure streak across different endpoints", [
            Transient("web_fetch", "https://a"), Transient("web_fetch", "https://b"),
            Transient("web_fetch", "https://c"), Transient("web_fetch", "https://d"),
            Transient("web_fetch", "https://e"), Transient("web_fetch", "https://f")
        ])
    };

    // ── Success shapes: the turn is making progress and every detector must stay quiet ──

    public static TheoryData<Shape> SuccessShapes => new()
    {
        // A long failure run interrupted by one success, then more failures. The success resets the budget.
        new Shape("failure run reset by a success", [
            Fail("search_tools", "a"), Fail("search_tools", "b"), Fail("search_tools", "c"),
            Ok("file_read", "d"),
            Fail("search_tools", "e"), Fail("search_tools", "f"), Fail("search_tools", "g")
        ]),

        // Legitimate retry: the same tool fails twice with different arguments, then succeeds.
        new Shape("legitimate retry then success", [
            Fail("file_edit", "old-1", "Error: old_string not found."),
            Fail("file_edit", "old-2", "Error: old_string not found."),
            Ok("file_edit", "old-3", "changed")
        ]),

        // Exploration: several distinct misses, then the right path. Distinct NotFound paths are not one cause.
        new Shape("exploration then success", [
            Missing("search_tools", "q1"), Missing("search_tools", "q2"),
            Missing("search_tools", "q3"), Missing("file_read", "/repo/a"),
            Missing("file_read", "/repo/b"), Missing("file_read", "/repo/c"),
            Missing("file_read", "/repo/d"), Ok("file_read", "/repo/found")
        ]),

        // Alternating failure and success. Every failure is followed by real work.
        new Shape("alternating failure and success", [
            Fail("search_tools", "a"), Ok("file_read", "b"),
            Fail("search_tools", "c"), Ok("file_read", "d"),
            Fail("search_tools", "e"), Ok("file_read", "f")
        ]),

        // A long run of distinct successful calls.
        new Shape("twelve distinct successful rounds", [
            Ok("file_read", "1"), Ok("file_read", "2"), Ok("file_read", "3"),
            Ok("file_read", "4"), Ok("file_read", "5"), Ok("file_read", "6"),
            Ok("file_read", "7"), Ok("file_read", "8"), Ok("file_read", "9"),
            Ok("file_read", "10"), Ok("file_read", "11"), Ok("file_read", "12")
        ]),

        // The same call returning a different result each time is progress, not a cycle.
        new Shape("same call, changing result", [
            Ok("check_background_job", "job-1", "running") with { PendingJob = true },
            Ok("check_background_job", "job-1", "running") with { PendingJob = true },
            Ok("check_background_job", "job-1", "done")
        ]),

        // A single failure in a productive turn.
        new Shape("one failure among successes", [
            Ok("file_read", "a"), Fail("file_read", "b"), Ok("file_read", "c")
        ])
    };

    // ── Assertions ──

    [Theory]
    [MemberData(nameof(ActionRecurrenceShapes))]
    public void Action_recurrence_shapes_intervene(Shape shape)
    {
        var observations = Run(shape);
        var last = observations[^1];
        Assert.Equal(ToolCycleDecisionKind.Correct, last.Decision.Kind);
        Assert.Equal(ToolCycleDecisionKind.Correct, last.Adjacent.Kind);
        Assert.Equal(LoopInterventionReason.ActionRecurrence, last.Decision.Reason);
        Assert.Equal(shape.Name == "alternating two-step cycle" ? 2 : 1, last.Adjacent.Period);
        Assert.Equal(2, last.Adjacent.Repetitions);
    }

    [Theory]
    [MemberData(nameof(FailureCauseRecurrenceShapes))]
    public void Failure_cause_recurrence_shapes_intervene(Shape shape)
    {
        var observations = Run(shape);
        Assert.Contains(observations, observation => observation.ResultsReason == LoopInterventionReason.FailureCauseRecurrence);
    }

    [Theory]
    [MemberData(nameof(FailureBudgetShapes))]
    public void Failure_budget_shapes_stay_conservative(Shape shape)
    {
        var observations = Run(shape);
        Assert.Contains(observations, observation => observation.ResultsReason == LoopInterventionReason.FailureBudget);
        Assert.DoesNotContain(observations, observation => observation.ResultsReason == LoopInterventionReason.FailureCauseRecurrence);
    }

    [Theory]
    [MemberData(nameof(SuccessShapes))]
    public void Success_shapes_must_not_intervene(Shape shape)
    {
        var observations = Run(shape);
        Assert.Equal(shape.Steps.Length, observations.Count);
        Assert.All(observations, observation =>
        {
            Assert.Equal(ToolCycleDecisionKind.Execute, observation.Decision.Kind);
            Assert.Equal(ToolCycleDecisionKind.Execute, observation.Adjacent.Kind);
            Assert.Equal(RefusedRepeatAction.None, observation.Decision.RefusedRepeat);
            Assert.Equal(RefusedRepeatAction.None, observation.Decision.Progress);
            Assert.Equal(RefusedRepeatAction.None, observation.ProgressAfterResults);
            Assert.Equal(LoopInterventionReason.None, observation.Decision.Reason);
            Assert.Equal(LoopInterventionReason.None, observation.ResultsReason);
        });
    }

    private sealed record Observation(ToolCycleDecision Adjacent, ToolCycleBatchDecision Decision,
        RefusedRepeatAction ProgressAfterResults, LoopInterventionReason ResultsReason);

    private static IReadOnlyList<Observation> Run(Shape shape)
    {
        var tracker = new TurnStateTracker();
        var observations = new List<Observation>();
        foreach (var step in shape.Steps)
        {
            var batch = Prepare(step);
            // The adjacent check changes state. A copy exposes its decision without a second check on the live tracker.
            var adjacentTracker = new TurnStateTracker();
            adjacentTracker.RestoreLoopGuard(tracker.ExportLoopGuard());
            var adjacent = adjacentTracker.EvaluateAdjacentBeforeDispatch(batch.Action);
            var decision = tracker.EvaluateBeforeDispatch(batch);
            if (decision.Progress == RefusedRepeatAction.EndTurn || decision.RefusedRepeat == RefusedRepeatAction.EndTurn)
            {
                observations.Add(new Observation(adjacent, decision, tracker.EvaluateProgress(), tracker.EvaluateProgressReason()));
                break;
            }
            if (decision.Kind == ToolCycleDecisionKind.Execute)
                tracker.ObserveCompleted(Complete(batch, step));
            observations.Add(new Observation(adjacent, decision, tracker.EvaluateProgress(), tracker.EvaluateProgressReason()));
        }
        return observations;
    }

    private static PreparedToolCycleBatch Prepare(Step step)
        => ToolCycleSignatureFactory.Prepare(
            [Call("call-" + Guid.NewGuid().ToString("N"), step.Tool, step.Args)],
            new FakeToolExecutor());

    private static CompletedToolCycleIteration Complete(PreparedToolCycleBatch batch, Step step)
    {
        var category = Enum.Parse<ToolInvocationOutcomeCategory>(step.Category);
        return ToolCycleSignatureFactory.Complete(
            batch,
            batch.Calls.ToDictionary(
                static call => call.CallId.Value,
                _ => new ToolCycleResult(category, step.Text)
                {
                    PendingJob = step.PendingJob,
                    FailureKey = step.FailureCode is null
                        ? null
                        : new ToolFailureKey(step.FailureSource!, step.FailureCode, step.FailureScope)
                },
                StringComparer.Ordinal));
    }

    private static FunctionCallContent Call(string callId, string toolName, string args)
        => new(callId, toolName, new Dictionary<string, object?> { ["arg"] = args });
}
