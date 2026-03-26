using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class LlmSessionTimeoutRetryTests(ITestOutputHelper output) : TestKit(output: output)
{
    private readonly TimeoutThenSucceedChatClient _chatClient = new();

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "timeout-retry-test-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            TurnLlmTimeout = TimeSpan.FromSeconds(1),
            ToolExecutionTimeout = TimeSpan.FromSeconds(1),
            SidecarLlmTimeout = TimeSpan.FromSeconds(1),
            LlmTimeoutMaxRetries = 2,
            LlmTimeoutRetryBaseDelaySeconds = 1,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
                MemorySidecarsEnabled = false,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());

        services.AddSingleton(sp => new SessionServices(
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            sp.GetService<IReadOnlyList<IContextLayerProvider>>() ?? Array.Empty<IContextLayerProvider>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<NetclawPaths>()));
        services.AddSingleton(sp => new SessionMemoryServices(
            sp.GetService<IMemoryExtractor>() ?? NullMemoryExtractor.Instance,
            sp.GetService<IMemoryRecallCoordinator>() ?? NullMemoryRecallCoordinator.Instance,
            sp.GetService<IMemoryCheckpointSink>() ?? NullMemoryCheckpointSink.Instance,
            sp.GetService<SQLiteMemoryStore>()));
        services.AddSingleton(new SessionObservability(null, null));
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawActors();
    }

    [Fact]
    public async Task Timeout_retries_then_succeeds()
    {
        // First call hangs (timeout), second call succeeds
        _chatClient.SucceedAfterCalls = 1;

        var sessionId = new SessionId("timeout-retry/retry-succeed");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("retry-succeed-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3));

        // First: retry status indicator
        var retryIndicator = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6));
        Assert.Contains("Retrying", retryIndicator.Message);
        Assert.Contains("attempt 1 of 2", retryIndicator.Message);
        Assert.Equal(ErrorCategory.Timeout, retryIndicator.Category);

        // Then: successful response from retry
        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(10));
        Assert.Contains("succeeded on call 2", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Verify exactly 2 LLM calls were made
        Assert.Equal(2, _chatClient.CallCount);
    }

    [Fact]
    public async Task Timeout_exhausts_retries_and_fails_gracefully()
    {
        // All calls hang — never succeed
        _chatClient.SucceedAfterCalls = int.MaxValue;

        var sessionId = new SessionId("timeout-retry/exhaust");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("exhaust-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3));

        // Retry 1 indicator
        var retry1 = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6));
        Assert.Contains("attempt 1 of 2", retry1.Message);

        // Retry 2 indicator
        var retry2 = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(10));
        Assert.Contains("attempt 2 of 2", retry2.Message);

        // Final failure with descriptive message
        var finalError = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(10));
        Assert.Contains("3 attempts", finalError.Message);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        // 1 initial + 2 retries = 3 total calls
        Assert.Equal(3, _chatClient.CallCount);
    }

    [Fact]
    public async Task Non_timeout_error_does_not_retry()
    {
        // Throw a non-timeout error on first call
        _chatClient.ThrowNonTimeoutError = true;

        var sessionId = new SessionId("timeout-retry/no-retry");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("no-retry-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3));

        // Immediate error, no retry indicators
        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6));
        Assert.DoesNotContain("Retrying", error.Message);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Only 1 LLM call made
        Assert.Equal(1, _chatClient.CallCount);
    }

    /// <summary>
    /// Chat client that hangs for the first N calls, then succeeds.
    /// Can also throw non-timeout errors for testing error classification.
    /// </summary>
    private sealed class TimeoutThenSucceedChatClient : IChatClient
    {
        private int _callCount;

        public int CallCount => _callCount;

        /// <summary>Succeed starting from this call number (1-based). Set to int.MaxValue to always hang.</summary>
        public int SucceedAfterCalls { get; set; } = 1;

        /// <summary>If true, first call throws a non-timeout error instead of hanging.</summary>
        public bool ThrowNonTimeoutError { get; set; }

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
            var callNumber = Interlocked.Increment(ref _callCount);

            if (ThrowNonTimeoutError && callNumber == 1)
                return ThrowAsync(new InvalidOperationException("Non-timeout provider error"), cancellationToken);

            if (callNumber > SucceedAfterCalls)
                return ReturnTextAsync($"succeeded on call {callNumber}", cancellationToken);

            return NeverCompletesAsync(CancellationToken.None);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> NeverCompletesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await gate.Task;
            yield break;
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ReturnTextAsync(
            string text,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(
                AiChatRole.Assistant,
                [new TextContent(text)]));

            foreach (var update in response.ToChatResponseUpdates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ThrowAsync(
            Exception ex,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw ex;
#pragma warning disable CS0162 // Unreachable code — required for async-iterator signature
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
