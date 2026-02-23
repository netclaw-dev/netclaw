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
        services.AddSingleton(new SessionConfig
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
            SnapshotInterval = 5,
            MaxToolIterationsPerTurn = 3 // Low limit for testing
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

        sessionManager.Tell(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        });
        subscriber.ExpectMsg<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Keep calling tools forever"
        }, TimeSpan.FromSeconds(3));

        // Expect 3 rounds of tool calls (the configured limit)
        for (var i = 0; i < 3; i++)
        {
            subscriber.ExpectMsg<ToolCallOutput>(TimeSpan.FromSeconds(3));
        }

        // After circuit breaker fires, LLM is called without tools → text response
        var text = subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(5));
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        // 4 LLM calls total: 3 returned tool calls + 1 forced text (no tools)
        Assert.Equal(4, _fakeChatClient.CallCount);

        // 3 tool executions
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

        sessionManager.Tell(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        });
        subscriber.ExpectMsg<SessionJoined>();

        // Turn 1: hits the limit at 3
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First turn"
        }, TimeSpan.FromSeconds(3));

        for (var i = 0; i < 3; i++)
            subscriber.ExpectMsg<ToolCallOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(5));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Turn 2: counter should be reset, so it also gets 3 tool iterations
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second turn"
        }, TimeSpan.FromSeconds(3));

        for (var i = 0; i < 3; i++)
            subscriber.ExpectMsg<ToolCallOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(5));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

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

        sessionManager.Tell(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        });
        subscriber.ExpectMsg<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TimeSpan.FromSeconds(3));

        // One tool call, then normal text response (well within limit of 3)
        subscriber.ExpectMsg<ToolCallOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(5));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        // 2 LLM calls: 1 tool call + 1 text
        Assert.Equal(2, _fakeChatClient.CallCount);
        Assert.Equal(1, _fakeToolExecutor.CallCount);
    }
}
