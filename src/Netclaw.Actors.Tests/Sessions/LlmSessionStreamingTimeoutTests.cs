// -----------------------------------------------------------------------
// <copyright file="LlmSessionStreamingTimeoutTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
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

namespace Netclaw.Actors.Tests.Sessions;

public sealed class LlmSessionStreamingTimeoutTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private readonly StreamingTimeoutTestChatClient _chatClient = new();

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
            FirstTokenTimeout = TimeSpan.FromSeconds(2),
            ToolExecutionTimeout = TimeSpan.FromSeconds(10),
            SidecarLlmTimeout = TimeSpan.FromSeconds(10),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
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

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // FirstTokenTimeout is 2s — should fire within ~3s
        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.Timeout, error.Category);
        Assert.Contains("timed out", error.Message);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Timeout_resets_on_delta_and_fires_after_silence()
    {
        // Emit deltas then hang — the unified timeout (2s) resets on each delta,
        // then fires 2s after the last one
        _chatClient.Mode = StreamMode.EmitThenHang;

        var sessionId = new SessionId("streaming-timeout/delta-then-silence");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("delta-silence-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Deltas stream, then silence — timeout fires after FirstTokenTimeout (2s) of no activity
        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(8), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.Timeout, error.Category);
        Assert.Contains("timed out", error.Message);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Successful_stream_completes_without_timeout()
    {
        _chatClient.Mode = StreamMode.SucceedImmediately;

        var sessionId = new SessionId("streaming-timeout/success");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("success-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("success", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    private enum StreamMode { HangForever, EmitThenHang, SucceedImmediately }

    private sealed class StreamingTimeoutTestChatClient : IChatClient
    {
        public StreamMode Mode { get; set; } = StreamMode.HangForever;

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
            return Mode switch
            {
                StreamMode.HangForever => TestStreamingHelpers.NeverCompletesAsync(CancellationToken.None),
                StreamMode.EmitThenHang => EmitThenHangAsync(cancellationToken),
                StreamMode.SucceedImmediately => TestStreamingHelpers.ReturnTextAsync("success response", cancellationToken),
                _ => throw new InvalidOperationException()
            };
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> EmitThenHangAsync(
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

            // Now hang — stream goes silent
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await gate.Task;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
