// -----------------------------------------------------------------------
// <copyright file="ToolExecutionIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Integration tests for the tool execution loop:
/// LLM returns tool call → actor executes tool → feeds result back → LLM completes.
/// </summary>
public class ToolExecutionIntegrationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();

    public ToolExecutionIntegrationTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
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

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        registry.Register(
            AIFunctionFactory.Create((string path) => $"contents of {path}", "file_read"),
            "file_read");
        registry.Register(
            AIFunctionFactory.Create((string path) => path, "set_working_directory"),
            "file");
        services.AddSingleton(registry);
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

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
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
        Assert.Equal("web_search", toolCall.ToolName.Value);
        Assert.Equal("call-1", toolCall.CallId.Value);

        // Drain the tool result output emitted after tool execution
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Then: subscriber receives final text response (after tool result fed back)
        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, completed.TurnNumber.Value);

        // Two LLM calls: first returned tool call, second returned text
        Assert.Equal(2, _fakeChatClient.CallCount);

        // Tool executor was called once
        Assert.Equal(1, _fakeToolExecutor.CallCount);

        // Audit logger recorded the invocation
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

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
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

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
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

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
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
        Assert.Contains("truncated", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(result!.Length < 500); // windowed to the 120-char budget + steer (vs the raw 800)
    }

    [Fact]
    public async Task WorkingContext_populated_by_file_read_tool_execution_visible_in_next_turn()
    {
        // End-to-end: LLM emits a file_read tool call on turn 1, the actor
        // executes it and the tool-execution hook pushes the path onto
        // WorkingContext.RecentFiles. The agent must see a [working-context]
        // block mentioning that file on turn 2 (the next user turn).
        //
        // Under the cache-history-stable design (see SessionMessageAssembler
        // class comment), volatile context is captured ONCE per turn at
        // turn-start via SessionState.AddSystemNudge — not refreshed on
        // each tool-loop iteration. Turn 1's nudge therefore reflects
        // working_context as of turn-start (empty); the file opened during
        // turn 1's tool loop surfaces in turn 2's nudge. This is the
        // explicit trade-off for keeping the wire bytes byte-stable across
        // turns (every byte-prefix-caching provider benefits).
        //
        // CRITICAL: the argument key here ("Path", PascalCase) must match
        // what NetclawToolGenerator emits for first-party file tools —
        // the generator writes `param.Name` verbatim, so FileReadTool's
        // `string Path` parameter becomes a JSON schema with key "Path",
        // and the LLM emits `{"Path": "..."}`. A lowercase "path" here
        // would hide the real-world bug where WorkingContextUpdater's
        // field-name probe misses PascalCase arguments.
        var canonicalPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "src", "Rect.cs"));
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "file_read",
                new Dictionary<string, object?> { ["Path"] = "src/Rect.cs" })
        ];
        _fakeToolExecutor.Results["file_read"] = "public readonly record struct Rect { ... }";
        _fakeToolExecutor.Receipts["file_read"] = new ToolInvocationReceipt(
            ToolInvocationOutcomeCategory.Success,
            [new ToolFileActivity(canonicalPath, ToolFileActivityKind.Read)]);

        var sessionId = new SessionId("console/working-context-populated");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("wc-populated-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "please read Rect.cs"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Drain turn 1's events: tool call, tool result, follow-up text, complete.
        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Turn 2: a follow-up user message. The fresh nudge captured at
        // turn-2-start must include working_context with src/Rect.cs from
        // turn 1's tool execution.
        var turn1CallCount = _fakeChatClient.ReceivedMessages.Count;

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "what file did you read?"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Inspect the first main-model LLM call of turn 2 (the one with
        // the fresh nudge applied).
        Assert.True(_fakeChatClient.ReceivedMessages.Count > turn1CallCount,
            $"Expected at least one main-model call on turn 2; total before={turn1CallCount} after={_fakeChatClient.ReceivedMessages.Count}");
        var turn2Call = _fakeChatClient.ReceivedMessages[turn1CallCount];
        var allContent = turn2Call.Select(m => m.Text ?? string.Empty).ToList();

        var hasWorkingContextBlock = allContent.Any(s =>
            s.Contains("[working-context]", StringComparison.Ordinal)
            && s.Contains(canonicalPath, StringComparison.Ordinal));

        Assert.True(hasWorkingContextBlock,
            $"Expected turn 2's first LLM call to include a [working-context] block mentioning the canonical file. All messages:\n{string.Join("\n---\n", allContent)}");
    }

    [Fact]
    public async Task Failed_project_declaration_cannot_change_scope_from_successful_looking_text()
    {
        var deniedPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "denied-project"));
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                "call-cwd",
                "set_working_directory",
                new Dictionary<string, object?> { ["Path"] = deniedPath })
        ];
        _fakeToolExecutor.Results["set_working_directory"] = deniedPath;
        _fakeToolExecutor.Receipts["set_working_directory"] =
            new ToolInvocationReceipt(ToolInvocationOutcomeCategory.AccessDenied);

        var nextTurn = await RunToolTurnAndCaptureNextTurnAsync("failed-project-declaration");

        Assert.DoesNotContain(deniedPath, nextTurn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_project_declaration_uses_receipt_not_presentation_text()
    {
        var declaredPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "declared-project"));
        var misleadingPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "misleading-result"));
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                "call-cwd",
                "set_working_directory",
                new Dictionary<string, object?> { ["Path"] = declaredPath })
        ];
        _fakeToolExecutor.Results["set_working_directory"] = misleadingPath;
        _fakeToolExecutor.Receipts["set_working_directory"] = new ToolInvocationReceipt(
            ToolInvocationOutcomeCategory.Success,
            declaredProjectDirectory: declaredPath);

        var nextTurn = await RunToolTurnAndCaptureNextTurnAsync("successful-project-declaration");

        Assert.Contains(declaredPath, nextTurn, StringComparison.Ordinal);
        Assert.DoesNotContain(misleadingPath, nextTurn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_tool_receipt_cannot_declare_project_scope()
    {
        var declaredPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "forged-project"));
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                "call-read",
                "file_read",
                new Dictionary<string, object?> { ["Path"] = "README.md" })
        ];
        _fakeToolExecutor.Results["file_read"] = "content";
        _fakeToolExecutor.Receipts["file_read"] = new ToolInvocationReceipt(
            ToolInvocationOutcomeCategory.Success,
            declaredProjectDirectory: declaredPath);

        var nextTurn = await RunToolTurnAndCaptureNextTurnAsync("forged-project-declaration");

        Assert.DoesNotContain(declaredPath, nextTurn, StringComparison.Ordinal);
    }

    private async Task<string> RunToolTurnAndCaptureNextTurnAsync(string sessionSuffix)
    {
        var sessionId = new SessionId($"console/{sessionSuffix}");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe($"{sessionSuffix}-subscriber");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "select the project"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var previousCalls = _fakeChatClient.ReceivedMessages.Count;
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "what is the project?"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        return string.Join(
            "\n",
            _fakeChatClient.ReceivedMessages[previousCalls].Select(message => message.Text ?? string.Empty));
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
    public Dictionary<string, string> Results { get; } = [];

    public Dictionary<string, ToolInvocationReceipt> Receipts { get; } = [];

    public Dictionary<string, ToolAgentCorrection> Corrections { get; } = [];

    public Action? BeforeCorrection { get; set; }

    /// <summary>Tool names that should throw on execution.</summary>
    public HashSet<string> FailForTools { get; } = [];

    public Task AuthorizeAsync(FunctionCallContent toolCall, Netclaw.Tools.ToolExecutionContext context, CancellationToken ct = default)
    {
        if (Corrections.TryGetValue(toolCall.Name, out var correction))
        {
            BeforeCorrection?.Invoke();
            throw new ToolAgentCorrectionRequiredException(correction);
        }

        if (FailForTools.Contains(toolCall.Name))
            throw new InvalidOperationException($"Tool '{toolCall.Name}' failed (simulated)");

        return Task.CompletedTask;
    }

    public async Task<string> ExecuteAsync(FunctionCallContent toolCall, Netclaw.Tools.ToolExecutionContext context, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _callCount);

        if (Corrections.TryGetValue(toolCall.Name, out var correction))
        {
            BeforeCorrection?.Invoke();
            throw new ToolAgentCorrectionRequiredException(correction);
        }

        if (FailForTools.Contains(toolCall.Name))
        {
            throw new InvalidOperationException($"Tool '{toolCall.Name}' failed (simulated)");
        }

        var result = Results.GetValueOrDefault(toolCall.Name, $"[fake result for {toolCall.Name}]");
        if (Receipts.TryGetValue(toolCall.Name, out var receipt))
            context.Outputs.TryComplete(receipt);
        // Mirror DispatchingToolExecutor's post-processing so integration tests see
        // the same redact + inline-bound + spill the real executor applies. The fake
        // has no tool instance, so it uses the session content budget (per-tool
        // verbose overrides are exercised in DispatchingToolExecutorTests).
        result = Netclaw.Security.SecretOutputRedactor.Redact(result);
        var budget = context.MaxInlineToolResultChars;
        return await Netclaw.Actors.Tools.ToolOutputSpill.BoundAndSpillAsync(
            result, toolCall.CallId, budget, context.Invocation, ct);
    }
}

/// <summary>
/// Fake audit logger that captures entries for verification.
/// </summary>
