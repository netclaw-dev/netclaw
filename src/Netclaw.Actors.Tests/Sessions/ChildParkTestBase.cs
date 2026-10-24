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
    private IActorRef? _recorder;
    private protected readonly SiblingTool Sibling = new();
    protected static readonly MessageSource AliceSource = OperatorWithTargets(Alice, AliceTarget, AliceRequestedTarget);

    protected override void RegisterParentTools(Netclaw.Actors.Tools.ToolRegistry registry) => registry.RegisterCore(Sibling);

    protected void PlanChild(string label, params IReadOnlyList<AIContent>[] rounds)
    {
        _plans[label] = rounds;
        Clients.A.ResponseContentsSelector = request =>
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
    /// <paramref name="beforeChildRuns"/> ran.
    /// </summary>
    private protected async Task<ChildRunAccepted> StartAsync(SessionId id, string label, string agent = "child-a",
        Func<Task>? beforeChildRuns = null)
    {
        var journal = await JournalAsync();
        var gate = NewGate();
        Clients.A.NextResponseGate = gate;
        await SendAsync(id, $"spawn:{agent}:{label}", AliceSource);
        await AwaitAssertAsync(() => Assert.Single(Accepted(journal, id)), Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, id);
        if (beforeChildRuns is not null)
            await beforeChildRuns();
        gate.TrySetResult();
        return Accepted(journal, id)[0];
    }

    protected async Task<ToolInteractionRequest[]> AwaitPromptsAsync(SubAgentRunId runId, int count)
    {
        await AwaitAssertAsync(() => Assert.True(PromptsOf(runId).Length >= count), Deadline, cancellationToken: Cancellation);
        return PromptsOf(runId);
    }

    private ToolInteractionRequest[] PromptsOf(SubAgentRunId runId) => Outputs<ToolInteractionRequest>()
        .Where(prompt => prompt.CallId.Value.StartsWith($"{runId.Value}/subagent-approval/", StringComparison.Ordinal))
        .DistinctBy(static prompt => prompt.CallId).ToArray();

    /// <summary>The park precondition (design D1): the requests are journaled, and the child actor is gone.</summary>
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
    /// failed, and no child actor of the run stays.
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

    private protected static IReadOnlyList<ToolApprovalRequested> RequestsOf(AcceptanceFault journal, SubAgentRunId runId)
        => journal.Committed.OfType<ToolApprovalRequested>().Where(evt => evt.SourceChildRunId == runId).ToArray();

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
