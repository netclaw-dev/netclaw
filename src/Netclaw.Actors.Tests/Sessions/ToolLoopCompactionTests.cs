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
using Netclaw.Actors.Tools;
using Xunit;

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
    private readonly FakeToolAuditLogger _fakeAuditLogger = new();

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
            MaxToolCallsPerTurn = 10,
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
        services.AddSingleton<IToolAuditLogger>(_fakeAuditLogger);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
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
}
