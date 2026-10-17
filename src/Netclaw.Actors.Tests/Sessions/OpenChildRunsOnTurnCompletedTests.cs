// -----------------------------------------------------------------------
// <copyright file="OpenChildRunsOnTurnCompletedTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Akka.Actor;
using Akka.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Media;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec (<c>background-subagent-runs</c>, "Each completed turn reports the open child runs"): the
/// session owns the fact that a child run is open. It reports the open runs in every
/// <see cref="TurnCompleted"/>. A headless caller ends its run on the first completed turn that reports none.
/// A client cannot infer the fact: a child can finish inside turn 1, and the session then starts turn 2 right
/// after <see cref="TurnCompleted"/> of turn 1.
/// <para>
/// A run is open from durable acceptance until the parent records the turn that consumes its result. The
/// parent model is the fake client of the base class. The child model waits on a gate or finishes at once.
/// </para>
/// </summary>
public sealed class OpenChildRunsOnTurnCompletedTests : ChildLifetimeTestBase
{
    public OpenChildRunsOnTurnCompletedTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task A_turn_with_no_child_reports_no_open_run()
    {
        // Control: a session that never started a child reports an empty list.
        var sessionId = new SessionId("boundary/open-runs-no-child");
        var subscriber = await JoinFullAsync(sessionId);
        await SendRequesterAsync(sessionId, "Say hello.");

        var seen = new List<SessionOutput>();
        var turn = await NextTurnAsync(subscriber, seen);

        Assert.Equal(TurnOutcome.Completed, turn.Outcome);
        Assert.Empty(turn.OpenChildRunIds);
    }

    [Fact]
    public async Task A_turn_that_starts_a_child_reports_it_open_and_the_follow_up_turn_reports_none()
    {
        // Planted bug: the session never reports the field, or it still lists the run after the parent
        // consumed the result.
        var sessionId = new SessionId("boundary/open-runs-one-child");
        var childGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-1", "child-a")];
        var subscriber = await JoinFullAsync(sessionId);

        try
        {
            await SendRequesterAsync(sessionId, "Start the child run.");
            var seen = new List<SessionOutput>();
            var first = await NextTurnAsync(subscriber, seen);
            var runId = AcceptedRunId(seen, "spawn-1");

            Assert.Equal(TurnOutcome.Completed, first.Outcome);
            Assert.Equal([runId], Ids(first));

            childGate.TrySetResult();
            var second = await NextTurnAsync(subscriber, seen);

            Assert.Equal(TurnOutcome.Completed, second.Outcome);
            Assert.Empty(second.OpenChildRunIds);
            Assert.Contains(FakeChat.ReceivedMessages, HasChildResult);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_child_that_finishes_inside_turn_one_is_still_open_when_turn_one_completes()
    {
        // The race that a client counter loses. The child finishes while the parent turn still runs. The
        // session starts turn 2 right after TurnCompleted of turn 1. A client that counts started minus
        // completed children reads zero at turn 1 and exits too early.
        // The child waits until the second parent call began. That call has then chosen its history, so the
        // session cannot fold the child result into turn 1. The second parent call waits until the child is done.
        var sessionId = new SessionId("boundary/open-runs-child-finishes-in-turn-one");
        var childGate = NewGate();
        var parentGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-1", "child-a")];
        FakeChat.AfterToolCallResponse = _ => FakeChat.NextResponseGate = parentGate;
        var subscriber = await JoinFullAsync(sessionId);

        try
        {
            await SendRequesterAsync(sessionId, "Start the child run.");
            await AwaitAssertAsync(
                () => Assert.True(FakeChat.ReceivedMessages.Count >= 2, "The second parent call has not started."),
                Deadline,
                cancellationToken: Cancellation);

            childGate.TrySetResult();
            var seen = new List<SessionOutput>();
            await PumpAsync(subscriber, seen, output => output is SubAgentOutput { Phase: SubAgentPhase.Completed });
            Assert.DoesNotContain(seen, output => output is TurnCompleted);

            parentGate.TrySetResult();
            var first = await NextTurnAsync(subscriber, seen);
            var runId = AcceptedRunId(seen, "spawn-1");
            var second = await NextTurnAsync(subscriber, seen);

            Assert.Equal(1, first.TurnNumber.Value);
            Assert.Equal([runId], Ids(first));
            Assert.Equal(2, second.TurnNumber.Value);
            Assert.Empty(second.OpenChildRunIds);
            Assert.Contains(FakeChat.ReceivedMessages, HasChildResult);
        }
        finally
        {
            childGate.TrySetResult();
            parentGate.TrySetResult();
        }
    }

    [Fact]
    public async Task Two_ready_children_give_two_follow_up_turns_with_a_shrinking_list()
    {
        // Planted bug: the session lists no run, or all runs, at the middle turn. It delivers one result per
        // model call, so the middle turn must list exactly one run.
        // Two user messages each start one child. Both children wait on a gate, so both results become ready
        // while the session is idle, and no model call can fold a result into an earlier turn.
        var sessionId = new SessionId("boundary/open-runs-two-children");
        var gateA = NewGate();
        var gateB = NewGate();
        Clients.Child.NextResponseGate = gateA;
        FakeChat.PlannedResponses.Enqueue([Spawn("spawn-a", "child-a")]);
        FakeChat.PlannedResponses.Enqueue([new Microsoft.Extensions.AI.TextContent("Started A.")]);
        FakeChat.PlannedResponses.Enqueue([Spawn("spawn-b", "child-a")]);
        FakeChat.PlannedResponses.Enqueue([new Microsoft.Extensions.AI.TextContent("Started B.")]);
        var subscriber = await JoinFullAsync(sessionId);

        try
        {
            await SendRequesterAsync(sessionId, "Start the first child run.");
            var seen = new List<SessionOutput>();
            var first = await NextTurnAsync(subscriber, seen);
            await Clients.Child.FirstCallEntered.Task.WaitAsync(Deadline, Cancellation);

            Clients.Child.NextResponseGate = gateB;
            await SendRequesterAsync(sessionId, "Start the second child run.");
            var second = await NextTurnAsync(subscriber, seen);
            await AwaitAssertAsync(
                () => Assert.Equal(2, Clients.Child.CallCount), Deadline, cancellationToken: Cancellation);
            var runA = AcceptedRunId(seen, "spawn-a");
            var runB = AcceptedRunId(seen, "spawn-b");

            gateA.TrySetResult();
            gateB.TrySetResult();
            var third = await NextTurnAsync(subscriber, seen);
            var fourth = await NextTurnAsync(subscriber, seen);

            Assert.Equal([runA], Ids(first));
            Assert.Equal(
                new[] { runA, runB }.Order(StringComparer.Ordinal),
                Ids(second).Order(StringComparer.Ordinal));
            var middle = Assert.Single(Ids(third));
            Assert.Contains(middle, new[] { runA, runB });
            Assert.Empty(fourth.OpenChildRunIds);
        }
        finally
        {
            gateA.TrySetResult();
            gateB.TrySetResult();
        }
    }

    [Fact]
    public async Task A_failed_turn_reports_the_open_run()
    {
        // Planted bug: the failure site of TurnCompleted skips the field. A failed turn still has an open run.
        var sessionId = new SessionId("boundary/open-runs-failed-turn");
        var childGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-1", "child-a")];
        // The second parent call fails.
        FakeChat.AfterToolCallResponse = _ => FakeChat.PlannedExceptions.Enqueue(
            new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable));
        var subscriber = await JoinFullAsync(sessionId);

        try
        {
            await SendRequesterAsync(sessionId, "Start the child run.");
            var seen = new List<SessionOutput>();
            var failed = await NextTurnAsync(subscriber, seen);

            Assert.Equal(TurnOutcome.Failed, failed.Outcome);
            Assert.Equal([AcceptedRunId(seen, "spawn-1")], Ids(failed));
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_failed_follow_up_turn_closes_the_run_and_reports_none()
    {
        // A failed model call during the delivery closes the parent decision for that result, so the session
        // does not deliver the result again. The failed turn then lists no run. A listed run would make a
        // client wait for a result that the session will never deliver.
        var sessionId = new SessionId("boundary/open-runs-failed-follow-up");
        var childGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-1", "child-a")];
        var subscriber = await JoinFullAsync(sessionId);

        try
        {
            await SendRequesterAsync(sessionId, "Start the child run.");
            var seen = new List<SessionOutput>();
            var first = await NextTurnAsync(subscriber, seen);
            var runId = AcceptedRunId(seen, "spawn-1");
            Assert.Equal([runId], Ids(first));

            // The next parent call is the delivery of the child result. It fails.
            FakeChat.PlannedExceptions.Enqueue(
                new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable));
            childGate.TrySetResult();

            var failed = await NextTurnAsync(subscriber, seen);

            Assert.Equal(TurnOutcome.Failed, failed.Outcome);
            Assert.Empty(failed.OpenChildRunIds);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    [Fact]
    public async Task The_delivered_child_result_reaches_subscribers_as_one_tool_pair()
    {
        // On dev, spawn_agent waited inside the turn and its result line carried the child text to every
        // channel. The delivery pair is model history only, so the child text must also go out as tool output.
        var sessionId = new SessionId("boundary/open-runs-delivery-pair");
        var childGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-1", "child-a")];
        var subscriber = await JoinFullAsync(sessionId);

        try
        {
            await SendRequesterAsync(sessionId, "Start the child run.");
            var seen = new List<SessionOutput>();
            await NextTurnAsync(subscriber, seen);
            var runId = AcceptedRunId(seen, "spawn-1");
            childGate.TrySetResult();
            await NextTurnAsync(subscriber, seen);

            var call = Assert.Single(seen.OfType<ToolCallOutput>(), output => output.ToolName.Value == "child_run_result");
            var result = Assert.Single(seen.OfType<ToolResultOutput>(), output => output.ToolName.Value == "child_run_result");
            Assert.Equal(call.CallId, result.CallId);
            Assert.NotEqual("spawn-1", call.CallId.Value);
            Assert.Contains(runId, result.Result, StringComparison.Ordinal);
            Assert.Contains(ChildReplyText, result.Result, StringComparison.Ordinal);
            // The call comes before its result, and both come before the end of the delivery turn.
            Assert.True(seen.IndexOf(call) < seen.IndexOf(result));
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_follow_up_turn_with_its_own_tool_batch_emits_the_delivery_pair_once()
    {
        // The continuingChild guard: the delivery turn runs a tool of its own, and the model call after that
        // batch must not select the child again and emit the pair a second time.
        // Planned parent calls: spawn, start text, then the delivery turn (a probe call, then the final text).
        var sessionId = new SessionId("boundary/open-runs-follow-up-tool-batch");
        var childGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.PlannedResponses.Enqueue([Spawn("spawn-1", "child-a")]);
        FakeChat.PlannedResponses.Enqueue([new Microsoft.Extensions.AI.TextContent("Started.")]);
        FakeChat.PlannedResponses.Enqueue([Probe("probe-1", FirstTool)]);
        FakeChat.PlannedResponses.Enqueue([new Microsoft.Extensions.AI.TextContent("Done.")]);
        var subscriber = await JoinFullAsync(sessionId);

        try
        {
            await SendRequesterAsync(sessionId, "Start the child run.");
            var seen = new List<SessionOutput>();
            var first = await NextTurnAsync(subscriber, seen);
            Assert.Single(first.OpenChildRunIds);

            // The child finishes. The delivery turn starts, calls the probe, and waits for its approval.
            childGate.TrySetResult();
            var approval = (ToolInteractionRequest)await PumpAsync(
                subscriber, seen, output => output is ToolInteractionRequest);
            Assert.Equal(FirstTool, approval.ToolName.Value);
            Assert.IsType<CommandAck>(await ApproveAsync(sessionId, approval.CallId));
            var second = await NextTurnAsync(subscriber, seen);

            Assert.Equal(TurnOutcome.Completed, second.Outcome);
            Assert.Empty(second.OpenChildRunIds);
            Assert.Equal(1, First.Runs);
            // The probe ran inside the delivery turn: one pair before it, and no second pair after it.
            var call = Assert.Single(seen.OfType<ToolCallOutput>(), output => output.ToolName.Value == "child_run_result");
            var result = Assert.Single(seen.OfType<ToolResultOutput>(), output => output.ToolName.Value == "child_run_result");
            Assert.Equal(call.CallId, result.CallId);
            var request = Assert.Single(seen.OfType<ToolInteractionRequest>());
            Assert.True(
                seen.IndexOf(call) < seen.IndexOf(request),
                "The delivery pair must come before the probe approval of the same turn.");
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    [Fact]
    public void TurnCompleted_makes_the_open_runs_a_required_property()
    {
        // The compiler must reject a site that forgets the value. A default of an empty list would let a new
        // emit site, or an edit that drops the line, end a headless run early with no signal.
        var property = typeof(TurnCompleted).GetProperty(nameof(TurnCompleted.OpenChildRunIds));
        Assert.NotNull(property);
        Assert.True(
            property.IsDefined(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), inherit: false),
            "TurnCompleted.OpenChildRunIds must be a required property.");
    }

    // ----- Helpers -----

    private static async Task<TurnCompleted> NextTurnAsync(TestProbe subscriber, List<SessionOutput> seen)
        => (TurnCompleted)await PumpAsync(subscriber, seen, output => output is TurnCompleted);

    // Reads session outputs into the list until one matches. The list keeps every output, in order.
    private static async Task<SessionOutput> PumpAsync(
        TestProbe subscriber, List<SessionOutput> seen, Func<SessionOutput, bool> stop)
    {
        SessionOutput? match = null;
        await subscriber.FishForMessageAsync(message =>
        {
            if (message is not SessionOutput output)
                return false;
            seen.Add(output);
            if (!stop(output))
                return false;
            match = output;
            return true;
        }, Deadline, cancellationToken: Cancellation);
        return match!;
    }

    private static string AcceptedRunId(IEnumerable<SessionOutput> seen, string callId)
    {
        var acceptance = Assert.Single(
            seen.OfType<ToolResultOutput>(), output => output.CallId.Value == callId);
        using var json = JsonDocument.Parse(acceptance.Result);
        Assert.Equal("accepted", json.RootElement.GetProperty("state").GetString());
        return json.RootElement.GetProperty("run_id").GetString()!;
    }

    private static IReadOnlyList<string> Ids(TurnCompleted turn)
        => turn.OpenChildRunIds.Select(id => id.Value).ToArray();
}

/// <summary>
/// The skipped-turn site of <see cref="TurnCompleted"/>: the session rejects an input before a model call.
/// A skipped turn does not deliver a child result, so an open run stays open. The model accepts text only,
/// so an image input is rejected.
/// </summary>
public sealed class OpenChildRunsSkippedTurnTests : ChildLifetimeTestBase
{
    public OpenChildRunsSkippedTurnTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "text-only-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Text,
        });
    }

    [Fact]
    public async Task A_skipped_turn_reports_the_open_run()
    {
        // Planted bug: the skipped site of TurnCompleted skips the field, so a headless caller reads zero
        // open runs and exits while a child run still works.
        var sessionId = new SessionId("boundary/open-runs-skipped-turn");
        var childGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-1", "child-a")];
        var subscriber = await JoinFullAsync(sessionId);

        try
        {
            await SendRequesterAsync(sessionId, "Start the child run.");
            var first = await NextTurnAsync(subscriber);

            await SessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "What is in this picture?",
                Source = RequesterSource(Requester),
                MediaReferences =
                [
                    new SerializableMediaReference
                    {
                        RelativePath = "photo.png",
                        MimeType = new MimeType("image/png"),
                        Modality = (int)MediaModality.Image
                    }
                ]
            }, Deadline, Cancellation);
            var skipped = await NextTurnAsync(subscriber);

            Assert.Equal(TurnOutcome.Skipped, skipped.Outcome);
            Assert.Equal(
                first.OpenChildRunIds.Select(id => id.Value).ToArray(),
                skipped.OpenChildRunIds.Select(id => id.Value).ToArray());
            Assert.Single(skipped.OpenChildRunIds);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    private static async Task<TurnCompleted> NextTurnAsync(TestProbe subscriber)
        => await subscriber.FishForMessageAsync<TurnCompleted>(_ => true, Deadline, cancellationToken: Cancellation);
}

/// <summary>
/// The slash-rejection site of <see cref="TurnCompleted"/>: the session rejects an unknown slash command
/// before a model call and reports a skipped turn. A child run that is open must stay in the list.
/// </summary>
public sealed class OpenChildRunsRejectedSlashCommandTests : ChildLifetimeTestBase
{
    public OpenChildRunsRejectedSlashCommandTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);
        // A registry must exist for the session to dispatch a slash command. It holds no skill.
        services.AddSingleton(new SkillRegistry());
    }

    [Fact]
    public async Task An_unknown_slash_command_reports_the_open_run()
    {
        // Planted bug: RejectSlashCommand builds its TurnCompleted without the open runs. A headless caller
        // that sends an unknown command while a child runs then exits at that turn and loses the result.
        var sessionId = new SessionId("boundary/open-runs-unknown-slash");
        var childGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-1", "child-a")];
        var subscriber = await JoinFullAsync(sessionId);

        try
        {
            await SendRequesterAsync(sessionId, "Start the child run.");
            var first = await NextTurnAsync(subscriber);
            var runId = Assert.Single(first.OpenChildRunIds).Value;

            await SendRequesterAsync(sessionId, "/no-such-command now");
            var rejected = await NextTurnAsync(subscriber);

            Assert.Equal(TurnOutcome.Skipped, rejected.Outcome);
            Assert.Equal([runId], rejected.OpenChildRunIds.Select(id => id.Value).ToArray());
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    private static async Task<TurnCompleted> NextTurnAsync(Akka.TestKit.TestProbe subscriber)
        => await subscriber.FishForMessageAsync<TurnCompleted>(_ => true, Deadline, cancellationToken: Cancellation);
}

/// <summary>
/// The routed-turn site of <see cref="TurnCompleted"/>: a slash command that routes to a subagent runs the
/// subagent as a child run. A child run from an earlier turn is still open when that turn completes.
/// </summary>
public sealed class OpenChildRunsRoutedSkillTurnTests : ChildLifetimeTestBase
{
    private const string RoutedSkill = "routed-skill";

    public OpenChildRunsRoutedSkillTurnTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);
        var skillDir = Path.Combine(TestPaths.BasePath, RoutedSkill);
        Directory.CreateDirectory(skillDir);
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillFile, """
            ---
            name: routed-skill
            description: Route to child-a.
            metadata:
              subagent: child-a
            ---

            # Routed

            Do the routed work.
            """);
        var registry = new SkillRegistry();
        registry.Register(new SkillEntry(RoutedSkill, "Routed", "Route to child-a.", skillFile, skillDir, null)
        {
            HasSubagentRoutingMetadata = true,
            Subagent = "child-a"
        });
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task A_routed_skill_turn_reports_the_open_run_from_an_earlier_turn()
    {
        // Planted bug: HandleRoutedSkillExecutionCompleted builds its TurnCompleted without the open runs.
        var sessionId = new SessionId("boundary/open-runs-routed-skill");
        var childGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-1", "child-a")];
        var subscriber = await JoinFullAsync(sessionId);

        try
        {
            await SendRequesterAsync(sessionId, "Start the child run.");
            var first = await NextTurnAsync(subscriber);
            var runId = Assert.Single(first.OpenChildRunIds).Value;

            // The first child call took the gate. The routed run then needs no gate.
            await Clients.Child.FirstCallEntered.Task.WaitAsync(Deadline, Cancellation);
            await AwaitAssertAsync(
                () => Assert.Null(Clients.Child.NextResponseGate), Deadline, cancellationToken: Cancellation);

            await SendRequesterAsync(sessionId, $"/{RoutedSkill} do it");
            var routed = await NextTurnAsync(subscriber);

            // The routed run is a child run too: the session accepts it in this turn and delivers its result in
            // a later turn. So the list holds the earlier run and the routed run.
            Assert.Equal(TurnOutcome.Completed, routed.Outcome);
            var open = routed.OpenChildRunIds.Select(id => id.Value).ToArray();
            Assert.Equal(2, open.Length);
            Assert.Contains(runId, open);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    private static async Task<TurnCompleted> NextTurnAsync(Akka.TestKit.TestProbe subscriber)
        => await subscriber.FishForMessageAsync<TurnCompleted>(_ => true, Deadline, cancellationToken: Cancellation);
}
