// -----------------------------------------------------------------------
// <copyright file="LlmSessionProcessingStateTests.cs" company="Petabridge, LLC">
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

/// <summary>
/// Verifies the processing signal that brackets each LLM model call:
/// <see cref="ProcessingStateOutput"/>(true) when a call starts and
/// (false) when it ends — on success and on failure. Channels render this
/// as a "typing" indicator.
/// </summary>
public sealed class LlmSessionProcessingStateTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private readonly ProcessingStateTestChatClient _chatClient = new();

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "processing-state-test-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
    }

    [Fact]
    public async Task Processing_brackets_a_successful_call_with_started_then_stopped()
    {
        _chatClient.Mode = StreamMode.Succeed;

        var sessionId = new SessionId("processing-state/success");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("processing-success-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Processing
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var started = await subscriber.ExpectMsgAsync<ProcessingStateOutput>(
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(started.IsProcessing, "First processing signal should be 'started'.");

        var stopped = await subscriber.ExpectMsgAsync<ProcessingStateOutput>(
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(stopped.IsProcessing, "Processing should stop when the response lands.");
    }

    [Fact]
    public async Task Processing_stops_when_a_call_fails()
    {
        _chatClient.Mode = StreamMode.Fail;

        var sessionId = new SessionId("processing-state/failure");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("processing-failure-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Processing
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var started = await subscriber.ExpectMsgAsync<ProcessingStateOutput>(
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(started.IsProcessing, "First processing signal should be 'started'.");

        var stopped = await subscriber.ExpectMsgAsync<ProcessingStateOutput>(
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(stopped.IsProcessing, "Processing should stop even when the call fails.");
    }

    private enum StreamMode { Succeed, Fail }

    private sealed class ProcessingStateTestChatClient : IChatClient
    {
        public StreamMode Mode { get; set; } = StreamMode.Succeed;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(new NotSupportedException("Streaming path only."));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => StreamAsync(cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Mode == StreamMode.Fail)
            {
                await Task.Yield();
                // Non-transient failure: no streaming retry, the turn fails outright.
                throw new InvalidOperationException("simulated provider failure");
            }

            yield return new ChatResponseUpdate
            {
                Role = AiChatRole.Assistant,
                Contents = [new TextContent("success response")]
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
