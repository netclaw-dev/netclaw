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
    /// Starts a child of Alice. The child waits at its first model call until the spawn turn ends and
    /// <paramref name="beforeChildRuns"/> ran. The agent child-b uses the second model client.
    /// </summary>
    private protected async Task<ChildRunAccepted> StartAsync(SessionId id, string label, string agent = "child-a",
        Func<Task>? beforeChildRuns = null)
    {
        var journal = await JournalAsync();
        ApprovalTool.Entered = () => _resumedAtProbe.Enqueue(journal.Committed.OfType<ChildRunEvent.Resumed>().Count());
        var started = Accepted(journal, id).Count;
        var gate = NewGate();
        (agent == "child-b" ? Clients.B : Clients.A).NextResponseGate = gate;
        await SendAsync(id, $"spawn:{agent}:{label}", AliceSource);
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
        await AwaitAssertAsync(async () => Assert.False(await ChildActorExistsAsync(id, run, null),
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
        for (var incarnation = 0; incarnation <= RequestsOf(journal, run.Run.RunId).Count; incarnation++)
            Assert.False(await ChildActorExistsAsync(id, run, incarnation == 0 ? null : incarnation));
        return terminal;
    }

    // The spawner names the first actor after the agent and the run. A resumed actor adds the prompt counter (D3).
    private async Task<bool> ChildActorExistsAsync(SessionId id, ChildRunAccepted run, int? incarnation)
    {
        var path = $"/user/session-manager/{Uri.EscapeDataString(id.Value)}/subagent-{run.Run.AgentName.Value}-{run.Run.RunId.Value}"
                   + (incarnation is { } number ? $"-{number}" : string.Empty);
        return (await Sys.ActorSelection(path).Ask<ActorIdentity>(new Identify(path), Deadline, Cancellation)).Subject is not null;
    }

    private protected static IReadOnlyList<ChildRunEvent.TerminalRecorded> TerminalsOf(AcceptanceFault journal, SubAgentRunId runId)
        => journal.Committed.OfType<ChildRunEvent.TerminalRecorded>().Where(evt => evt.RunId == runId).ToArray();

    // PR 2 writes the requests of a park only inside its Parked record.
    private protected static IReadOnlyList<ToolApprovalRequested> RequestsOf(AcceptanceFault journal, SubAgentRunId runId)
        => journal.Committed.OfType<ChildRunEvent.Parked>().Where(evt => evt.RunId == runId).SelectMany(evt => evt.Requests).ToArray();

    /// <summary>
    /// The idle timeout passivates the session. The final snapshot and the journal delete run first. A turn that still
    /// runs, for instance a notice turn, ignores the idle timeout, so the spec sends it again until the session stops.
    /// </summary>
    protected async Task PassivateAsync(SessionId id)
    {
        var session = await ResolveSessionAsync(id);
        var stopped = CreateTestProbe();
        stopped.Watch(session);
        await AwaitAssertAsync(async () =>
        {
            session.Tell(ReceiveTimeout.Instance);
            await stopped.ExpectTerminatedAsync(session, TimeSpan.FromSeconds(1), cancellationToken: Cancellation);
        }, Deadline, cancellationToken: Cancellation);
    }

    protected static FunctionCallContent SiblingCall(string callId) => new(callId, SiblingTool.ToolName,
        new Dictionary<string, object?> { ["_rationale"] = "Run beside the consent call." });

    private sealed class OutputRecorder : ReceiveActor
    {
        public OutputRecorder(ConcurrentQueue<object> outputs) => ReceiveAny(outputs.Enqueue);
    }

    /// <summary>A tool that runs beside a consent call. A spec can hold its next call until it releases it.</summary>
    internal sealed class SiblingTool : INetclawTool
    {
        public const string ToolName = "sibling_probe";
        private TaskCompletionSource? _entered;
        private TaskCompletionSource? _release;

        public string Name => ToolName;
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
        public string Description => "Run beside a consent call.";
        public string GrantCategory => "builtin";
        public JsonElement ParameterSchema => default;
        public AITool ToAITool() => AIFunctionFactory.Create(() => "sibling done", Name, Description);
        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => throw new InvalidOperationException("This test requires invocation authority.");
        public Task HoldNext() => (_entered = NewGate(), _release = NewGate()).Item1.Task;
        public void Release() => _release?.TrySetResult();

        public async Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context,
            CancellationToken ct = default)
        {
            _entered?.TrySetResult();
            if (Interlocked.Exchange(ref _release, null) is { } release)
                await release.Task.WaitAsync(ct);
            return "sibling done";
        }
    }
}
