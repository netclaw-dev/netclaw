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
/// A catalog of tool-loop shapes. Each case proves its detector and progress stage separately.
/// </summary>
public sealed class LoopGuardShapeTests
{
    private const string RationaleError =
        "Error: Required meta argument '_rationale' must be a non-empty string. "
        + "Supply one sentence that states the tool call intent. The tool was NOT executed.";

    /// <summary>One tool call in a scripted turn. The category is a name so the
    /// public theory data can carry it; the internal enum stays internal.</summary>
    public sealed record Step(string Tool, string Args, string Category, string Text)
    {
        /// <summary>True when the receipt is a pending background job, not a result.</summary>
        public bool PendingJob { get; init; }
    }

    /// <summary>A named scripted turn.</summary>
    public sealed record Shape(string Name, Step[] Steps)
    {
        public string ExpectedProgressStage { get; init; } = nameof(RefusedRepeatAction.EscalatedNudge);
    }

    private static Step Fail(string tool, string args, string text = RationaleError)
        => new(tool, args, nameof(ToolInvocationOutcomeCategory.InvalidInput), text);

    private static Step Ok(string tool, string args, string text = "ok")
        => new(tool, args, nameof(ToolInvocationOutcomeCategory.Success), text);

    private static Step Denied(string tool, string args)
        => new(tool, args, nameof(ToolInvocationOutcomeCategory.AccessDenied), "Error: path denied.");

    private static Step Missing(string tool, string args)
        => new(tool, args, nameof(ToolInvocationOutcomeCategory.NotFound), "Error: not found.");

    private static Step Transient(string tool, string args)
        => new(tool, args, nameof(ToolInvocationOutcomeCategory.TransientFailure), "Error: connection reset.");

    // ── Failure shapes: the turn is stuck and the guard must intervene ──

    /// <summary>
    /// Shapes the guard catches today. These are the controls: they prove the
    /// catalog measures the guard rather than the harness.
    /// </summary>
    public static TheoryData<Shape> CaughtFailureShapes => new()
    {
        // Same call, same result, twice. The recurrence episode reaches two
        // equal rounds, so the third call is refused.
        new Shape("same call, same result, twice", [
            Fail("search_tools", "same"), Fail("search_tools", "same"), Fail("search_tools", "same")
        ]),

        // A two-step cycle, A B A B A. Two equal adjacent copies of the period-2
        // action match, so the fifth call is refused.
        new Shape("alternating two-step cycle", [
            Fail("search_tools", "a"), Fail("load_tool", "b"),
            Fail("search_tools", "a"), Fail("load_tool", "b"),
            Fail("search_tools", "a")
        ])
    };

    /// <summary>These shapes require the progress axis because no action repeats.</summary>
    public static TheoryData<Shape> MissedFailureShapes => new()
    {
        // The reported incident. Six calls, six different tools, one identical
        // error. Every recurrence key differs, so no episode reaches two rounds.
        new Shape("rotating tools, identical error", [
            Fail("spawn_agent", "a"), Fail("search_tools", "b"), Fail("search_tools", "c"),
            Fail("load_tool", "d"), Fail("search_tools", "e"), Fail("search_tools", "f"),
            Fail("spawn_agent", "g"), Fail("load_tool", "h")
        ]) { ExpectedProgressStage = nameof(RefusedRepeatAction.EndTurn) },

        // Same tool, different arguments, identical error. The arguments hash
        // differs, so the recurrence key differs on every call.
        new Shape("same tool, varying arguments, identical error", [
            Fail("search_tools", "query-1"), Fail("search_tools", "query-2"),
            Fail("search_tools", "query-3"), Fail("search_tools", "query-4"),
            Fail("search_tools", "query-5"), Fail("search_tools", "query-6")
        ]),

        // Flailing with a different error each time. No action repeats and no
        // outcome repeats, so neither detector has anything to compare.
        new Shape("varying tools, varying errors", [
            Fail("spawn_agent", "a", "Error: unknown agent 'x'."),
            Fail("search_tools", "b", "Error: no server named 'y'."),
            Fail("load_tool", "c", "Error: tool 'z' is not loaded."),
            Fail("file_read", "d", "Error: path is required."),
            Fail("shell_execute", "e", "Error: command is required."),
            Fail("web_fetch", "f", "Error: url is required.")
        ]),

        // A permission wall. Every call is denied for the same reason, but the
        // model keeps trying different paths.
        new Shape("access denied streak across different paths", [
            Denied("file_read", "/etc/a"), Denied("file_read", "/etc/b"),
            Denied("file_write", "/etc/c"), Denied("file_read", "/root/d"),
            Denied("file_write", "/root/e"), Denied("file_read", "/root/f")
        ]),

        // Repeated misses on resources that do not exist. Each path differs, so
        // no action repeats.
        new Shape("not found streak across different paths", [
            Missing("file_read", "/a"), Missing("file_read", "/b"),
            Missing("file_read", "/c"), Missing("file_read", "/d"),
            Missing("file_read", "/e"), Missing("file_read", "/f"),
            Missing("file_read", "/g"), Missing("file_read", "/h"),
            Missing("file_read", "/i"), Missing("file_read", "/j"),
            Missing("file_read", "/k"), Missing("file_read", "/l")
        ]),

        // A backend that keeps dropping the connection. The model retries with
        // different endpoints, so no action repeats.
        new Shape("transient failure streak across different endpoints", [
            Transient("web_fetch", "https://a"), Transient("web_fetch", "https://b"),
            Transient("web_fetch", "https://c"), Transient("web_fetch", "https://d"),
            Transient("web_fetch", "https://e"), Transient("web_fetch", "https://f")
        ])
    };

    // ── Success shapes: the turn is making progress and the guard must stay quiet ──

    public static TheoryData<Shape> SuccessShapes => new()
    {
        // A long failure run interrupted by one success, then more failures.
        // The success proves the model can make progress.
        new Shape("failure run reset by a success", [
            Fail("search_tools", "a"), Fail("search_tools", "b"), Fail("search_tools", "c"),
            Ok("file_read", "d"),
            Fail("search_tools", "e"), Fail("search_tools", "f"), Fail("search_tools", "g")
        ]),

        // Legitimate retry: the same tool fails twice with different arguments,
        // then succeeds. A guard that fires here is worse than no guard.
        new Shape("legitimate retry then success", [
            Fail("file_edit", "old-1", "Error: old_string not found."),
            Fail("file_edit", "old-2", "Error: old_string not found."),
            Ok("file_edit", "old-3", "changed")
        ]),

        // Exploration: several misses, then the right query. This is normal
        // search behavior, not a loop.
        new Shape("exploration then success", [
            Missing("search_tools", "q1"), Missing("search_tools", "q2"),
            Missing("search_tools", "q3"), Missing("file_read", "/repo/a"),
            Missing("file_read", "/repo/b"), Missing("file_read", "/repo/c"),
            Missing("file_read", "/repo/d"), Ok("file_read", "/repo/found")
        ]),

        // Alternating failure and success. Every failure is followed by real
        // work, so the turn is progressing.
        new Shape("alternating failure and success", [
            Fail("search_tools", "a"), Ok("file_read", "b"),
            Fail("search_tools", "c"), Ok("file_read", "d"),
            Fail("search_tools", "e"), Ok("file_read", "f")
        ]),

        // A long run of distinct successful calls. The guard must never fire on
        // a productive turn, however many rounds it takes.
        new Shape("twelve distinct successful rounds", [
            Ok("file_read", "1"), Ok("file_read", "2"), Ok("file_read", "3"),
            Ok("file_read", "4"), Ok("file_read", "5"), Ok("file_read", "6"),
            Ok("file_read", "7"), Ok("file_read", "8"), Ok("file_read", "9"),
            Ok("file_read", "10"), Ok("file_read", "11"), Ok("file_read", "12")
        ]),

        // The same call returning a different result each time is progress, not
        // a cycle. A polling tool looks like this: a running job returns a
        // PendingBackgroundJob receipt, which the guard exempts.
        new Shape("same call, changing result", [
            Ok("check_background_job", "job-1", "running") with { PendingJob = true },
            Ok("check_background_job", "job-1", "running") with { PendingJob = true },
            Ok("check_background_job", "job-1", "done")
        ]),

        // A single failure in a productive turn. One miss is not a loop.
        new Shape("one failure among successes", [
            Ok("file_read", "a"), Fail("file_read", "b"), Ok("file_read", "c")
        ])
    };

    [Theory]
    [MemberData(nameof(CaughtFailureShapes))]
    public void Caught_failure_shapes_intervene(Shape shape)
    {
        var observations = Run(shape);
        var last = observations[^1];
        Assert.Equal(ToolCycleDecisionKind.Correct, last.Decision.Kind);
        Assert.Equal(ToolCycleDecisionKind.Correct, last.Adjacent.Kind);
        Assert.Equal(shape.Name == "alternating two-step cycle" ? 2 : 1, last.Adjacent.Period);
        Assert.Equal(2, last.Adjacent.Repetitions);
    }

    [Theory]
    [MemberData(nameof(MissedFailureShapes))]
    public void Missed_failure_shapes_must_intervene(Shape shape)
    {
        var observations = Run(shape);
        Assert.All(observations, observation =>
        {
            Assert.Equal(ToolCycleDecisionKind.Execute, observation.Decision.Kind);
            Assert.Equal(ToolCycleDecisionKind.Execute, observation.Adjacent.Kind);
            Assert.Equal(RefusedRepeatAction.None, observation.Decision.RefusedRepeat);
        });
        var final = Enum.Parse<RefusedRepeatAction>(shape.ExpectedProgressStage);
        Assert.Equal(final, observations[^1].ProgressAfterResults);
        RefusedRepeatAction[] stages = final == RefusedRepeatAction.EndTurn
            ? [RefusedRepeatAction.Nudge, RefusedRepeatAction.EscalatedNudge, RefusedRepeatAction.EndTurn]
            : [RefusedRepeatAction.Nudge, RefusedRepeatAction.EscalatedNudge];
        Assert.Equal(stages, observations.Select(observation => observation.ProgressAfterResults)
            .Where(stage => stage != RefusedRepeatAction.None).Distinct().ToArray());
        Assert.Equal(shape.Steps.Length, observations.Count);
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
        });
    }

    private sealed record Observation(ToolCycleDecision Adjacent, ToolCycleBatchDecision Decision,
        RefusedRepeatAction ProgressAfterResults);

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
                observations.Add(new Observation(adjacent, decision, tracker.EvaluateProgress()));
                break;
            }
            if (decision.Kind == ToolCycleDecisionKind.Execute)
                tracker.ObserveCompleted(Complete(batch, step));
            observations.Add(new Observation(adjacent, decision, tracker.EvaluateProgress()));
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
                _ => new ToolCycleResult(category, step.Text) { PendingJob = step.PendingJob },
                StringComparer.Ordinal));
    }

    private static FunctionCallContent Call(string callId, string toolName, string args)
        => new(callId, toolName, new Dictionary<string, object?> { ["arg"] = args });
}
