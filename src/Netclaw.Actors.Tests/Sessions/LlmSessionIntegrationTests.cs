using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Configuration;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Integration test that exercises the full Netclaw actor pipeline:
/// message routing → session actor → IChatClient → strongly-typed output delivery.
/// Subscribers join sessions directly via <see cref="JoinSession"/> and receive
/// <see cref="SessionOutput"/> events filtered by <see cref="OutputFilter"/>.
/// </summary>
public class LlmSessionIntegrationTests : TestKit
{
    private readonly FakeChatClient _fakeChatClient = new();

    public LlmSessionIntegrationTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new SessionConfig
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
            SnapshotInterval = 5
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
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
    public async Task JoinSession_receives_SessionJoined_acknowledgement()
    {
        var sessionId = new SessionId("test-channel/join-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("join-probe");

        var joined = await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, joined.SessionId);
        Assert.Equal(0, joined.TurnCount);
        Assert.Null(joined.Title);
    }

    [Fact]
    public async Task SendUserMessage_delivers_TextOutput_and_TurnCompleted()
    {
        var sessionId = new SessionId("test-channel/test-thread");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("adapter-probe");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        var ack = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello, Netclaw!"
        }, TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, ack.SessionId);

        // Subscriber receives typed output events
        var text = subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, text.SessionId);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        var completed = subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, completed.SessionId);
        Assert.Equal(1, completed.TurnNumber);
    }

    [Fact]
    public async Task OutputFilter_controls_which_content_categories_are_delivered()
    {
        _fakeChatClient.IncludeThinking = true;
        var sessionId = new SessionId("test-channel/filter-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var textOnlySub = CreateTestProbe("text-only");
        var textAndUsageSub = CreateTestProbe("text-usage");
        var fullSub = CreateTestProbe("full");

        // Three subscribers with different filter bitmasks — sequential Ask
        // ensures each join is fully processed before the next
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = textOnlySub,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        textOnlySub.ExpectMsg<SessionJoined>(); // Drain subscriber notification
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = textAndUsageSub,
            Filter = OutputFilter.TextAndUsage
        }, TimeSpan.FromSeconds(3));
        textAndUsageSub.ExpectMsg<SessionJoined>(); // Drain subscriber notification
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = fullSub,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        fullSub.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Think about this"
        }, TimeSpan.FromSeconds(3));

        // TextOnly: TextOutput + TurnCompleted (lifecycle always delivered)
        textOnlySub.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        textOnlySub.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));
        textOnlySub.ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        // TextAndUsage: TextOutput + UsageOutput + TurnCompleted (no thinking)
        textAndUsageSub.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        textAndUsageSub.ExpectMsg<UsageOutput>(TimeSpan.FromSeconds(3));
        textAndUsageSub.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));
        textAndUsageSub.ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        // Full: ThinkingOutput + TextOutput + UsageOutput + TurnCompleted
        fullSub.ExpectMsg<ThinkingOutput>(TimeSpan.FromSeconds(3));
        fullSub.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        var usage = fullSub.ExpectMsg<UsageOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal(128_000, usage.ContextWindowTokens);
        Assert.NotNull(usage.UsagePercent);
        Assert.True(usage.UsagePercent > 0);
        fullSub.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));
        fullSub.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Two_sessions_are_routed_independently()
    {
        var session1 = new SessionId("channel-A/thread-1");
        var session2 = new SessionId("channel-B/thread-2");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var sub1 = CreateTestProbe("adapter-1");
        var sub2 = CreateTestProbe("adapter-2");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = session1,
            Subscriber = sub1,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        sub1.ExpectMsg<SessionJoined>(); // Drain subscriber notification
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = session2,
            Subscriber = sub2,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        sub2.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session1,
            Content = "Message for session 1",
        }, TimeSpan.FromSeconds(3));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session2,
            Content = "Message for session 2"
        }, TimeSpan.FromSeconds(3));

        // Each subscriber only gets its own session's output
        var text1 = sub1.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal(session1, text1.SessionId);
        sub1.ExpectMsg<TurnCompleted>();

        var text2 = sub2.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal(session2, text2.SessionId);
        sub2.ExpectMsg<TurnCompleted>();

        sub1.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
        sub2.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Buffered_messages_are_batched_into_follow_up_LLM_call()
    {
        _fakeChatClient.Delay = TimeSpan.FromMilliseconds(200);
        var sessionId = new SessionId("channel-C/thread-3");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("adapter-batch");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        // First message — actor enters Processing
        var ack1 = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, ack1.SessionId);

        // These two are deterministically buffered
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second message"
        }, TimeSpan.FromSeconds(3));
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Third message"
        }, TimeSpan.FromSeconds(3));

        // First turn output
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Second turn output (batched follow-up)
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Only two LLM calls total
        Assert.Equal(2, _fakeChatClient.CallCount);

        subscriber.ExpectNoMsg(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Session_recovers_state_after_actor_is_killed()
    {
        var sessionId = new SessionId("test-channel/recovery-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("recovery-sub");

        // Phase 1: Build up state — two completed turns
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second message"
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Phase 2: Kill the session actor child
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3));
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child);

        // Phase 3: Recover — send JoinSession to the same session ID.
        // GenericChildPerEntityParent creates a new actor that recovers from journal.
        var recoverSub = CreateTestProbe("recovery-sub-2");
        var recovered = await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = recoverSub,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(5));
        recoverSub.ExpectMsg<SessionJoined>(); // Drain subscriber notification
        Assert.Equal(sessionId, recovered.SessionId);
        Assert.Equal(2, recovered.TurnCount); // Both turns recovered

        // Phase 4: Verify the session still works — send a third message
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Third message after recovery"
        }, TimeSpan.FromSeconds(3));
        var text = recoverSub.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        var completed = recoverSub.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));
        Assert.Equal(3, completed.TurnNumber); // Continues from recovered state
    }
}

/// <summary>
/// Fake IChatClient that returns canned responses for testing.
/// Supports configurable thinking tokens, usage data, and tool calls.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private int _callCount;

    public int CallCount => _callCount;

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// When true, responses include TextReasoningContent and UsageDetails.
    /// </summary>
    public bool IncludeThinking { get; set; }

    /// <summary>
    /// When set, the first response returns these tool calls instead of text.
    /// Subsequent calls return normal text (simulating the LLM completing after tool results).
    /// When <see cref="AlwaysReturnToolCalls"/> is true, every call returns tool calls
    /// as long as tools are available in options (for testing iteration limits).
    /// </summary>
    public List<FunctionCallContent>? ToolCallsOnFirstCall { get; set; }

    /// <summary>
    /// When true, every call returns tool calls (from <see cref="ToolCallsOnFirstCall"/>)
    /// as long as <c>options.Tools</c> is non-empty. When tools are omitted from options
    /// (circuit breaker fired), returns normal text instead.
    /// </summary>
    public bool AlwaysReturnToolCalls { get; set; }

    /// <summary>
    /// When set, all responses include this usage data.
    /// Used to simulate token counts that trigger compaction.
    /// </summary>
    public UsageDetails? UsageOverride { get; set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);

        // Return tool calls if configured
        if (ToolCallsOnFirstCall is not null)
        {
            var returnToolCalls = AlwaysReturnToolCalls
                ? options?.Tools?.Count > 0   // Every call, as long as tools are available
                : _callCount == 1;            // First call only (existing behavior)

            if (returnToolCalls)
            {
                var toolCallContents = new List<AIContent>(ToolCallsOnFirstCall);
                var toolCallMessage = new ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.Assistant,
                    toolCallContents);
                var toolResponse = new ChatResponse(toolCallMessage);
                if (UsageOverride is not null)
                    toolResponse.Usage = UsageOverride;
                return toolResponse;
            }
        }

        var contents = new List<AIContent>();

        if (IncludeThinking)
        {
            contents.Add(new TextReasoningContent("[fake thinking] Let me consider..."));
        }

        contents.Add(new TextContent($"[fake] Response #{_callCount}"));

        var responseMessage = new ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant,
            contents);

        var response = new ChatResponse(responseMessage);

        if (UsageOverride is not null)
        {
            response.Usage = UsageOverride;
        }
        else if (IncludeThinking)
        {
            response.Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 20,
                TotalTokenCount = 30,
                ReasoningTokenCount = 5
            };
        }

        return response;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return CreateStreamingUpdatesAsync(messages, options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> CreateStreamingUpdatesAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
