// -----------------------------------------------------------------------
// <copyright file="LlmSessionEmptyTextCapTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Hosting;
using Akka.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// An empty text part is not answer text. A backend can send <c>TextContent("")</c>
/// next to every reasoning chunk. Such a delta must not restart the thinking cap, or a
/// call that only thinks is never stopped. The ActorSystem runs on
/// <see cref="Akka.TestKit.TestScheduler"/>: virtual time moves only on
/// <c>AdvanceScheduler</c>.
/// </summary>
public sealed class LlmSessionEmptyTextCapTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    // Time is virtual. NoProgress is 10 s, so the cap is 30 s. A 7 s step is under the
    // deadline (each thinking delta keeps the call alive) and never lands on the cap
    // boundary: 28 s is under the cap and 35 s is over it.
    private static readonly TimeSpan NoProgress = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan LivenessBudget = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FaultCeiling = TimeSpan.FromSeconds(6);

    private readonly ScriptedStreamingChatClient _chatClient = new();
    private readonly List<SessionOutput> _seen = [];
    private TestProbe _subscriber = null!;

    protected override bool UseTestScheduler => true;

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "empty-text-cap-test-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            // Liveness budgets are far above the test span, so only the no-progress
            // deadline and the thinking cap can end the call.
            PrefillTimeout = LivenessBudget,
            FirstTokenTimeout = LivenessBudget,
            NoProgressTimeout = NoProgress,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(new FakeToolExecutor());

        var registry = new ToolRegistry();
        registry.RegisterCore(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Empty_text_next_to_thinking_does_not_restart_the_thinking_cap()
    {
        await StartTurnAsync("empty-text-cap/stop");

        // 28 s of thinking deltas, each with an empty text part: under the 30 s cap.
        await StreamThinkingWithEmptyTextForAsync(steps: 4);

        // 35 s is past the cap. No real text or tool call arrived.
        AdvanceScheduler(Step);

        var error = await ReadUntilAsync(output => output is ErrorOutput) as ErrorOutput;
        Assert.NotNull(error);
        // The provider streamed without fault. The model gave no answer, so the model gets the blame.
        Assert.Equal(ErrorCategory.ModelBehavior, error.Category);
        Assert.Contains("thinking", error.Message, StringComparison.OrdinalIgnoreCase);
        await ReadUntilAsync(output => output is TurnCompleted);
    }

    private async Task StartTurnAsync(string sessionIdValue)
    {
        var sessionId = new SessionId(sessionIdValue);
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        _subscriber = CreateTestProbe("empty-text-cap-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(_subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Text | OutputFilter.TextStreaming | OutputFilter.Thinking | OutputFilter.ToolCalls
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await _subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Plan the scene"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // The watchdog is armed before the stream is invoked, so the first step is safe.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Streams thinking deltas at virtual time 0, then moves virtual time by one step
    /// before each further delta. Every delta also carries an empty text part, as a
    /// backend that pads each reasoning chunk with <c>content: ""</c> does.
    /// </summary>
    private async Task StreamThinkingWithEmptyTextForAsync(int steps)
    {
        // The session buffers the first thinking delta and shows it with the second.
        _chatClient.Push(ThinkingWithEmptyText("first-0"));
        _chatClient.Push(ThinkingWithEmptyText("first-1"));
        await ReadUntilAsync(output => IsThinkingDelta(output, "first-1"));

        for (var step = 1; step <= steps; step++)
        {
            AdvanceScheduler(Step);
            var marker = $"first-step-{step}";
            _chatClient.Push(ThinkingWithEmptyText(marker));
            await ReadUntilAsync(output => IsThinkingDelta(output, marker));
        }
    }

    private static bool IsThinkingDelta(SessionOutput output, string marker)
        => output is ThinkingDeltaOutput delta && delta.Delta.Contains(marker, StringComparison.Ordinal);

    private async Task<SessionOutput> ReadUntilAsync(Func<SessionOutput, bool> predicate)
    {
        while (true)
        {
            var output = await _subscriber.ExpectMsgAsync<SessionOutput>(
                FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
            _seen.Add(output);
            if (predicate(output))
                return output;
        }
    }

    private static ChatResponseUpdate ThinkingWithEmptyText(string text)
        => new() { Role = AiChatRole.Assistant, Contents = [new TextReasoningContent(text), new TextContent("")] };

    private sealed class ScriptedStreamingChatClient : IChatClient
    {
        private readonly Channel<int> _invocations =
            Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Channel<ChatResponseUpdate> _updates = Channel.CreateUnbounded<ChatResponseUpdate>();
        private int _callCount;

        public async Task WaitForStreamInvocationAsync(CancellationToken cancellationToken)
            => await _invocations.Reader.ReadAsync(cancellationToken);

        public void Push(ChatResponseUpdate update) => _updates.Writer.TryWrite(update);

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
            _invocations.Writer.TryWrite(call);

            return ReadScriptedAsync(cancellationToken);
        }

        private async IAsyncEnumerable<ChatResponseUpdate> ReadScriptedAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
