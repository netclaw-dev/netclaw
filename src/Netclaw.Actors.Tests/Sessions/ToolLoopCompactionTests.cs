// -----------------------------------------------------------------------
// <copyright file="ToolLoopCompactionTests.cs" company="Petabridge, LLC">
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
using Netclaw.Actors.Tests.Tools;
using Netclaw.Actors.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Regression test for #424: compaction never triggers during tool-loop iterations.
/// Verifies that _lastInputTokenCount is updated from tool-call responses and that
/// ShouldCompact() is checked before firing follow-up LLM calls in the tool loop.
/// </summary>
public class ToolLoopCompactionTests : LlmSessionTestBase
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();

    public ToolLoopCompactionTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 1000,
        });
        services.AddSingleton(new SessionConfig
        {
            MaxToolIterationsPerTurn = 10,
            Tuning = new SessionTuning
            {
                CompactionThreshold = 0.75,
                SnapshotInterval = 5,
                KeepRecentToolResults = 1,
                KeepRecentMessages = 0,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        registry.RegisterCore(
            AIFunctionFactory.Create(() => "deferred_probe", "load_tool"),
            "builtin");
        registry.Register(
            AIFunctionFactory.Create(() => "deferred result", "deferred_probe"),
            "builtin");
        services.AddSingleton(registry);
        services.AddSingleton(TestToolAccessPolicy.Create(new ToolConfig()));
    }

    [Fact]
    public async Task Compaction_triggers_during_tool_loop_when_token_threshold_exceeded()
    {
        // Configure a deterministic two-call sequence:
        // 1. tool call with high usage to trigger compaction during the tool loop
        // 2. plain text with low usage after compaction so the turn completes
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test" })
        ];
        _fakeChatClient.PlannedToolCallDecisions.Enqueue(true);
        _fakeChatClient.PlannedToolCallDecisions.Enqueue(false);
        _fakeChatClient.PlannedUsageOverrides.Enqueue(new UsageDetails
        {
            InputTokenCount = 800, // Exceeds 750 threshold (0.75 * 1000)
            OutputTokenCount = 50,
            TotalTokenCount = 850
        });
        _fakeChatClient.PlannedUsageOverrides.Enqueue(new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 20,
            TotalTokenCount = 120
        });

        var sessionId = new SessionId("test-channel/tool-loop-compaction");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("tool-compact-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TestContext.Current.CancellationToken);

        // The first LLM call returns a tool call (with 800 token usage).
        // After tool execution completes, the actor should detect that
        // _lastInputTokenCount >= threshold and trigger compaction instead
        // of firing another LLM call.
        await subscriber.ExpectMsgAsync<ToolCallOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var compaction = await subscriber.ExpectMsgAsync<CompactionOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(compaction.MessagesAfter < compaction.MessagesBefore,
            "Compaction should have reduced message count");

        // After compaction, the session drains the buffer and completes the turn.
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Exact_cycle_state_survives_successful_compaction()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("cycle-call", "web_search",
                new Dictionary<string, object?> { ["query"] = "same" })
        ];
        _fakeChatClient.PlannedToolCallDecisions.Enqueue(true);
        _fakeChatClient.PlannedToolCallDecisions.Enqueue(true);
        _fakeChatClient.PlannedToolCallDecisions.Enqueue(true);
        _fakeChatClient.PlannedToolCallDecisions.Enqueue(false);
        _fakeChatClient.PlannedUsageOverrides.Enqueue(new UsageDetails { InputTokenCount = 800 });
        _fakeChatClient.PlannedUsageOverrides.Enqueue(new UsageDetails { InputTokenCount = 100 });
        _fakeChatClient.PlannedUsageOverrides.Enqueue(new UsageDetails { InputTokenCount = 100 });
        _fakeChatClient.PlannedUsageOverrides.Enqueue(new UsageDetails { InputTokenCount = 100 });
        _fakeToolExecutor.Results["web_search"] = "same result";

        var sessionId = new SessionId("test-channel/cycle-compaction");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("cycle-compaction-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Repeat a search across compaction."
        }, TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<CompactionOutput>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        var correction = await subscriber.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("repeated action-and-outcome cycle", correction.Result, StringComparison.Ordinal);
        Assert.Equal(2, _fakeToolExecutor.CallCount);
    }

    [Fact]
    public async Task Successful_compaction_preserves_a_loaded_deferred_tool()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("load-call", "load_tool",
                new Dictionary<string, object?> { ["name"] = "deferred_probe" })
        ];
        _fakeChatClient.PlannedToolCallDecisions.Enqueue(true);
        _fakeChatClient.PlannedToolCallDecisions.Enqueue(false);
        _fakeChatClient.PlannedUsageOverrides.Enqueue(new UsageDetails { InputTokenCount = 800 });
        _fakeChatClient.PlannedUsageOverrides.Enqueue(new UsageDetails { InputTokenCount = 100 });
        _fakeToolExecutor.Results["load_tool"] = "deferred_probe";

        var sessionId = new SessionId("test-channel/schema-compaction");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("schema-compaction-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Load a deferred tool before compaction."
        }, TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<CompactionOutput>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("deferred_probe", _fakeChatClient.ReceivedToolNames[^1]);
    }
}
