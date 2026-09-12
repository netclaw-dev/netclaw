// -----------------------------------------------------------------------
// <copyright file="LlmSessionStreamingTimeoutTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Runs the ActorSystem on <see cref="Akka.TestKit.TestScheduler"/> so the
/// watchdog's timer fires only on an explicit <c>AdvanceScheduler</c> — no
/// wall-clock race against threadpool scheduling. The chat client signals when its
/// streaming method is invoked; that happens strictly after the watchdog is armed,
/// so the test only advances once the timer exists. The success path advances
/// nothing, so a healthy stream can never be spuriously timed out under load.
/// </summary>
public sealed class LlmSessionStreamingTimeoutTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private static readonly TimeSpan FirstTokenTimeout = TimeSpan.FromSeconds(2);
    private readonly StreamingTimeoutTestChatClient _chatClient = new();

    protected override bool UseTestScheduler => true;

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "streaming-timeout-test-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            PrefillTimeout = FirstTokenTimeout,
            FirstTokenTimeout = FirstTokenTimeout,
            ToolExecutionTimeout = TimeSpan.FromSeconds(10),
            SidecarLlmTimeout = TimeSpan.FromSeconds(10),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
                // These tests target the watchdog's own timeout-detection behavior
                // (arm/promote/fire), not turn-level resume — disable resume so a
                // single watchdog expiry fails the turn exactly as before that
                // feature existed. See LlmTurnResumeTests for resume coverage.
                TimeoutResumeRetryBudget = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
    }

    [Fact]
    public async Task Timeout_fires_when_no_deltas_arrive()
    {
        _chatClient.Mode = StreamMode.HangForever;

        var sessionId = new SessionId("streaming-timeout/no-deltas");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("no-delta-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // The watchdog is armed before the stream is invoked; advance only after.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        AdvanceScheduler(FirstTokenTimeout);

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.Timeout, error.Category);
        Assert.Contains("timed out", error.Message);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Timeout_resets_on_delta_and_fires_after_silence()
    {
        // Emit deltas (each resets the liveness budget) then go silent; the timeout
        // fires one budget after the last delta.
        _chatClient.Mode = StreamMode.EmitThenHang;

        var sessionId = new SessionId("streaming-timeout/delta-then-silence");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("delta-silence-sub");

        // Subscribe to streaming deltas so we can observe delta PROCESSING. The
        // watchdog re-arms on each delta before that delta is emitted, so receiving
        // the last delta proves the last re-arm has run — only then is it safe to
        // advance (a delta processed after the advance would re-arm the liveness
        // timer past it, and the timeout would never fire). ErrorOutput/TurnCompleted
        // are emitted with no required flag, so they arrive regardless of filter.
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextStreaming
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(
            m => m is TextDeltaOutput d && d.Delta.Contains("chunk3", StringComparison.Ordinal),
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);

        // The stream is now silent on the virtual clock; advance one budget to fire.
        AdvanceScheduler(FirstTokenTimeout);

        var error = (ErrorOutput)await subscriber.FishForMessageAsync<object>(
            m => m is ErrorOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.Timeout, error.Category);
        Assert.Contains("timed out", error.Message);
        await subscriber.FishForMessageAsync<object>(
            m => m is TurnCompleted, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Successful_stream_completes_without_timeout()
    {
        _chatClient.Mode = StreamMode.SucceedImmediately;

        var sessionId = new SessionId("streaming-timeout/success");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("success-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Virtual time never advances, so the watchdog cannot fire — a correct stream
        // completes regardless of how slowly the box schedules it.
        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("success", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    private enum StreamMode { HangForever, EmitThenHang, SucceedImmediately }

    private sealed class StreamingTimeoutTestChatClient : IChatClient
    {
        // Unbounded so the producer (chat client) never blocks; SingleReader because
        // only the test awaits invocations, one turn at a time.
        private readonly Channel<int> _invocations =
            Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
        private int _callCount;

        public StreamMode Mode { get; set; } = StreamMode.HangForever;

        /// <summary>Awaits the next streaming invocation. The watchdog is already armed by then.</summary>
        public async Task WaitForStreamInvocationAsync(CancellationToken cancellationToken)
            => await _invocations.Reader.ReadAsync(cancellationToken);

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
            _invocations.Writer.TryWrite(Interlocked.Increment(ref _callCount));

            return Mode switch
            {
                StreamMode.HangForever => TestStreamingHelpers.NeverCompletesAsync(CancellationToken.None),
                StreamMode.EmitThenHang => EmitThenHangAsync(cancellationToken),
                StreamMode.SucceedImmediately => TestStreamingHelpers.ReturnTextAsync("success response", cancellationToken),
                _ => throw new InvalidOperationException()
            };
        }

        private async IAsyncEnumerable<ChatResponseUpdate> EmitThenHangAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate
            {
                Role = AiChatRole.Assistant,
                Contents = [new TextContent("chunk1 ")]
            };
            await Task.Yield();

            yield return new ChatResponseUpdate
            {
                Contents = [new TextContent("chunk2 ")]
            };
            await Task.Yield();

            yield return new ChatResponseUpdate
            {
                Contents = [new TextContent("chunk3")]
            };
            await Task.Yield();

            // Deltas done; the stream is now silent.
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await gate.Task;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
