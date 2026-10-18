// -----------------------------------------------------------------------
// <copyright file="SubAgentThinkingCapTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.SubAgents;
using Xunit;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// Thinking counts as progress for the sub-agent no-progress deadline, but one model
/// call that streams only thinking fails after three times that deadline with reason
/// <c>thinking_only_timeout</c>. A text delta or a tool-call delta restarts the cap.
/// Production case: child 878043df thought for 92 minutes in one call.
/// <para>
/// The ActorSystem runs on <see cref="Akka.TestKit.TestScheduler"/>, so virtual time
/// moves only on <c>Advance</c>. The stream ping throttle of the sub-agent reads the
/// same virtual clock. A test pushes one stream update, waits until the stream reader
/// handed it over, and then round-trips an <see cref="Identify"/> message. The ping
/// goes to the actor mailbox before the reader asks for the next update, so the
/// Identify reply proves the actor handled the ping before the next time step.
/// </para>
/// </summary>
public sealed class SubAgentThinkingCapTests : TestKit
{
    // NoProgress is 10 s, so the cap is 30 s. A 7 s step is under the deadline (each
    // thinking ping keeps the call alive) and it never lands on the cap boundary:
    // 28 s is under the cap and 35 s is over it.
    private static readonly TimeSpan NoProgress = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan LivenessBudget = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FaultCeiling = TimeSpan.FromSeconds(6);

    private const string ThinkingOnlyTimeoutReason = "thinking_only_timeout";
    private const string NoProgressReason = "no_substantive_output_timeout";

    private readonly ScriptedStreamingChatClient _client = new();
    private TestProbe _results = null!;
    private IActorRef _agent = ActorRefs.Nobody;
    private int _identifyCount;

    public SubAgentThinkingCapTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.AddHocon(
            "akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\"",
            HoconAddMode.Prepend);
    }

    [Fact]
    public async Task Thinking_only_call_fails_with_thinking_only_timeout_after_three_times_the_deadline()
    {
        await StartRunAsync();

        // Thinking keeps the call alive past the 10 s deadline: 28 s of virtual time.
        await StreamThinkingForAsync(steps: 4);
        Assert.False(_results.HasMessages);

        // 35 s is past the 30 s cap and no text or tool call arrived.
        Advance(Step);

        var result = await _results.ExpectMsgAsync<SubAgentResult>(
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal(SubAgentRunOutcome.Failed, result.Outcome);
        Assert.Equal(ThinkingOnlyTimeoutReason, result.OutcomeReason?.Value);
        Assert.Contains("thinking", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Thinking_shorter_than_the_cap_is_not_stopped()
    {
        await StartRunAsync();

        // 28 s of thinking is over the 10 s deadline but under the 30 s cap.
        await StreamThinkingForAsync(steps: 4);
        _client.PushFinalText("Final answer after long thinking.");

        var result = await _results.ExpectMsgAsync<SubAgentResult>(
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal(SubAgentRunOutcome.Completed, result.Outcome);
        Assert.Null(result.OutcomeReason);
        Assert.Contains("Final answer after long thinking.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_delta_restarts_the_thinking_cap()
    {
        await StartRunAsync();

        // 28 s of thinking, a text delta, then 28 s more thinking: 56 s in total.
        // The cap counts from the text delta, so the call is alive at 56 s (28 s of
        // thinking since the text) and over the cap at 63 s (35 s since the text).
        await StreamThinkingForAsync(steps: 4);
        await PushAndSettleAsync(TextUpdate("Draft: outline"));
        await StreamThinkingForAsync(steps: 4);
        Assert.False(_results.HasMessages);

        Advance(Step);

        var result = await _results.ExpectMsgAsync<SubAgentResult>(
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ThinkingOnlyTimeoutReason, result.OutcomeReason?.Value);
    }

    [Fact]
    public async Task Empty_text_next_to_thinking_does_not_restart_the_thinking_cap()
    {
        await StartRunAsync();

        // 28 s of thinking deltas, each with an empty text part, as a backend that pads
        // every reasoning chunk with content "" sends them. That is not answer text.
        await PushAndSettleAsync(ThinkingWithEmptyText());
        for (var i = 0; i < 4; i++)
        {
            Advance(Step);
            await PushAndSettleAsync(ThinkingWithEmptyText());
        }
        Assert.False(_results.HasMessages);

        // 35 s is past the 30 s cap and no real text or tool call arrived.
        Advance(Step);

        var result = await _results.ExpectMsgAsync<SubAgentResult>(
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ThinkingOnlyTimeoutReason, result.OutcomeReason?.Value);
    }

    [Fact]
    public async Task Tool_call_delta_restarts_the_thinking_cap()
    {
        await StartRunAsync();

        await StreamThinkingForAsync(steps: 4);
        var toolCall = new FunctionCallContent("call-1", "web_search",
            new Dictionary<string, object?> { ["query"] = "scene" });
        await PushAndSettleAsync(new ChatResponseUpdate
        {
            Role = Microsoft.Extensions.AI.ChatRole.Assistant,
            Contents = [toolCall]
        });
        await StreamThinkingForAsync(steps: 4);
        Assert.False(_results.HasMessages);

        Advance(Step);

        var result = await _results.ExpectMsgAsync<SubAgentResult>(
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ThinkingOnlyTimeoutReason, result.OutcomeReason?.Value);
    }

    [Fact]
    public async Task Call_with_no_deltas_still_fails_with_the_no_progress_reason()
    {
        await StartRunAsync();

        // No delta of any kind: the plain no-progress deadline (10 s) fires first.
        Advance(TimeSpan.FromSeconds(11));

        var result = await _results.ExpectMsgAsync<SubAgentResult>(
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal(NoProgressReason, result.OutcomeReason?.Value);
        Assert.Contains("no substantive output", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Answer_delta_inside_the_ping_throttle_window_still_restarts_the_thinking_cap()
    {
        await StartRunAsync();

        // Thinking deltas arrive once for each second. The throttle (2 s) lets a ping
        // through every second delta, so the last ping goes out at 28 s.
        await StreamThinkingForAsync(steps: 28, step: TimeSpan.FromSeconds(1));

        // 0.5 s after that ping, inside the throttle window, a text delta arrives. It
        // must reach the watchdog now. If the throttle held it back, the cap would
        // stop the call at 30 s while the model is answering.
        Advance(TimeSpan.FromSeconds(0.5));
        await PushAndSettleAsync(TextUpdate("Draft: outline"));

        // 28 s more thinking, ending at 56.5 s: 28.5 s since the text delta.
        await StreamThinkingForAsync(steps: 28, step: TimeSpan.FromSeconds(1));
        Assert.False(_results.HasMessages);

        _client.PushFinalText(" and the final answer.");

        var result = await _results.ExpectMsgAsync<SubAgentResult>(
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal(SubAgentRunOutcome.Completed, result.Outcome);
        Assert.Contains("final answer", result.Output, StringComparison.Ordinal);
    }

    private async Task StartRunAsync()
    {
        _results = CreateTestProbe();
        _agent = Sys.ActorOf(SubAgentActor.CreateProps(
            new SubAgentDefinition
            {
                Name = new AgentName("test-agent"),
                SystemPrompt = "You are a test agent.",
                Tools = [],
                EmitStructuredFindings = false
            },
            _client,
            SubAgentActorTests.PermissivePolicy()));

        _agent.Tell(new RunSubAgent
        {
            Scope = SubAgentTestScope.Create(),
            Task = "Write the scene file",
            // Liveness budgets are far above the test span, so only the no-progress
            // deadline and the thinking cap can end the call.
            Timeout = LivenessBudget,
            PrefillTimeout = LivenessBudget,
            NoProgressTimeout = NoProgress
        }, _results);

        // The watchdog is armed before the stream is invoked, so the first step is safe.
        await _client.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Streams one thinking delta at the current virtual time, then moves virtual time
    /// by one step before each further delta.
    /// </summary>
    private async Task StreamThinkingForAsync(int steps, TimeSpan? step = null)
    {
        await PushAndSettleAsync(ThinkingUpdate("thinking"));
        for (var i = 0; i < steps; i++)
        {
            Advance(step ?? Step);
            await PushAndSettleAsync(ThinkingUpdate("thinking"));
        }
    }

    private async Task PushAndSettleAsync(ChatResponseUpdate update)
    {
        await _client.PushAsync(update, TestContext.Current.CancellationToken);

        // The actor handles the ping before it answers Identify (same mailbox, same order).
        var id = ++_identifyCount;
        _agent.Tell(new Identify(id), TestActor);
        var identity = await ExpectMsgAsync<ActorIdentity>(
            FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(id, identity.MessageId);
    }

    private void Advance(TimeSpan offset) => ((TestScheduler)Sys.Scheduler).Advance(offset);

    private static ChatResponseUpdate ThinkingUpdate(string text)
        => new()
        {
            Role = Microsoft.Extensions.AI.ChatRole.Assistant,
            Contents = [new TextReasoningContent(text)]
        };

    private static ChatResponseUpdate ThinkingWithEmptyText()
        => new()
        {
            Role = Microsoft.Extensions.AI.ChatRole.Assistant,
            Contents = [new TextReasoningContent("thinking"), new TextContent("")]
        };

    private static ChatResponseUpdate TextUpdate(string text)
        => new()
        {
            Role = Microsoft.Extensions.AI.ChatRole.Assistant,
            Contents = [new TextContent(text)]
        };

    /// <summary>
    /// A model stream that the test drives by hand. <see cref="PushAsync"/> returns after
    /// the stream reader has handled the update and asked for the next one.
    /// </summary>
    private sealed class ScriptedStreamingChatClient : IChatClient
    {
        private readonly Channel<int> _invocations =
            Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Channel<ChatResponseUpdate> _updates = Channel.CreateUnbounded<ChatResponseUpdate>();
        private readonly Channel<int> _handled = Channel.CreateUnbounded<int>();
        private int _callCount;

        public async Task WaitForStreamInvocationAsync(CancellationToken cancellationToken)
            => await _invocations.Reader.ReadAsync(cancellationToken);

        public async Task PushAsync(ChatResponseUpdate update, CancellationToken cancellationToken)
        {
            _updates.Writer.TryWrite(update);

            // If the actor ended the call, the reader never handles this update. The
            // fault ceiling turns that hang into a clear failure.
            using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ceiling.CancelAfter(FaultCeiling);
            await _handled.Reader.ReadAsync(ceiling.Token);
        }

        public void PushFinalText(string text)
        {
            _updates.Writer.TryWrite(new ChatResponseUpdate
            {
                Role = Microsoft.Extensions.AI.ChatRole.Assistant,
                Contents = [new TextContent(text)],
                FinishReason = ChatFinishReason.Stop
            });
            _updates.Writer.TryComplete();
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(new NotSupportedException("Streaming path only."));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call > 1)
                throw new InvalidOperationException("The scripted stream serves one model call.");

            _invocations.Writer.TryWrite(call);
            return ReadScriptedAsync(cancellationToken);
        }

        private async IAsyncEnumerable<ChatResponseUpdate> ReadScriptedAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update;

                // Code after the yield runs when the reader asks for the next update,
                // which is after it handled this update.
                _handled.Writer.TryWrite(1);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
