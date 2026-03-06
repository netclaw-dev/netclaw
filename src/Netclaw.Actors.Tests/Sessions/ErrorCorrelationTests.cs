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

/// <summary>
/// Verifies that <see cref="ErrorOutput"/> emitted by <see cref="LlmSessionActor"/>
/// carries a non-empty <see cref="ErrorOutput.CorrelationId"/> and a correctly
/// classified <see cref="ErrorOutput.Category"/>.
/// </summary>
public sealed class ErrorCorrelationTests(ITestOutputHelper output) : TestKit(output: output)
{
    private readonly FailingChatClient _chatClient = new();

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new SessionConfig
        {
            ModelId = "error-test-model",
            ContextWindowTokens = 128_000,
            SnapshotInterval = 5,
            TitleGenerationInterval = 0,
            TurnLlmTimeoutSeconds = 10,
            ToolExecutionTimeoutSeconds = 10,
            SidecarLlmTimeoutSeconds = 10
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
    public async Task Provider_failure_emits_ErrorOutput_with_ProviderFailure_category_and_non_empty_CorrelationId()
    {
        var sessionId = new SessionId("error-correlation/provider-fail-1");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("error-subscriber");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "trigger error"
        }, TimeSpan.FromSeconds(3));

        var error = subscriber.ExpectMsg<ErrorOutput>(TimeSpan.FromSeconds(5));

        Assert.Equal(ErrorCategory.ProviderFailure, error.Category);
        Assert.NotEqual(Guid.Empty, error.CorrelationId);
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Each_error_turn_gets_a_distinct_CorrelationId()
    {
        var sessionId = new SessionId("error-correlation/provider-fail-2");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("error-subscriber-2");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "first"
        }, TimeSpan.FromSeconds(3));

        var firstError = subscriber.ExpectMsg<ErrorOutput>(TimeSpan.FromSeconds(5));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second"
        }, TimeSpan.FromSeconds(3));

        var secondError = subscriber.ExpectMsg<ErrorOutput>(TimeSpan.FromSeconds(5));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

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

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "trigger timeout"
        }, TimeSpan.FromSeconds(3));

        var error = subscriber.ExpectMsg<ErrorOutput>(TimeSpan.FromSeconds(5));

        Assert.Equal(ErrorCategory.Timeout, error.Category);
        Assert.NotEqual(Guid.Empty, error.CorrelationId);
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));
    }

    private sealed class FailingChatClient : IChatClient
    {
        public bool ThrowTimeout { get; set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(new NotSupportedException("Streaming path only."));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            // GetException() returns non-null, but the compiler cannot prove it,
            // so the null check keeps yield break reachable and avoids CS0162.
            var ex = GetException();
            if (ex is not null) throw ex;
            yield break;
        }

        private Exception GetException() => ThrowTimeout
            ? (Exception)new TimeoutException("Simulated provider timeout")
            : new InvalidOperationException("Simulated provider failure");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
