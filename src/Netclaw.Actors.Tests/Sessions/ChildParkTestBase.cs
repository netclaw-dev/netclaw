// -----------------------------------------------------------------------
// <copyright file="ChildParkTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Shared setup for the PR 1 session specs of durable-child-consent-wait (issue #2572). A spec plans the model responses
/// of a child by its task label, which starts with <c>park</c>. Response N answers the request that holds N assistant
/// messages, so a resumed child continues the same plan. A recorder subscribes to the session and keeps every output.
/// </summary>
public abstract class ChildParkTestBase(ITestOutputHelper output) : ChildConsentTestBase(output)
{
    private static readonly Regex ParkLabel = new(@"\[(park[^\]\s]*)\]", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, IReadOnlyList<AIContent>[]> _plans = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<object> _outputs = new();
    // For each run of the approval probe: the count of Resumed records in the journal at that moment.
    private readonly ConcurrentQueue<int> _resumedAtProbe = new();
    private IActorRef? _recorder;
    private protected readonly SiblingTool Sibling = new();
    protected static readonly MessageSource AliceSource = OperatorWithTargets(Alice, AliceTarget, AliceRequestedTarget);

    protected override void RegisterParentTools(Netclaw.Actors.Tools.ToolRegistry registry) => registry.RegisterCore(Sibling);

    protected void PlanChild(string label, params IReadOnlyList<AIContent>[] rounds)
    {
        _plans[label] = rounds;
        Clients.A.ResponseContentsSelector = Clients.B.ResponseContentsSelector = request =>
        {
            var task = request.FirstOrDefault(static message => message.Role == AiChatRole.User)?.Text ?? string.Empty;
            if (ParkLabel.Match(task) is not { Success: true } match || !_plans.TryGetValue(match.Groups[1].Value, out var plan))
                return null;
            var round = request.Count(static message => message.Role == AiChatRole.Assistant);
            return round < plan.Length ? plan[round] : [new TextContent($"{ChildOutput} {match.Groups[1].Value}")];
        };
    }

    /// <summary>Joins the output recorder to the session. A spec joins again after a restart.</summary>
    protected async Task JoinRecorderAsync(SessionId id)
    {
        _recorder ??= Sys.ActorOf(Props.Create(() => new OutputRecorder(_outputs)));
        var joined = Outputs<SessionJoined>().Count;
        SessionManager.Tell(new JoinSession(_recorder) { SessionId = id, Filter = OutputFilter.Full });
        await AwaitAssertAsync(() => Assert.True(Outputs<SessionJoined>().Count > joined), Deadline, cancellationToken: Cancellation);
    }

    protected IReadOnlyList<T> Outputs<T>() => _outputs.OfType<T>().ToArray();

    /// <summary>
    /// Starts a child of Alice, or of <paramref name="source"/>. The child waits at its first model call until the spawn
    /// turn ends and <paramref name="beforeChildRuns"/> ran. The agent child-b uses the second model client.
    /// </summary>
    private protected async Task<ChildRunAccepted> StartAsync(SessionId id, string label, string agent = "child-a",
        Func<Task>? beforeChildRuns = null, MessageSource? source = null)
    {
        var journal = await JournalAsync();
        ApprovalTool.Entered = () => _resumedAtProbe.Enqueue(journal.Committed.OfType<ChildRunEvent.Resumed>().Count());
        var started = Accepted(journal, id).Count;
        var gate = NewGate();
        (agent == "child-b" ? Clients.B : Clients.A).NextResponseGate = gate;
        await SendAsync(id, $"spawn:{agent}:{label}", source ?? AliceSource);
        await AwaitAssertAsync(() => Assert.Equal(started + 1, Accepted(journal, id).Count), Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, id);
        if (beforeChildRuns is not null)
            await beforeChildRuns();
        gate.TrySetResult();
        return Accepted(journal, id)[started];
    }

    // Holds the next parent model request that carries the text, so a turn stays open. The caller releases it.
    private protected async Task<TaskCompletionSource> HoldTurnAsync(AcceptanceFault journal, SessionId id, string text, MessageSource source)
    {
        await SettleNoticesAsync(journal, id);
        var hold = NewGate();
        var entered = ParentGate.Arm(request => FreshUserTexts(request).Contains(text), hold);
        await SendAsync(id, text, source);
        await entered.WaitAsync(Deadline, Cancellation);
        return hold;
    }

    protected async Task<ToolInteractionRequest[]> AwaitPromptsAsync(SubAgentRunId runId, int count)
    {
        await AwaitAssertAsync(() => Assert.True(PromptsOf(runId).Length >= count), Deadline, cancellationToken: Cancellation);
        return PromptsOf(runId);
    }

    private protected ToolInteractionRequest[] PromptsOf(SubAgentRunId runId) => Outputs<ToolInteractionRequest>()
        .Where(prompt => prompt.CallId.Value.StartsWith($"{runId.Value}/subagent-approval/", StringComparison.Ordinal))
        .DistinctBy(static prompt => prompt.CallId).ToArray();

    /// <summary>
    /// The park precondition (design D1, PR 2 design D4): the requests are journaled in a Parked record, and the child
    /// actor is gone.
    /// </summary>
    private protected async Task AssertParkedAsync(AcceptanceFault journal, SessionId id, ChildRunAccepted run, int requests)
    {
        await AwaitAssertAsync(() => Assert.Equal(requests, RequestsOf(journal, run.Run.RunId).Count), Deadline,
            cancellationToken: Cancellation);
        await AwaitAssertAsync(async () => Assert.False(await ChildActorExistsAsync(journal, id, run),
            "The child must park and stop."), TimeSpan.FromSeconds(10), cancellationToken: Cancellation);
    }

    /// <summary>
    /// The invariants of every run (cut plan S1 to S4): one terminal record inside the deadline, each parked call runs
    /// at most <paramref name="probeRuns"/> times under the run authority, each request has a close, no parent turn
    /// failed, and no child actor of the run stays. PR 2: each parked call runs after a committed Resumed record, and
    /// the run has no more Resumed records than Parked records.
    /// </summary>
    private protected async Task<ChildRunTerminal> AssertRunInvariantsAsync(AcceptanceFault journal, SessionId id,
        ChildRunAccepted run, int probeRuns)
    {
        await AwaitAssertAsync(() => Assert.NotEmpty(TerminalsOf(journal, run.Run.RunId)), Deadline, cancellationToken: Cancellation);
        await BarrierAsync(id, run);
        var terminal = Assert.Single(TerminalsOf(journal, run.Run.RunId)).Terminal;
        var authority = run.Run.OriginalContext;
        Assert.Equal(probeRuns, ApprovalTool.Contexts.Count);
        Assert.All(ApprovalTool.Contexts, context =>
        {
            Assert.Equal(authority.Audience, context.Audience);
            Assert.Equal(authority.Boundary, context.Boundary);
            Assert.Equal(authority.DefaultDeliveryTarget, context.DefaultDeliveryTarget);
            Assert.Equal(authority.RequestedDeliveryTarget, context.RequestedDeliveryTarget);
        });
        Assert.All(_resumedAtProbe, resumed => Assert.True(resumed > 0, "A parked call ran before the journal held a Resumed record."));
        var parks = journal.Committed.OfType<ChildRunEvent.Parked>().Count(evt => evt.RunId == run.Run.RunId);
        var resumes = journal.Committed.OfType<ChildRunEvent.Resumed>().Count(evt => evt.RunId == run.Run.RunId);
        Assert.True(resumes <= parks, $"The run has {resumes} Resumed records for {parks} Parked records.");
        await AwaitAssertAsync(() => Assert.All(RequestsOf(journal, run.Run.RunId), request =>
            Assert.Contains(Outputs<ToolInteractionClosed>(), closed => closed.CallId.Value == request.CallId)),
            Deadline, cancellationToken: Cancellation);
        Assert.DoesNotContain(Outputs<TurnCompleted>(), static turn => turn.Outcome == TurnOutcome.Failed);
        Assert.False(await ChildActorExistsAsync(journal, id, run), "A child actor of the run is still alive after the run ended.");
        return terminal;
    }

    // The spawner names the first actor after the agent and the run. A resumed actor adds the prompt counter (D3). A
    // drain park adds no request, so its resume can take the suffix -0 (running-child-drain-park design D1).
    private async Task<bool> ChildActorExistsAsync(AcceptanceFault journal, SessionId id, ChildRunAccepted run)
    {
        var name = $"/user/session-manager/{Uri.EscapeDataString(id.Value)}/subagent-{run.Run.AgentName.Value}-{run.Run.RunId.Value}";
        var paths = Enumerable.Range(0, RequestsOf(journal, run.Run.RunId).Count + 1).Select(prompts => $"{name}-{prompts}").Prepend(name);
        foreach (var path in paths)
        {
            if ((await Sys.ActorSelection(path).Ask<ActorIdentity>(new Identify(path), Deadline, Cancellation)).Subject is not null)
                return true;
        }

        return false;
    }

    /// <summary>The drain request, the virtual time when it started, and the child model calls at that time.</summary>
    private protected sealed record DrainStart(Task<DaemonRestartPrepared> Ack, DateTimeOffset StartedAt, int ChildCalls);

    /// <summary>The drain park of the run (null when the child did not park), and the child model calls at the park.</summary>
    private protected sealed record DrainPark(ChildRunEvent.Parked? Park, int ChildCalls);

    /// <summary>
    /// The daemon drains the session. The method returns after the session took the drain request and logged its move to
    /// Passivating. The session sends the park signal to each live child in that same message (design D4), so the
    /// signal is in the child mailbox before the caller releases a held tool.
    /// </summary>
    private protected async Task<DrainStart> BeginDrainAsync(SessionId id)
    {
        var started = new DrainStart(AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(id, "daemon-stop")),
            Sys.Scheduler.Now, Clients.A.CallCount);
        await AwaitAssertAsync(async () => Assert.Equal("Passivating", await PhaseOfAsync(id)), Deadline,
            cancellationToken: Cancellation);
        return started;
    }

    /// <summary>
    /// The precondition of a drain park (running-child-drain-park task 1.3), after the drain acknowledgement:
    /// <list type="bullet">
    /// <item>the acknowledgement arrived before the drain deadline;</item>
    /// <item>it carries a restart reminder when no request of the run is open (PR 2 CountsAsUnreported);</item>
    /// <item>the journal holds one Parked record of the run with <paramref name="requests"/> requests, or none when
    /// <paramref name="requests"/> is null, and no Resumed or terminal record of the run;</item>
    /// <item>the child started no model call after the drain, and no child actor of the run and no session is alive.</item>
    /// </list>
    /// </summary>
    private protected async Task<DrainPark> AssertDrainParkAsync(AcceptanceFault journal, SessionId id, ChildRunAccepted run,
        int? requests, DrainStart drain)
    {
        var ack = await drain.Ack;
        var waited = Sys.Scheduler.Now - drain.StartedAt;
        Assert.True(waited < DaemonConfig.BoundedDrainTimeout, $"The session acknowledged the drain after {waited}.");
        var parks = journal.Committed.OfType<ChildRunEvent.Parked>().Where(evt => evt.RunId == run.Run.RunId).ToArray();
        ChildRunEvent.Parked? park = null;
        if (requests is { } count)
            Assert.Equal(count, (park = Assert.Single(parks)).Requests.Count);
        else
            Assert.Empty(parks);
        if (requests is null or 0)
            Assert.NotNull(ack.RestartReminder);
        else
            Assert.Null(ack.RestartReminder);
        Assert.DoesNotContain(journal.Committed.OfType<ChildRunEvent.Resumed>(), evt => evt.RunId == run.Run.RunId);
        Assert.Empty(TerminalsOf(journal, run.Run.RunId));
        Assert.Equal(drain.ChildCalls, Clients.A.CallCount);
        var session = $"/user/session-manager/{Uri.EscapeDataString(id.Value)}";
        await AwaitAssertAsync(async () =>
        {
            Assert.False(await ChildActorExistsAsync(journal, id, run), "A child actor of the run is alive after the drain.");
            Assert.Null((await Sys.ActorSelection(session).Ask<ActorIdentity>(new Identify(session), Deadline, Cancellation)).Subject);
        }, Deadline, cancellationToken: Cancellation);
        return new DrainPark(park, Clients.A.CallCount);
    }

    /// <summary>
    /// The postcondition of a drain park (running-child-drain-park task 1.3), after the run ended: the run invariants;
    /// each sibling call ID ran at most once over all incarnations, under the recorded authority of the run; and the
    /// first model request of a child that resumed a drain park holds the transcript of the park. With no park, no child
    /// started again.
    /// </summary>
    private protected async Task<ChildRunTerminal> AssertDrainResumeAsync(AcceptanceFault journal, SessionId id,
        ChildRunAccepted run, DrainPark park, int probeRuns)
    {
        var terminal = await AssertRunInvariantsAsync(journal, id, run, probeRuns);
        Assert.All(Sibling.Calls.GroupBy(static call => call.CallId), calls => Assert.Single(calls));
        var authority = run.Run.OriginalContext;
        Assert.All(Sibling.Calls, call =>
        {
            Assert.Equal(authority.Audience, call.Context.Audience);
            Assert.Equal(authority.Boundary, call.Context.Boundary);
            Assert.Equal(authority.DefaultDeliveryTarget, call.Context.DefaultDeliveryTarget);
            Assert.Equal(authority.RequestedDeliveryTarget, call.Context.RequestedDeliveryTarget);
        });
        if (park.Park is null)
            Assert.Equal(park.ChildCalls, Clients.A.CallCount);
        else if (park.Park.Requests.Count == 0)
        {
            // The request holds the system message of the child and then the transcript of the park.
            var resumed = Clients.A.ReceivedMessages[park.ChildCalls];
            Assert.Equal(park.Park.Park.Transcript.Select(static message => Shape(ChatMessageConverter.ToAiMessage(message))),
                resumed.Skip(1).Select(Shape));
        }
        return terminal;
    }

    // The parts of a model message that the park keeps: the role, the text, and the call IDs of each call and result.
    private static string Shape(ChatMessage message) => string.Join('|', message.Role.Value, message.Text,
        string.Join(',', message.Contents.OfType<FunctionCallContent>().Select(static call => call.CallId)),
        string.Join(',', message.Contents.OfType<FunctionResultContent>().Select(static result => result.CallId)));

    private protected static IReadOnlyList<ChildRunEvent.TerminalRecorded> TerminalsOf(AcceptanceFault journal, SubAgentRunId runId)
        => journal.Committed.OfType<ChildRunEvent.TerminalRecorded>().Where(evt => evt.RunId == runId).ToArray();

    // PR 2 writes the requests of a park only inside its Parked record.
    private protected static IReadOnlyList<ToolApprovalRequested> RequestsOf(AcceptanceFault journal, SubAgentRunId runId)
        => journal.Committed.OfType<ChildRunEvent.Parked>().Where(evt => evt.RunId == runId).SelectMany(evt => evt.Requests).ToArray();

    /// <summary>
    /// The idle timeout passivates the session. The final snapshot and the journal delete run first. A turn that still
    /// runs, for instance a notice turn, ignores the idle timeout, so the spec sends it again until the session stops.
    /// On the virtual scheduler each try also moves the clock past the passivation timers.
    /// </summary>
    protected async Task PassivateAsync(SessionId id)
    {
        var session = await ResolveSessionAsync(id);
        var stopped = CreateTestProbe();
        stopped.Watch(session);
        await AwaitAssertAsync(async () =>
        {
            session.Tell(ReceiveTimeout.Instance);
            if (UseTestScheduler)
                AdvanceScheduler(TimeSpan.FromSeconds(1));
            await stopped.ExpectTerminatedAsync(session, TimeSpan.FromMilliseconds(UseTestScheduler ? 100 : 1000),
                cancellationToken: Cancellation);
        }, Deadline, cancellationToken: Cancellation);
    }

    // The call ID travels as an argument too, because the tool context does not hold it.
    protected static FunctionCallContent SiblingCall(string callId) => new(callId, SiblingTool.ToolName,
        new Dictionary<string, object?> { ["call"] = callId, ["_rationale"] = "Run beside the consent call." });

    private sealed class OutputRecorder : ReceiveActor
    {
        public OutputRecorder(ConcurrentQueue<object> outputs) => ReceiveAny(outputs.Enqueue);
    }

    /// <summary>
    /// A tool that runs beside a consent call. A spec can hold its next call until it releases it. The tool records each
    /// call that starts, with its call ID and its context.
    /// </summary>
    internal sealed class SiblingTool : INetclawTool
    {
        public const string ToolName = "sibling_probe";
        public ConcurrentQueue<(string? CallId, ToolInvocationContext Context)> Calls { get; } = new();
        private TaskCompletionSource? _entered;
        private TaskCompletionSource? _release;
        // The gate of the held call. The call takes _release, so Release needs its own reference.
        private TaskCompletionSource? _held;

        public string Name => ToolName;
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
        public string Description => "Run beside a consent call.";
        public string GrantCategory => "builtin";
        public JsonElement ParameterSchema => default;
        public AITool ToAITool() => AIFunctionFactory.Create(() => "sibling done", Name, Description);
        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => throw new InvalidOperationException("This test requires invocation authority.");
        public Task HoldNext() => (_entered = NewGate(), _release = _held = NewGate()).Item1.Task;
        public void Release() => _held?.TrySetResult();

        public async Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context,
            CancellationToken ct = default)
        {
            Calls.Enqueue((arguments?.TryGetValue("call", out var call) == true ? call?.ToString() : null, context));
            _entered?.TrySetResult();
            if (Interlocked.Exchange(ref _release, null) is { } release)
                await release.Task.WaitAsync(ct);
            return "sibling done";
        }
    }
}
