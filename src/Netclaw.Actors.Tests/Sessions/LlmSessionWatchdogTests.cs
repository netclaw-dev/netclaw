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

    private sealed class HangingStreamingChatClient : IChatClient
    {
        private int _callCount;

        public int CallCount => _callCount;

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
            Interlocked.Increment(ref _callCount);
            return NeverCompletesAsync(CancellationToken.None);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> NeverCompletesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await gate.Task;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
