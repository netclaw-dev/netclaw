using System.Diagnostics;
using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class LlmSessionWatchdogTests(ITestOutputHelper output) : TestKit(output: output)
{
    private readonly HangingStreamingChatClient _chatClient = new();

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new SessionConfig
        {
            ModelId = "watchdog-test-model",
            ContextWindowTokens = 128_000,
            SnapshotInterval = 5,
            TitleGenerationInterval = 0,
            TurnLlmTimeoutSeconds = 1,
            ToolExecutionTimeoutSeconds = 1,
            SidecarLlmTimeoutSeconds = 1
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawActors();
    }

    [Fact]
    public async Task Watchdog_times_out_stuck_streaming_call_and_session_recovers_for_follow_up_turn()
    {
        var sessionId = new SessionId("watchdog/session-1");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("watchdog-subscriber");

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
            Content = "first"
        }, TimeSpan.FromSeconds(3));

        var firstError = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6));
        Assert.Contains("timeout", firstError.Message, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second"
        }, TimeSpan.FromSeconds(3));

        var secondError = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6));
        Assert.Contains("timeout", secondError.Message, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        Assert.True(_chatClient.CallCount >= 2);
    }

    [Fact]
    public async Task Stale_llm_completion_is_ignored_after_newer_turn_starts()
    {
        var sessionId = new SessionId("watchdog/session-stale-llm");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("watchdog-stale-llm-subscriber");

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
            Content = "first"
        }, TimeSpan.FromSeconds(3));

        var firstError = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6));
        Assert.Contains("timeout", firstError.Message, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second"
        }, TimeSpan.FromSeconds(3));

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3));

        child.Tell(new LlmResponseReceived
        {
            OperationId = 1,
            Response = new ChatResponse(new ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                [new TextContent("stale completion should be ignored")])),
            StreamedText = false,
            StreamedThinking = false
        });

        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));

        var secondError = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6));
        Assert.Contains("timeout", secondError.Message, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Buffered_reprompt_is_replayed_after_failed_turn()
    {
        _chatClient.SucceedAfterFirstTimeout = true;

        var sessionId = new SessionId("watchdog/session-buffered-retry");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("watchdog-buffered-retry-subscriber");

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
            Content = "first"
        }, TimeSpan.FromSeconds(3));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second"
        }, TimeSpan.FromSeconds(3));

        var firstError = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6));
        Assert.Contains("timeout", firstError.Message, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        var recoveredText = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6));
        Assert.Contains("recovered after timeout", recoveredText.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        Assert.Equal(2, _chatClient.CallCount);
    }

    [Fact]
    public async Task Buffered_reprompt_is_recovered_after_actor_restart()
    {
        _chatClient.SucceedAfterFirstTimeout = true;

        var sessionId = new SessionId("watchdog/session-buffered-recovery");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("watchdog-buffered-recovery-sub");

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
            Content = "first"
        }, TimeSpan.FromSeconds(3));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3));
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child);

        var recoverSub = CreateTestProbe("watchdog-buffered-recovery-sub-2");
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = recoverSub,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(5));
        await recoverSub.ExpectMsgAsync<SessionJoined>();

        var recoveredText = await recoverSub.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6));
        Assert.Contains("recovered after timeout", recoveredText.Text, StringComparison.OrdinalIgnoreCase);
        await recoverSub.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        Assert.Equal(2, _chatClient.CallCount);
    }

    private sealed class HangingStreamingChatClient : IChatClient
    {
        private int _callCount;

        public int CallCount => _callCount;
        public bool SucceedAfterFirstTimeout { get; set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(new NotSupportedException("Streaming path only in this test."));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var callNumber = Interlocked.Increment(ref _callCount);

            if (SucceedAfterFirstTimeout && callNumber > 1)
                return ReturnTextAsync($"recovered after timeout on call {callNumber}", cancellationToken);

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
                Microsoft.Extensions.AI.ChatRole.Assistant,
                [new TextContent(text)]));

            foreach (var update in response.ToChatResponseUpdates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

public sealed class LlmSessionTurnBudgetTests(ITestOutputHelper output) : TestKit(output: output)
{
    private readonly HeartbeatStreamingChatClient _chatClient = new();

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new SessionConfig
        {
            ModelId = "turn-budget-test-model",
            ContextWindowTokens = 128_000,
            SnapshotInterval = 5,
            TitleGenerationInterval = 0,
            TurnLlmTimeoutSeconds = 1,
            ToolExecutionTimeoutSeconds = 1,
            SidecarLlmTimeoutSeconds = 1,
            MaxTurnDurationSeconds = 2
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawActors();
    }

    [Fact]
    public async Task Turn_budget_times_out_chatty_stream_that_keeps_refreshing_watchdog()
    {
        var sessionId = new SessionId("watchdog/session-turn-budget");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("turn-budget-sub");

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
            Content = "keep streaming forever"
        }, TimeSpan.FromSeconds(3));

        ErrorOutput? error = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        while (DateTimeOffset.UtcNow < deadline && error is null)
        {
            var output = await subscriber.ExpectMsgAsync<SessionOutput>(TimeSpan.FromSeconds(1));
            if (output is ErrorOutput err)
                error = err;
        }

        Assert.NotNull(error);
        Assert.Contains("too long", error!.Message, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class HeartbeatStreamingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(new NotSupportedException("Streaming path only in this test."));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var response = new ChatResponse(new ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.Assistant,
                    [new TextContent("tick")])) ;

                foreach (var update in response.ToChatResponseUpdates())
                    yield return update;

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromMilliseconds(200))
                    await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
