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
using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Integration tests for the max tool iterations safety circuit breaker.
/// Verifies that unbounded agentic tool loops are terminated after the configured limit.
/// </summary>
public class MaxToolIterationTests : TestKit
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();
    private readonly FakeToolAuditLogger _fakeAuditLogger = new();

    public MaxToolIterationTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            MaxToolCallsPerTurn = 3, // Low limit for testing
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
                MemorySidecarsEnabled = false,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);
        services.AddSingleton<IToolAuditLogger>(_fakeAuditLogger);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());

        // Composite records for LlmSessionActor constructor
        services.AddSingleton(sp => new SessionServices(
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            sp.GetService<IReadOnlyList<IContextLayerProvider>>() ?? Array.Empty<IContextLayerProvider>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<NetclawPaths>()));
        services.AddSingleton(sp => new SessionToolServices(
            sp.GetRequiredService<IToolExecutor>(),
            sp.GetService<IToolAuditLogger>(),
            sp.GetRequiredService<ToolRegistry>(),
            sp.GetService<ToolAccessPolicy>(),
            sp.GetService<Netclaw.Actors.Channels.TrustContextDeriver>(),
            sp.GetService<Netclaw.Actors.Skills.SkillRegistry>()));
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
    public async Task Tool_iteration_limit_forces_text_response()
    {
        // Configure: LLM always returns tool calls when tools are available
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test" })
        ];
        _fakeChatClient.AlwaysReturnToolCalls = true;
        _fakeToolExecutor.Results["web_search"] = "search result";

        var sessionId = new SessionId("test-channel/max-iter-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("max-iter-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Keep calling tools forever"
        }, TimeSpan.FromSeconds(3));

        // Expect 3 rounds of tool calls (the configured limit)
        for (var i = 0; i < 3; i++)
        {
            await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3));
        }

        // After circuit breaker fires, LLM is called without tools → text response
        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5));
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        // 4 LLM calls total: 3 returned tool calls + 1 forced text (no tools)
        Assert.Equal(4, _fakeChatClient.CallCount);

        // 3 tool executions
        Assert.Equal(3, _fakeToolExecutor.CallCount);
    }

    [Fact]
    public async Task Tool_iteration_limit_fails_turn_when_model_keeps_emitting_tool_calls_without_tools()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test" })
        ];
        _fakeChatClient.AlwaysReturnToolCalls = true;
        _fakeChatClient.IgnoreToolAvailability = true;
        _fakeToolExecutor.Results["web_search"] = "search result";

        var sessionId = new SessionId("test-channel/max-iter-violation");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("max-iter-violation-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Keep calling tools even when disabled"
        }, TimeSpan.FromSeconds(3));

        for (var i = 0; i < 3; i++)
            await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3));

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(5));
        Assert.Equal(ErrorCategory.ProviderFailure, error.Category);
        Assert.Contains("tool calls", error.Message, StringComparison.OrdinalIgnoreCase);

        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        Assert.Equal(4, _fakeChatClient.CallCount);
        Assert.Equal(3, _fakeToolExecutor.CallCount);
    }

    [Fact]
    public async Task Tool_iteration_counter_resets_between_turns()
    {
        // Configure: LLM always returns tool calls when tools are available
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test" })
        ];
        _fakeChatClient.AlwaysReturnToolCalls = true;
        _fakeToolExecutor.Results["web_search"] = "search result";

        var sessionId = new SessionId("test-channel/iter-reset-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("iter-reset-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification

        // Turn 1: hits the limit at 3
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First turn"
        }, TimeSpan.FromSeconds(3));

        for (var i = 0; i < 3; i++)
            await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Turn 2: counter should be reset, so it also gets 3 tool iterations
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second turn"
        }, TimeSpan.FromSeconds(3));

        for (var i = 0; i < 3; i++)
            await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        // 8 LLM calls total: 2 turns × (3 tool + 1 text)
        Assert.Equal(8, _fakeChatClient.CallCount);

        // 6 tool executions total: 2 turns × 3
        Assert.Equal(6, _fakeToolExecutor.CallCount);
    }

    [Fact]
    public async Task Normal_tool_use_within_limit_works_unchanged()
    {
        // Configure: LLM returns tool call only on first call (default behavior)
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test" })
        ];
        _fakeToolExecutor.Results["web_search"] = "search result";

        var sessionId = new SessionId("test-channel/normal-tool-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("normal-tool-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TimeSpan.FromSeconds(3));

        // One tool call, then normal text response (well within limit of 3)
        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        // 2 LLM calls: 1 tool call + 1 text
        Assert.Equal(2, _fakeChatClient.CallCount);
        Assert.Equal(1, _fakeToolExecutor.CallCount);
    }
}
