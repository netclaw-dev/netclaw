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
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
                MaxInlineToolResultChars = 120,
            }
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
        registry.Register(
            AIFunctionFactory.Create((string path) => $"contents of {path}", "file_read"),
            "file_read");
        services.AddSingleton(registry);
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());

        services.AddTestNetclawPaths();
        services.AddSingleton(sp => new SessionServices(
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            sp.GetService<IReadOnlyList<IContextLayerProvider>>() ?? Array.Empty<IContextLayerProvider>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<NetclawPaths>()));
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
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for test query"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // First: subscriber receives tool call output
        var toolCall = await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("web_search", toolCall.ToolName);
        Assert.Equal("call-1", toolCall.CallId);

        // Drain the tool result output emitted after tool execution
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Then: subscriber receives final text response (after tool result fed back)
        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
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
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for two things"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Two tool call outputs
        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Drain two tool result outputs emitted after tool execution
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Final text response
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

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
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Try the failing tool"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Tool call output emitted
        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Drain the tool result output (error text is fed back as a tool result)
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // LLM gets called again with the error message as tool result,
        // then returns normal text
        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

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
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run huge tool"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task WorkingContext_populated_by_file_read_tool_execution()
    {
        // End-to-end: LLM emits a file_read tool call, the actor executes
        // it and appends the result to history, the tool-execution hook
        // pushes the path onto WorkingContext.RecentFiles, and the next
        // LLM call receives a [working-context] block in its dynamic
        // system message.
        //
        // CRITICAL: the argument key here ("Path", PascalCase) must match
        // what NetclawToolGenerator emits for first-party file tools —
        // the generator writes `param.Name` verbatim, so FileReadTool's
        // `string Path` parameter becomes a JSON schema with key "Path",
        // and the LLM emits `{"Path": "..."}`. A lowercase "path" here
        // would hide the real-world bug where WorkingContextUpdater's
        // field-name probe misses PascalCase arguments.
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "file_read",
                new Dictionary<string, object?> { ["Path"] = "src/Rect.cs" })
        ];
        _fakeToolExecutor.Results["file_read"] = "public readonly record struct Rect { ... }";

        var sessionId = new SessionId("console/working-context-populated");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("wc-populated-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "please read Rect.cs"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Wait for the full tool-call loop: ToolCallOutput, ToolResultOutput,
        // second LLM call's TextOutput, TurnCompleted.
        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // The second (follow-up) main-model call after the tool execution
        // should see a `[working-context]` block. Since #608, volatile
        // content (including working-context) lives in a User-role tail
        // message rather than a System message, so the agent sees it as
        // runtime context appended after the user's message.
        var mainModelCalls = _fakeChatClient.ReceivedMessages
            .Where(msgs => !(msgs.FirstOrDefault(m => m.Role == Microsoft.Extensions.AI.ChatRole.System)?.Text
                ?? string.Empty).Contains("session summarizer", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(mainModelCalls.Count >= 2,
            $"Expected at least 2 main-model calls (initial + tool-loop follow-up); got {mainModelCalls.Count}");

        var followUpCall = mainModelCalls[^1];
        var allContent = followUpCall
            .Select(m => m.Text ?? string.Empty)
            .ToList();

        var hasWorkingContextBlock = allContent.Any(s =>
            s.Contains("[working-context]", StringComparison.Ordinal)
            && s.Contains("src/Rect.cs", StringComparison.Ordinal));

        Assert.True(hasWorkingContextBlock,
            $"Expected the follow-up LLM call to include a [working-context] block mentioning src/Rect.cs. All messages:\n{string.Join("\n---\n", allContent)}");
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

    public Task AuthorizeAsync(FunctionCallContent toolCall, Netclaw.Tools.ToolExecutionContext? context = null, CancellationToken ct = default)
    {
        if (FailForTools.Contains(toolCall.Name))
            throw new InvalidOperationException($"Tool '{toolCall.Name}' failed (simulated)");

        return Task.CompletedTask;
    }

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
