// -----------------------------------------------------------------------
// <copyright file="LlmSessionWatchdogTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class LlmSessionWatchdogTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private readonly HangingStreamingChatClient _chatClient = new();

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "watchdog-test-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            PrefillTimeout = TimeSpan.FromSeconds(1),
            FirstTokenTimeout = TimeSpan.FromSeconds(1),
            ToolExecutionTimeout = TimeSpan.FromSeconds(1),
            SidecarLlmTimeout = TimeSpan.FromSeconds(1),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
    }

    [Fact]
    public async Task Watchdog_times_out_stuck_streaming_call_and_session_recovers_for_follow_up_turn()
    {
        var sessionId = new SessionId("watchdog/session-1");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("watchdog-subscriber");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "first"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var firstError = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("timed out", firstError.Message, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var secondError = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("timed out", secondError.Message, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(_chatClient.CallCount >= 2);
    }

    [Fact]
    public async Task Buffered_reprompt_is_replayed_after_failed_turn()
    {
        _chatClient.SucceedAfterFirstTimeout = true;

        var sessionId = new SessionId("watchdog/session-buffered-retry");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("watchdog-buffered-retry-subscriber");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "first"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var firstError = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("timed out", firstError.Message, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var recoveredText = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("recovered after timeout", recoveredText.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

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
                return TestStreamingHelpers.ReturnTextAsync($"recovered after timeout on call {callNumber}", cancellationToken);

            return TestStreamingHelpers.NeverCompletesAsync(CancellationToken.None);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
