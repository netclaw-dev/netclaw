// -----------------------------------------------------------------------
// <copyright file="ErrorCorrelationTests.cs" company="Petabridge, LLC">
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

/// <summary>
/// Verifies that <see cref="ErrorOutput"/> emitted by <see cref="LlmSessionActor"/>
/// carries a non-empty <see cref="ErrorOutput.CorrelationId"/> and a correctly
/// classified <see cref="ErrorOutput.Category"/>.
/// </summary>
public sealed class ErrorCorrelationTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private readonly FailingChatClient _chatClient = new();

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "error-test-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            FirstTokenTimeout = TimeSpan.FromSeconds(10),
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
    public async Task Provider_failure_emits_ErrorOutput_with_ProviderFailure_category_and_non_empty_CorrelationId()
    {
        var sessionId = new SessionId("error-correlation/provider-fail-1");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("error-subscriber");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "trigger error"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ErrorCategory.ProviderFailure, error.Category);
        Assert.NotEqual(Guid.Empty, error.CorrelationId);
        var tc = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, tc.Outcome);

        var child = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);

        var resumedSubscriber = CreateTestProbe("error-resume-subscriber");
        var resumed = await sessionManager.Ask<SessionJoined>(new JoinSession(resumedSubscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, resumed.TurnCount);
        Assert.NotNull(resumed.RecentMessages);
        var resumedError = Assert.Single(resumed.RecentTranscript!, entry =>
            entry.Type == SessionTranscriptEntryTypes.Error);
        Assert.Equal(error.CorrelationId.ToString("D"), resumedError.ErrorCorrelationId);
        Assert.Equal(nameof(ErrorCategory.ProviderFailure), resumedError.ErrorCategory);
    }

    [Fact]
    public async Task Each_error_turn_gets_a_distinct_CorrelationId()
    {
        var sessionId = new SessionId("error-correlation/provider-fail-2");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("error-subscriber-2");

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

        var firstError = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var secondError = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(firstError.CorrelationId, secondError.CorrelationId);
        Assert.Equal(ErrorCategory.ProviderFailure, firstError.Category);
        Assert.Equal(ErrorCategory.ProviderFailure, secondError.Category);
    }

    [Fact]
    public async Task Timeout_error_emits_ErrorOutput_with_Timeout_category()
    {
        _chatClient.ThrowTimeout = true;

        var sessionId = new SessionId("error-correlation/timeout-fail-1");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("timeout-subscriber");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "trigger timeout"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ErrorCategory.Timeout, error.Category);
        Assert.NotEqual(Guid.Empty, error.CorrelationId);
        var tc = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, tc.Outcome);
    }

    private sealed class FailingChatClient : IChatClient
    {
        public bool ThrowTimeout { get; set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(new NotSupportedException("Streaming path only."));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            // Throw synchronously so the LlmCallFailed message is enqueued in
            // the actor's mailbox before the actor finishes processing the
            // SendUserMessage. Using Task.Yield() here defers the exception to
            // the thread pool, creating a race where LlmCallFailed delivery
            // depends on thread pool scheduling and can exceed ExpectMsgAsync
            // timeouts under load.
            => throw GetException();

        private Exception GetException() => ThrowTimeout
            ? (Exception)new TimeoutException("Simulated provider timeout")
            : new InvalidOperationException("Simulated provider failure");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
