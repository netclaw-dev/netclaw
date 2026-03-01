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
/// Integration tests for the tool execution loop:
/// LLM returns tool call → actor executes tool → feeds result back → LLM completes.
/// </summary>
public class ToolExecutionIntegrationTests : TestKit
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();
    private readonly FakeToolAuditLogger _fakeAuditLogger = new();

    public ToolExecutionIntegrationTests(ITestOutputHelper output) : base(output: output)
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
            TitleGenerationInterval = 0,
            MaxInlineToolResultChars = 120
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);
        services.AddSingleton<IToolAuditLogger>(_fakeAuditLogger);

        // Register a tool in the registry
        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
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
    public async Task Tool_call_executes_and_feeds_result_back_to_LLM()
    {
        // Configure: first LLM call returns a tool call, second returns text
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test query" })
        ];

        _fakeToolExecutor.Results["web_search"] = "Found 3 results for test query";

        var sessionId = new SessionId("test-channel/tool-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("tool-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for test query"
        }, TimeSpan.FromSeconds(3));

        // First: subscriber receives tool call output
        var toolCall = subscriber.ExpectMsg<ToolCallOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal("web_search", toolCall.ToolName);
        Assert.Equal("call-1", toolCall.CallId);

        // Then: subscriber receives final text response (after tool result fed back)
        var text = subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(5));
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        var completed = subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));
        Assert.Equal(1, completed.TurnNumber);

        // Two LLM calls: first returned tool call, second returned text
        Assert.Equal(2, _fakeChatClient.CallCount);

        // Tool executor was called once
        Assert.Equal(1, _fakeToolExecutor.CallCount);

        // Audit logger recorded the invocation
        Assert.Single(_fakeAuditLogger.Entries);
        Assert.Equal("web_search", _fakeAuditLogger.Entries[0].ToolName);
        Assert.True(_fakeAuditLogger.Entries[0].Allowed);
    }

    [Fact]
    public async Task Multiple_tool_calls_in_single_response_all_executed()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "query 1" }),
            new FunctionCallContent("call-2", "web_search",
                new Dictionary<string, object?> { ["query"] = "query 2" })
        ];

        _fakeToolExecutor.Results["web_search"] = "search result";

        var sessionId = new SessionId("test-channel/multi-tool-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("multi-tool-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for two things"
        }, TimeSpan.FromSeconds(3));

        // Two tool call outputs
        subscriber.ExpectMsg<ToolCallOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<ToolCallOutput>(TimeSpan.FromSeconds(3));

        // Final text response
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(5));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        Assert.Equal(2, _fakeToolExecutor.CallCount);
        Assert.Equal(2, _fakeAuditLogger.Entries.Count);
    }

    [Fact]
    public async Task Tool_execution_error_returns_error_text_to_LLM()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-err", "failing_tool", null)
        ];

        _fakeToolExecutor.FailForTools.Add("failing_tool");

        var sessionId = new SessionId("test-channel/tool-error-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("error-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Try the failing tool"
        }, TimeSpan.FromSeconds(3));

        // Tool call output emitted
        subscriber.ExpectMsg<ToolCallOutput>(TimeSpan.FromSeconds(3));

        // LLM gets called again with the error message as tool result,
        // then returns normal text
        var text = subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(5));
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Two LLM calls total
        Assert.Equal(2, _fakeChatClient.CallCount);
    }

    [Fact]
    public async Task Oversized_tool_result_is_truncated_before_reentering_context_window()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-long", "web_search",
                new Dictionary<string, object?> { ["query"] = "huge" })
        ];

        _fakeToolExecutor.Results["web_search"] = new string('x', 800);

        var sessionId = new SessionId("test-channel/tool-truncate-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("truncate-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run huge tool"
        }, TimeSpan.FromSeconds(3));

        subscriber.ExpectMsg<ToolCallOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(5));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        Assert.Equal(2, _fakeChatClient.CallCount);
        Assert.Equal(2, _fakeChatClient.ReceivedMessages.Count);

        var secondCall = _fakeChatClient.ReceivedMessages[1];
        var toolMessage = secondCall.FirstOrDefault(m => m.Role == Microsoft.Extensions.AI.ChatRole.Tool);
        Assert.NotNull(toolMessage);

        var result = toolMessage!.Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.NotNull(result);
        Assert.Contains("tool result truncated", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(result!.Length < 300);
    }
}

/// <summary>
/// Fake tool executor for testing. Returns canned results by tool name.
/// </summary>
internal sealed class FakeToolExecutor : IToolExecutor
{
    private int _callCount;

    public int CallCount => _callCount;

    /// <summary>Tool name → result string.</summary>
    public Dictionary<string, string> Results { get; } = new();

    /// <summary>Tool names that should throw on execution.</summary>
    public HashSet<string> FailForTools { get; } = new();

    public Task<string> ExecuteAsync(FunctionCallContent toolCall, Netclaw.Tools.ToolExecutionContext? context = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _callCount);

        if (FailForTools.Contains(toolCall.Name))
        {
            throw new InvalidOperationException($"Tool '{toolCall.Name}' failed (simulated)");
        }

        var result = Results.GetValueOrDefault(toolCall.Name, $"[fake result for {toolCall.Name}]");
        return Task.FromResult(result);
    }
}

/// <summary>
/// Fake audit logger that captures entries for verification.
/// </summary>
internal sealed class FakeToolAuditLogger : IToolAuditLogger
{
    public List<ToolAuditEntry> Entries { get; } = new();

    public void Log(ToolAuditEntry entry)
    {
        Entries.Add(entry);
    }
}
